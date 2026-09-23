# MiniBar — C# WPF (.NET 8) 可扩展任务栏空壳

一个「什么插件都没有也能跑」的任务栏外壳：**所有功能都以 DLL 形式热插拔**——
运行时加载、禁用、重新加载、删除，不需要重启程序。插件的图标像系统任务栏的任务按钮一样
排在上面，可以固定、拖拽排序、打开/关闭界面；有别的程序全屏时自动让位成迷你窗口。

---

## 1. 需求 → 实现对照

| 你的要求 | 实现位置 | 说明 |
| --- | --- | --- |
| **像任务栏一样占据一定的屏幕空间** | `Interop/AppBarService.cs` | 用 Shell 的 `SHAppBarMessage`（`ABM_NEW`/`ABM_QUERYPOS`/`ABM_SETPOS`/`ABM_REMOVE`）注册成系统 AppBar —— 和系统任务栏是同一套机制。注册后 Windows 会把**屏幕工作区**让出一条（实测 1920×1080 上工作区高度 1042 → 967），其它窗口最大化时会自动避开，不是靠置顶硬顶。隐藏/进迷你模式/退出前一定会 `ABM_REMOVE` 把空间还回去。也可以在设置里关掉，退回悬浮胶囊模式 |
| 常驻显示，其他窗口最大化时不遮挡 | 同上 + `Interop/TopmostGuard.cs` | AppBar 保证工作区不被覆盖；再叠一层 `TOPMOST` + 每 3 秒重申，防止被别的置顶窗口/安装器挤下去 |
| 其他程序全屏时自动转为迷你窗口，显示指定插件的内容 | `Interop/FullscreenWatcher.cs`、`UI/MiniWindow.xaml` | 两条腿：① AppBar 的 `ABN_FULLSCREENAPP` 系统通知（立即响应）② 700ms 轮询前台窗口矩形是否铺满整块显示器（校正，含无边框全屏）+ `SHQueryUserNotificationState` 兜底。进入时隐藏任务栏并归还空间，迷你窗口带 `WS_EX_NOACTIVATE`：点它不会把全屏程序切走 |
| **宿主设置界面，可读取插件的设置** | `UI/SettingsWindow.xaml`、`Services/HostSettingsProvider.cs`、`MiniBar.Sdk/ISettingsPlugin` | 左侧是「宿主设置 + 每个插件」的导航，右侧渲染 `PluginSettingsSection` 卡片。插件实现 `ISettingsPlugin` 就能把自己的设置项挂上来（文本/开关/数值/下拉/路径选择/按钮/说明），宿主负责渲染与读写并即时生效；**宿主自己的设置用的是同一套模型**，所以两边长得完全一样。入口：`Ctrl+Alt+,` / 右键任务栏 → 设置… / `Ctrl+Alt+P` 插件管理器里每个插件的齿轮按钮 |
| 插件图标像任务栏任务图标一样显示 | `UI/BarWindow.xaml(.cs)`、`ViewModels/BarViewModel.cs` | `ItemsControl` + `WrapPanel`，支持 4 条屏幕边缘（底/顶/左/右）自动换向 |
| 可固定、排序 | `Hosting/PluginHost.cs`、`Hosting/PluginStateStore.cs` | 固定顺序持久化在 `plugins.json`；拖拽图标实时重排，松手即保存 |
| 打开关闭界面 | `UI/FlyoutWindow.xaml`、`ShellService.OpenPanel/TogglePanel` | 点图标切换该插件的面板（任务栏式语义）；面板内容在关闭时立即释放 |
| DLL 热插拔（加载/禁用/删除） | `Hosting/PluginLoadContext.cs`、`PluginHost.cs`、`PluginScanner.cs` | 每个插件一个可回收 `AssemblyLoadContext`；程序集**按字节流加载**，不锁文件，卸载后立刻可删。同一插件装了两份时按修改时间新的胜出，另一份标记为"重复文件"并在插件管理器里可一键删除 |
| 在显示区域显示自定义内容 | `MiniBar.Sdk/IBarWidgetPlugin` | 插件可以把任意 WPF 元素直接嵌进任务栏（示例：时钟读数、CPU/内存读数） |
| 在右键菜单中添加项 | `MiniBar.Sdk/IContextMenuPlugin` | 可挂到「任务栏空白处 / 图标上 / 迷你窗口 / 插件管理器」四个位置 |
| 响应快捷键执行命令 | `MiniBar.Sdk/IHotkeyPlugin`、`Interop/HotkeyManager.cs` | 宿主统一 `RegisterHotKey`，冲突自动回传插件 |
| 插件可出现在任意位置 | `IShellService.CreateWindow`、`IContextMenuPlugin`、`IBarWidgetPlugin`、`ISettingsPlugin` | 面板 / 迷你窗口 / 浮窗 / 任意菜单位置 / 设置页面，全部由插件声明 |
| 拖拽文件或文件夹到程序上操作 | `BarWindow.OnFileDrop`、`MiniWindow`、`PluginManagerWindow`、`ShellService.HandleDrop` | 先问被拖到图标上的插件 → 再按顺序问其它拖放插件 → 都没人处理则走内置兜底 |
| 拖入插件 DLL 直接加载 | `PluginHost.LoadPluginFile` | 自动复制到用户插件目录再加载（绝不留住外部文件句柄、也绝不误删用户自己的文件）；拖入**更新版本**会直接替换旧版本 |
| 低内存占用 | 见下文第 7 节 | 实测：空壳 + 4 个插件常驻 **28~48 MB**，空闲裁剪后可降到 **12 MB** 量级 |
| 让系统任务栏自动隐藏 | `Interop/SystemTaskbar.cs` + 设置里的开关 | 用 Shell 自己的 `ABM_SETSTATE + ABS_AUTOHIDE`（就是"任务栏设置 → 自动隐藏任务栏"背后的调用），不是硬藏窗口 —— 所以占位会一起收掉，而且退出时能恢复用户原设置 |
| 面板内容过多时不截断 | `UI/FlyoutWindow.xaml(.cs)` | 浮层高度改成"内容自适应 + 上限为屏幕可用高度"，超出部分出现滚动条；文字放不下时用 `…` 收尾并在悬停提示里给出全文 |

---

## 2. 快速开始

```bash
# 需要 .NET 8 SDK（本仓库 global.json 已锁定 8.0.406）
dotnet build MiniBar.sln -c Debug
src/MiniBar.App/bin/Debug/MiniBar.exe
```

启动后屏幕底部中央会出现一条任务栏，插件 DLL 已经躺在 `src/MiniBar.App/bin/Debug/Plugins/`。

**插件开发时的迭代循环**：改完插件代码 → `dotnet build plugins/MiniBar.Plugin.XXX` →
把新 DLL 拷进插件目录（或直接拖到任务栏上）→ 宿主自动探测、重新加载，**不需要重启宿主**。
也可以直接在运行中的宿主上点插件的「重新加载」按钮。

### 触达插件目录的三种方式

| 目录 | 用途 |
| --- | --- |
| `{程序目录}\Plugins` | 随程序分发的内置插件 |
| `%LOCALAPPDATA%\MiniBar\Plugins` | 用户插件目录，往这里丢 DLL 就会被自动发现（始终可写） |
| 直接拖拽 | 把 DLL 拖到任务栏 / 迷你窗口 / 插件管理器上 |

配置与数据：`%APPDATA%\MiniBar\`（`settings.json`、`plugins.json`、`data\{插件ID}\`、`logs\minibar.log`）。

---

## 3. 代码阅读导览（建议按这个顺序看）

想把它当学习材料的话，这个顺序最省力 —— 每个文件的开头都写了大段注释讲"为什么"：

| 顺序 | 文件 | 你会学到 |
| --- | --- | --- |
| 1 | `src/MiniBar.App/App.xaml.cs` | 一个 WPF 程序的启动顺序为什么必须这样排；单实例转发；系统资源的退出兜底 |
| 2 | `src/MiniBar.Sdk/IMinibarPlugin.cs`、`IShellService.cs` | 宿主与插件之间的"契约"长什么样；为什么要有一个独立的契约程序集 |
| 3 | `src/MiniBar.App/Hosting/PluginLoadContext.cs` | **AssemblyLoadContext**：热插拔的技术地基；共享程序集与流式加载两条铁律 |
| 4 | `src/MiniBar.App/Hosting/PluginHost.cs` | 扫描 → 探测 → 加载 → 卸载的完整生命周期；探测为什么不锁文件；卸载为什么要催 GC |
| 5 | `src/MiniBar.App/Interop/AppBarService.cs` | **AppBar**：怎么像任务栏一样"占住"屏幕边缘，以及必须成对释放的理由 |
| 6 | `src/MiniBar.App/Interop/DisplayService.cs` | DPI 与坐标空间：为什么定位窗口要用像素级 `SetWindowPos` |
| 7 | `src/MiniBar.App/UI/BarWindow.xaml.cs` | 任务栏窗口：点击 / 中键 / 拖拽排序 / 拖放 / WndProc 钩子 |
| 8 | `src/MiniBar.App/UI/FlyoutWindow.xaml.cs` | 面板浮层：为什么"失去焦点就关"要有例外，尺寸上限怎么算 |
| 9 | `src/MiniBar.App/UI/SettingsWindow.xaml.cs`、`Services/HostSettingsProvider.cs` | 一套 UI 同时渲染"宿主设置"和"插件设置"的做法 |
| 10 | `plugins/MiniBar.Plugin.SedentaryReminder/` | 一个功能完整的插件范例（7 种能力都用上了，注释非常啰嗦） |

几个"踩过的坑"都写在对应文件的注释里，遇到看不懂的地方可以搜关键字：
`Loaded 早于 SourceInitialized`、`白底白字`、`ClickHandled`、`ABM_REMOVE`、`防回环`。

---

## 4. 目录结构

```
MiniBar.sln
├─ src/MiniBar.Sdk/            插件契约（宿主与插件唯一共享的程序集，约 700 行）
│   ├─ IMinibarPlugin.cs       入口 + IPluginContext（宿主注入的运行环境）
│   ├─ IShellService.cs        宿主能力门面：开面板/迷你模式/通知/浮窗/热插拔
│   ├─ PluginManifestAttribute.cs
│   ├─ Capabilities/           8 个能力接口，插件按需实现
│   └─ Models/                 图标、主题、菜单项、快捷键、拖放上下文……
├─ src/MiniBar.App/            宿主（约 6800 行）
│   ├─ Hosting/                插件发现 / 加载 / 卸载 / 状态持久化 ← 热插拔核心
│   ├─ Interop/                置顶、全屏检测、全局热键、显示器、工作集裁剪
│   ├─ Services/               面板与迷你模式编排、主题、单实例、日志
│   ├─ UI/                     任务栏窗口、浮层、迷你窗口、通知、浮窗、插件管理器、主题
│   └─ ViewModels/
└─ plugins/                    5 个示例插件，同时也是开发范例
    ├─ MiniBar.Plugin.Clock          全部 7 种能力都用到（含 4 时区面板）
    ├─ MiniBar.Plugin.SystemMonitor  零依赖读 Win32 采样 CPU/内存，演示徽标刷新
    ├─ MiniBar.Plugin.QuickNotes     面板里放文本框，自动落盘；认领 .txt/.md 拖放
    ├─ MiniBar.Plugin.QuickLaunch    只靠菜单/热键/拖放工作，演示"没有图标也能存在"
    └─ MiniBar.Plugin.SedentaryReminder  久坐提醒：注释最详细的一个，适合照着写自己的插件
```

---

## 5. 架构

```
┌────────────────────────────── MiniBar.exe (宿主) ──────────────────────────────┐
│  BarWindow ──► FlyoutWindow / MiniWindow / FloatingWindow / PluginManager       │
│      ▲                                                                          │
│      │ 绑定（PluginDescriptor）                                                  │
│  PluginHost ──┬── PluginScanner    只读元数据探测，探测完立刻卸载探测上下文      │
│               ├── PluginLoadContext × N  每个插件一个可回收 ALC                  │
│               └── PluginStateStore 固定顺序 / 启用状态持久化                     │
│      │                                                                          │
│  ShellService（IShellService 的唯一实现）──► 通知所有插件：面板、迷你、浮窗、菜单 │
└─────────────────────────────────────────────────────────────────────────────────┘
                  ▲ 只通过 MiniBar.Sdk 里的接口交互（双向边界清晰）
   ┌──────────────┴──────────────┬──────────────────┬──────────────────┐
   │ 插件 DAC（可回收 ALC #1）   │  ALC #2          │  ALC #3          │ …
   └─────────────────────────────┴──────────────────┴──────────────────┘
```

### 热插拔是怎么做到「能删掉」的

1. **可回收 ALC**：每个插件 `new PluginLoadContext(path) { IsCollectible = true }`。
2. **流式加载**：主程序集与托管依赖都用 `LoadFromStream`（而不是 `LoadFromAssemblyPath`），
   所以宿主持有的文件句柄为零 —— 这是卸载后能立刻删除文件的前提。
3. **契约必须共享**：`MiniBar.Sdk` 一律 `return null` 交回默认上下文解析。否则插件里的
   `IMinibarPlugin` 与宿主里的会是两个不同的 `Type`，`is IMinibarPlugin` 恒为 false。
   → 因此插件项目**不要**把 `MiniBar.Sdk.dll` 复制到插件目录（示例 csproj 用 `Private="false"`）。
4. **认真卸载**：`Dispose()` → 清空所有引用 → `alc.Unload()` → 两轮 `GC.Collect + WaitForPendingFinalizers`
   → 再 `EmptyWorkingSet` 把页还给系统。
5. **删除只发生在自己的目录里**：只允许删除两个插件目录内的文件；拖进来的外部 DLL 会先被
   复制到用户插件目录，所以永远不会误删用户自己的文件。

### 线程模型

* 文件枚举与程序集探测：后台线程（只读元数据，不碰 UI）；
* 插件实例创建、`Initialize`、所有能力回调、`ObservableCollection` 增删：UI 线程；
* 空闲裁剪内存：线程池定时器（`DispatcherTimer` 在 `ApplicationIdle` 优先级下可能长期不被调度）。

---

## 6. 插件开发

只要三步：建一个 `net8.0-windows` 类库 → 引用 `MiniBar.Sdk`（`Private="false"`）→ 写一个类。

```csharp
[PluginManifest("my.first", "我的插件", Icon = "emoji:🎯", DefaultPinned = true)]
public sealed class FirstPlugin : IMinibarPlugin, ITaskButtonPlugin, IPanelContentPlugin
{
    private IPluginContext _context = null!;

    public void Initialize(IPluginContext context) => _context = context;
    public void Dispose() { }

    public PluginIcon Icon => "emoji:🎯";
    public string DisplayName => "我的插件";
    public void OnClick(BarItemClickContext context) => context.TogglePanel();

    public string PanelTitle => "我的插件";
    public FrameworkElement CreateContent(IPanelHost host) => new TextBlock { Text = "你好，MiniBar" };
}
```

能力接口一览（想要什么位置就实现什么接口，任意组合）：

| 接口 | 作用 | 关键点 |
| --- | --- | --- |
| `ITaskButtonPlugin` | 任务栏图标 | 图标 / 名称 / 徽标 / 提示；`ManagesOwnActivation = true` 可接管点击语义 |
| `IBarWidgetPlugin` | **直接在显示区渲染任意 UI** | 返回的元素会被嵌进任务栏；`ReleaseBarWidget` 里停掉计时器 |
| `IPanelContentPlugin` | 点图标弹出的面板 | 每次打开创建、关闭销毁（低内存的关键约定） |
| `ICompactContentPlugin` | 迷你模式的紧凑内容 | 有其他程序全屏时唯一可见的东西，要极简、不抢焦点 |
| `IContextMenuPlugin` | 往 4 处右键菜单加项 | 宿主会自动收集并合并分隔线 |
| `IHotkeyPlugin` | 全局快捷键 | 宿主统一注册/注销，冲突会回调 `OnHotkeyRegistrationFailed` |
| `IDropHandlerPlugin` | 处理拖入的文件/文件夹 | `CanHandle` 要快；设置 `context.Handled = true` 表示"我处理了" |

细节（生命周期约定、图标语法、主题取色、常见坑）见 **[docs/插件开发指南.md](docs/插件开发指南.md)**。

---

## 7. 低内存设计（实测）

| 措施 | 效果 |
| --- | --- |
| 只用 `EnumDisplayMonitors`，不引用 WinForms | 省掉整个 `System.Windows.Forms` 程序集 |
| 手写 30 行 MVVM 基类，不引 CommunityToolkit / DI 容器 | 少两个依赖、少一份启动开销 |
| 禁用的插件**完全不加载** | 禁用后插件内存归零，文件与配置保留 |
| 面板 / 浮层 / 迷你窗口全部按需创建，内容关闭即释放 | 空闲时只剩任务栏与固定插件 |
| 图标图片按 `DecodePixelWidth=32` 解码并缓存（≤64 项） | 避免把原图整张读进内存 |
| 卸载插件后 `GC.Collect×2` + `EmptyWorkingSet` | 任务管理器里的内存读数立刻下降 |
| 空闲 20s 后自动归还工作集（可在菜单里关闭） | 常驻占用进一步下探 |

实测（1920×1080 / 125% 缩放，宿主 + 4 个插件）：

```
启动 4 秒后            48.5 MB
打开插件面板           76.9 MB
关闭面板（自动裁剪）   27.9 MB   ← EmptyWorkingSet 只把页标记为可回收，代价接近零
空闲 25 秒后           35.5 MB   （时钟/监视的计时器触发后小幅回升，再被周期裁剪）
纯空壳（无插件）       ~12 MB 量级
```

---

## 8. 交互速查

| 操作 | 行为 |
| --- | --- |
| 左键点图标 | 打开 / 关闭该插件的界面（插件的 `OnClick` 仍会被调用，可做附加动作） |
| 中键点图标 | 关闭该插件界面（同时回调 `OnAuxClick`） |
| 右键点图标 | 图标菜单：打开/关闭、用作迷你内容、固定、移到最前/最后、插件自己的菜单项、重新加载、禁用、卸载并删除…… |
| 右键点空白处 | 主菜单：插件管理、迷你模式、任务栏位置（4 边）、主题、插件自己的菜单项、打开目录、退出 |
| 拖拽固定图标 | 实时重排，松手即持久化 |
| 拖入文件/文件夹 | 图标上 → 该插件优先；空白处 → 按安装顺序询问所有拖放插件；DLL → 直接热加载 |
| `Ctrl+Alt+M` | 进入 / 退出迷你模式（全屏时也会自动进入） |
| `Ctrl+Alt+,` | 打开设置窗口（宿主设置 + 所有插件的设置） |
| `Ctrl+Alt+B` | 打开 / 关闭久坐提醒面板（示例插件注册的） |
| `Ctrl+Alt+H` | 显示 / 隐藏任务栏 |
| `Ctrl+Alt+P` | 打开插件管理器 |
| `Ctrl+Alt+T` / `Ctrl+Alt+N` | 示例插件注册的快捷键（时钟 / 便签） |
| `Ctrl+Alt+1~5` | 示例插件「快捷启动」的第 N 项 |

---

## 9. 已知边界

* **契约程序集不能随插件复制**（见 4.3）。插件目录里只应有插件自己的 DLL。
* 插件只能引用 `MiniBar.Sdk`，引用宿主内部类型会在加载时抛异常（宿主内部类型均为 `internal`）。
* 快捷键被别的程序占用时会注册失败，宿主会把原因回传插件（示例插件会记日志/弹提示）。
* 迷你窗口默认 `WS_EX_NOACTIVATE`（不抢全屏程序的焦点）；需要交互的紧凑内容可在
  `settings.json` 里把 `MiniWindowAcceptsFocus` 设为 `true`。
* 单实例：第二个实例（例如把文件拖到 exe 上）会把路径通过命名管道转发给已运行的实例后退出，
  因此“拖到程序上”在两种入口下行为一致。
* 全屏判定以「前台窗口铺满整块显示器」为准；某些无边框全屏游戏若用的是窗口化全屏，
  宿主会依赖 `SHQueryUserNotificationState` 兜底（`TreatQuietHoursAsFullscreen` 可选开启）。
