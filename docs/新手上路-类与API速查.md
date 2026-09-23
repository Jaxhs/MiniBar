# 新手上路：一眼看不懂的类与 API 速查

这份文档专门解释本项目里出现的**不常见类型**：它是什么、为什么这里要用它、怎么用、新手要注意什么。
按"你在代码里遇到它的位置"分组，可以直接搜类名跳转。

阅读建议：先看 [README 的《代码阅读导览》](../README.md#3-代码阅读导览建议按这个顺序看)，
遇到不懂的类型再回来查这里。

---

## 1. 进程、线程与定时器

### `Mutex`（互斥体）
- **是什么**：跨进程的"锁"。带名字的 Mutex 全系统只有一个，第一个创建的进程"持有"它。
- **本项目用在哪**：`Services/SingleInstance.cs` —— 判断"我是不是第一个启动的实例"。
- **怎么用**：`new Mutex(true, "Global\\MiniBar.SingleInstance", out var createdNew)`，
  `createdNew == false` 就说明已经有实例在跑了。
- **注意**：名字要够独特（一般带公司/产品名）；进程被强杀时 Mutex 会被系统回收，
  所以它比"写一个锁文件"可靠得多（锁文件在崩溃后会留下垃圾）。

### `NamedPipeServerStream` / `NamedPipeClientStream`（命名管道）
> 你点名说看不懂的就是它。简单说：**Windows 自带的、本机进程之间传数据的水管。**

- **是什么**：管道 = 一条字节流；"命名" = 起了名字，别的进程按名字就能找到它。
  服务端负责建管子并等待连接，客户端按同名连上来，然后双方像读写文件一样读写。
- **为什么这里要用它**：把文件拖到 `MiniBar.exe` 上时，Windows 会**新开一个进程**并把它作为命令行参数。
  新进程不能自己开第二条任务栏，于是它要把"用户拖进来的路径"告诉**已经在运行的那个实例**，
  然后自己退出。这就是最典型的"一个进程给另一个进程捎个话"的场景。
- **为什么不用别的**：
  - 临时文件：要处理并发、清理、异常残留，容易出脏数据；
  - 剪贴板：会污染用户的剪贴板，还可能被其它程序覆盖；
  - HTTP/本地端口：要占用端口、可能被杀毒/防火墙拦、开销也大；
  - 命名管道：**系统级、只在本机、按名字寻址、有现成的流式 API**，最合适。
- **最小用法**（本项目 `SingleInstance.cs` 里的形态）：
  ```csharp
  // 服务端（第一个实例，在后台线程循环等待）
  using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
      PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
  await server.WaitForConnectionAsync();          // 阻塞/异步等到有客户端连上来
  using var reader = new StreamReader(server, Encoding.UTF8);
  var text = await reader.ReadToEndAsync();       // 客户端发完就关，这里一次读干净

  // 客户端（第二个实例）
  using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
  client.Connect(timeoutMs);                      // 一定要给超时，否则连不上会一直等
  using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
  writer.Write(payload);
  ```
- **新手注意**：
  1. 管道名不要带 `\\` 前缀（那是 `\\.\pipe\` 的逻辑名，API 会自己加）；
  2. 两端**编码要一致**（这里都用 UTF-8 且不带 BOM，否则中文会乱码）；
  3. 服务端要有**超时或取消**，不然宿主退出时线程可能卡在 `WaitForConnection` 上；
  4. 写完要 `Flush()`/`AutoFlush`，否则数据还在缓冲区里；
  5. 用多个客户端时记得用循环 + 新实例（一根管子一次连接）。

### `AppDomain.CurrentDomain.ProcessExit` / `UnhandledException` / `SessionEnding`
- **是什么**：进程级的事件钩子。`ProcessExit` 在正常退出前触发；`UnhandledException` 在有未捕获异常、
  进程即将终止时触发；`SessionEnding` 在注销/关机时触发。
- **本项目用在哪**：`App.xaml.cs` —— 把 AppBar 占用的屏幕空间、被改动的系统任务栏状态**还回去**。
- **为什么必须要**：这类系统级状态如果不恢复，用户桌面会永久少一条（或者任务栏消失）。
  正常退出走 `OnExit` 就够了，但异常退出/被强杀只有这些钩子能兜住。
- **注意**：`ProcessExit` 里能做的事很有限（时间短、别的线程可能已停），所以只做"一次系统调用"级别的事；
  想在极端情况下更保险，还可以用 `try/finally` 包住主循环。

### `DispatcherTimer` vs `System.Timers.Timer`
| | `DispatcherTimer` | `System.Timers.Timer` |
|---|---|---|
| 回调线程 | **UI 线程**（能直接改控件） | 线程池线程（改控件必须 `Dispatcher.Invoke`） |
| 精度/优先级 | 跟随消息队列，可指定 `DispatcherPriority` | 系统定时器，独立于界面 |
| 典型用途 | 时钟走秒、界面动画、状态刷新 | 后台清理、写日志、空闲裁剪内存 |

- **本项目用在哪**：插件的时钟/监视/久坐提醒用 `DispatcherTimer`；
  `AppLog` 的批量落盘、`App` 的空闲裁剪内存用 `System.Timers.Timer`。
- **坑**：`DispatcherTimer` 在 `ApplicationIdle` 优先级下，界面持续有活时可能**很久不被调度**；
  需要稳定触发就用普通优先级或 `System.Timers.Timer`。

### `Task.Run` / `async` / `await` / `ConfigureAwait`
- **是什么**：`Task` = 一个"将来会完成的工作"；`async/await` 让你用同步写法等它，而不阻塞线程。
- **本项目用在哪**：`App.StartAsync`（启动流程）、`PluginHost` 的目录扫描与程序集探测（读磁盘慢，放后台）。
- **关键点**：`await` 之后默认**回到原来的上下文**（在 WPF 里就是 UI 线程）。
  `ConfigureAwait(true)` 是这个默认行为的显式写法；`ConfigureAwait(false)` 表示"不用回来，随便哪个线程都行"
  （库代码常用，能省一次线程切换）。
- **坑**：`async void` 只能用在事件处理器上（异常无法被 `await` 捕获，会直接抛到 Dispatcher）。

---

## 2. 程序集与插件热插拔

### `AssemblyLoadContext`（ALC，程序集加载上下文）
- **是什么**：.NET 里"加载一个 DLL"= 把它**登记进某个 ALC**。ALC 决定了三件事：
  ①从哪找依赖 ②同名程序集算不算同一个类型 ③以后能不能把它丢掉。
- **本项目用在哪**：`Hosting/PluginLoadContext.cs` —— **每个插件一个 `isCollectible: true` 的 ALC**。
- **为什么**：默认 ALC **永远不能卸载**。想做到"删掉插件真的把内存释放掉"，就必须给每个插件单独建上下文。
- **两条铁律**：
  1. **契约程序集 `MiniBar.Sdk` 必须共享**：插件 ALC 遇到它要 `return null`（交回默认上下文）。
     否则插件里的 `IMinibarPlugin` 与宿主里的会变成两个不同的 `Type`，`is IMinibarPlugin` 恒为 false；
  2. **用流式加载**（`LoadFromStream`）而不是 `LoadFromAssemblyPath`，
     这样进程不占 DLL 文件句柄，卸载后能立刻删除文件。副作用：插件里 `Assembly.Location` 为空。
- **卸载为什么要配合 GC**：`Unload()` 只是"标记可回收"。插件里只要还有活对象
  （静态字段、没停的计时器、没退订的事件），整个上下文就回收不了。
  所以宿主卸载后主动 `GC.Collect()` + `WaitForPendingFinalizers()` 催两轮，
  再 `EmptyWorkingSet` 把内存页还给系统（实测 109MB → 12MB）。
- **给插件作者的推论**：`Dispose()` 里必须停计时器、退订事件、把界面字段置 null。

### `AssemblyDependencyResolver`
- **是什么**：读插件旁边的 `*.deps.json`，按里面的记录帮你找依赖 DLL 的路径。
- **怎么用**：`resolver.ResolveAssemblyToPath(assemblyName)` → 路径字符串（找不到返回 null）。
- **坑**：没有 `deps.json` 时要自己兜底（本项目就退化为"插件目录下的同名 DLL"）。

### `Activator.CreateInstance` / `Assembly.GetTypes` / 反射相关
- **是什么**：运行时"按名字找类型、再创建实例"的能力。
- **本项目用在哪**：`PluginScanner.Probe` 找带 `[PluginManifest]` 且实现 `IMinibarPlugin` 的类型；
  `PluginHost.LoadInstance` 创建插件实例。
- **关键设计**：**探测**（只读元数据）在**临时 ALC** 里做，读完立刻 `Unload()`，只留字符串结果 ——
  这样"这 DLL 是不是插件"的判断不会污染内存，也不需要执行插件代码。
- **坑**：`GetTypes()` 会因为某个类型加载失败而整体抛异常，所以要用 `ReflectionTypeLoadException` 兜底。

### `GC.Collect()` / `WaitForPendingFinalizers()`
- **是什么**：主动请求垃圾回收 / 等待终结器队列跑完。
- **注意**：平时**不要**随便调（会打乱 GC 的自适应策略、影响性能）。这里是有明确目的：
  "用户刚点了禁用/删除，希望内存立刻降下来"，属于合理的少数场景。

---

## 3. Win32 互操作（P/Invoke）

> 这一层是项目里最容易劝退的部分。术语先说清：
> **P/Invoke** = 从托管代码调用 Windows 系统 DLL 里的 C 函数。

### `IntPtr` 与 HWND
- **是什么**：`IntPtr` 是"平台相关宽度的整数"，用来装**指针/句柄**。
  Windows 给每个窗口一个身份证号（HWND），在 C# 里就是 `IntPtr`。
- **坑**：64 位下句柄可能超出 `int` 范围，所以**永远不要**用 `int` 存句柄。
- **本项目**：`WindowInteropHelper(window).Handle` 拿到 WPF 窗口的 HWND；迷你窗口、气泡都靠它做定位。

### `[DllImport]` / `StructLayout` / `Marshal`
- **是什么**：声明外部 C 函数的结构。`StructLayout(LayoutKind.Sequential)` 表示"字段按声明顺序在内存里排"，
  这样 C 那边的结构体才能和我们这边对上。
- **必须做的一件事**：显式声明 `argtypes` / `restype`（尤其在 64 位上），否则返回值会被截断成 32 位而出错。
- **本项目**：`Interop/NativeMethods.cs` 集中放这些声明；`APPBARDATA`/`RECT`/`POINT` 是配套的结构体。

### `WindowInteropHelper` / `HwndSource` / `AddHook`
- **`WindowInteropHelper`**：WPF 是托管世界，要拿原生句柄、设置 `Owner` 都用它。
- **`HwndSource.AddHook`**：可以挂一个"窗口过程钩子"，收到**原生 Windows 消息**
  （`WM_DISPLAYCHANGE` 分辨率变化、`WM_DPICHANGED` 缩放变化、自定义的 AppBar 回调消息……）。
- **本项目**：`BarWindow.OnWndProc`。
- **新手坑（踩过）**：`Loaded` 事件**可能早于** `SourceInitialized`（实测差 177ms），
  那时 `Handle` 还是 0。所以要在构造阶段用 `EnsureHandle()` 主动创建句柄，不要依赖事件顺序。

### `SetWindowPos` 与 `WS_EX_*` 窗口样式
- **`SetWindowPos`**：移动/调整窗口（像素级），参数里有 `HWND_TOPMOST` 表示"放到最上层"。
- **常用扩展样式**：
  | 常量 | 作用 |
  | --- | --- |
  | `WS_EX_TOOLWINDOW` | 不出现在 Alt+Tab 与系统任务栏（本项目任务栏用它） |
  | `WS_EX_NOACTIVATE` | 点击不抢焦点（迷你窗口、气泡用它，这样不会把全屏游戏切出来） |
  | `WS_EX_TOPMOST` | 常驻最上层 |
  | `WS_EX_LAYERED` | 支持逐像素透明（WPF 的 `AllowsTransparency` 会自己加） |
- **为什么本项目定位不用 `Window.Left/Top`**：在 125% 缩放的多显示器上，WPF 的逻辑坐标与物理像素会错位；
  直接 `SetWindowPos` 到物理像素最稳。

### `SHAppBarMessage` + `APPBARDATA`（AppBar）
- **是什么**：向系统申请"占住屏幕某条边缘"的机制 —— **系统任务栏本身就是个 AppBar**。
  注册后 Windows 会把屏幕**工作区**让出一条，于是别的窗口最大化时会自动避开。
- **本项目**：`Interop/AppBarService.cs`。`ABM_NEW` 注册、`ABM_QUERYPOS` 让系统裁位置、
  `ABM_SETPOS` 登记、`ABM_REMOVE` 归还。
- **三条必须记住的规则**：
  1. 隐藏 / 进迷你模式 / 退出**都要 `ABM_REMOVE`**，否则桌面永久少一条（被强杀时系统也会留下残留）；
  2. 自己 `SETPOS` 之后 Shell 会回一次 `ABN_POSCHANGED`（通知的就是你刚做的事），
     必须忽略 1 秒内的通知，否则"设位置→被通知→再设位置"死循环；
  3. `ABN_FULLSCREENAPP` 是系统给的**全屏程序开始/结束**通知，比轮询更快（但只在注册期间有效）。

### `SHAppBarMessage(ABM_SETSTATE)`（系统任务栏自动隐藏）
- **本项目**：`Interop/SystemTaskbar.cs`。`ABS_AUTOHIDE` 就等于勾上"任务栏设置 → 自动隐藏任务栏"。
- **注意**：**不要**用 `ShowWindow(SW_HIDE)` 藏任务栏 —— 窗口看不见了但它申请的占位还在。
  恢复时把 `ABM_GETSTATE` 读到的**原值原样写回**，不要自作聪明换成"看起来合理"的值。

### `GetLastInputInfo`（空闲时长）
- **是什么**：返回"最后一次键鼠操作"的 tick 计数，减一下就是"用户多久没动了"。
- **本项目**：`NativeMethods.GetIdleTime()`，用于 ①久坐提醒判断人是否离开 ②空闲时裁剪内存。
- **注意**：它统计的是**全系统**输入，不区分哪个程序。

### `SHQueryUserNotificationState`（全屏/演示模式）
- **是什么**：问系统"现在是不是在演示模式 / D3D 独占全屏 / 专注模式"。
- **本项目**：`FullscreenWatcher` 的兜底判据（有些游戏用独占全屏，窗口矩形判断不出来）。

### `EmptyWorkingSet` / `SetProcessWorkingSetSize`（内存裁剪）
- **是什么**：把进程的**工作集**（当前驻留在物理内存里的页）标记为可回收，交给系统去换出。
  它**不是**释放内存，只是"先不要占着"；下次访问这些页会再换回来。
- **本项目**：`NativeMethods.TrimWorkingSet()`，在"关闭面板 / 卸载插件 / 空闲 20 秒"时调用 ——
  任务管理器里的内存读数会立刻降下来（实测 109MB → 12MB）。

---

## 4. WPF 界面

### `DataTemplate` / `ItemsControl` / `ItemContainerGenerator`
- **`DataTemplate`**：告诉 WPF"一个数据对象该长什么样"。本项目任务栏图标、插件卡片都是它。
- **`ItemsControl`**：列表容器，配合 `ItemTemplate` 渲染每一项。
- **`ItemContainerGenerator`**：`ItemsControl` 内部为每一项生成的容器（`ContentPresenter`）。
  容器是**异步生成**的，列表刚刷新时可能还没生成完 —— 所以要等 `StatusChanged` 事件再去找容器
  （本项目挂载插件内嵌内容就是这么做的）。
- **坑**：**横向 `StackPanel` 会给子元素无限宽度**，于是 `TextTrimming` 永远不会生效、文字被硬切。
  需要省略号就换成 `Grid`（`*` 列有确定宽度）。

### `ContentControl` / `ContentPresenter`
- **是什么**：`ContentControl` = "内容只有一个子元素"的控件（`Button`、`Window` 都是）；
  `ContentPresenter` 是它用来显示内容的那块占位。
- **本项目**：任务栏图标用 `ContentControl + ContentTemplate="{StaticResource PluginIconTemplate}"`
  统一渲染"图标"这个概念（emoji / 字体图标 / 图片三种形态）。

### `VisualTreeHelper`（视觉树）
- **是什么**：WPF 的界面是一棵树，`VisualTreeHelper.GetParent()` 可以往上找父元素。
- **本项目用在哪**：①判断"用户点的是不是插件自己的控件"（从被点元素往上找 `Tag == "Interactive"`）；
  ②拖拽排序时判断鼠标落在哪个图标上。

### `DynamicResource` vs `StaticResource`
- **`StaticResource`**：加载时解析一次，之后**不会**再变。
- **`DynamicResource`**：每次用到时**现场查**资源字典 —— 所以运行期替换主题字典时，
  所有绑定的颜色会**自动跟着变**。
- **本项目**：所有颜色都用 `DynamicResource`，这就是切换深浅主题时界面立刻变色的原因。

### `ResourceDictionary` / `MergedDictionaries`
- **是什么**：XAML 资源（样式、颜色、模板）的字典。`MergedDictionaries` 可以把多个字典合并进来。
- **本项目**：`ThemeService.Apply()` 把 `Light.xaml` / `Dark.xaml` 插到 `MergedDictionaries` 的**第 0 位**。
- **坑（踩过）**：**自定义主题 + 全局隐式 `TextBlock` 样式 ⇒ 必须自己给
  `ToolTip`/`ComboBox`/`TextBox`/`CheckBox` 写模板**。否则系统默认模板是浅底深字，
  配上你设的近白前景就是"白底白字"（深色主题下"tooltip 一片空白"就是这么来的）。

### `ObservableCollection` / `INotifyPropertyChanged`
- **`ObservableCollection<T>`**：集合增删会**自动通知**界面刷新（本项目插件列表）。
- **`INotifyPropertyChanged`**：对象属性变了通知界面（本项目 `ObservableObject` 基类）。
- **手写 `RaisePropertyChanged()` 为什么要 `[CallerMemberName]`**：编译器自动填上调用者的属性名，
  省得手写字符串写错（写错了绑定的属性永远不刷新，还很难查）。

### `IValueConverter`（值转换器）
- **是什么**：XAML 里不方便写 `if`，就把"值的转换"独立成一个小类，
  在绑定里写 `{Binding X, Converter={StaticResource BoolToVisibility}}`。
- **本项目**：`Converters.cs`（bool → Visibility、字符串非空 → Visibility、图标 → 图片源）。

### `Dispatcher.BeginInvoke` / `DispatcherPriority`
- **是什么**：把一段代码"排队到 UI 线程稍后执行"。
- **为什么要用它**：①从后台线程改界面（WPF 控件有线程亲和性，直接改会抛异常）；
  ②把"稍后再说"的事情延后，避免在事件处理中间做重活。
- **优先级**：`Background`（界面空了才做）、`ApplicationIdle`（更晚）、`Normal`（默认）。

### `Mouse.Capture` / `MouseButtonEventArgs`
- **`Mouse.Capture(element)`**：把鼠标"抓住"，这样鼠标移出窗口后仍能收到移动/松开事件
  （拖拽排序必须用）。
- **`MouseButtonEventArgs`**：带 `ChangedButton`（哪个键）与 `ClickCount`（连击次数）。

### `Effect` / `DropShadowEffect`
- **是什么**：WPF 的像素级效果（阴影、模糊）。**代价**：被加效果的元素会被渲染到一张离屏位图，
  面积越大越费；所以任务栏在"贴边占位"模式下把边距收小、阴影面积也随之变小。

### `WindowChrome`
- **是什么**：WPF 提供的"自绘标题栏"支持：`CaptionHeight`（哪一块当标题栏可以拖动）、
  `ResizeBorderThickness`（多宽算边框可拉伸）。
- **本项目**：设置窗口用它做无边框 + 可拖拽 + 可缩放。
- **坑**：标题栏里的按钮要加 `WindowChrome.IsHitTestVisibleInChrome="True"`，否则点击会被当成拖动。

### `SizeToContent` / `UseLayoutRounding` / `SnapsToDevicePixels`
- **`SizeToContent`**：窗口按内容自动决定大小。本项目**面板浮层**用它做"内容多高就多高"，
  再用 `MaxHeight` 限制上限，超出就滚动。
- **`UseLayoutRounding` + `SnapsToDevicePixels`**：让边框落在整数像素上，避免 125% 缩放下出现模糊/虚边。

### `pack://` URI
- **是什么**：WPF 的资源地址协议。`pack://application:,,,/UI/Themes/Dark.xaml` 表示
  "打进本程序集里的资源"。本项目加载主题字典、插件图标都用它。

---

## 5. 数据、文件与杂项

### `JsonSerializer` / `JsonSerializerOptions`
- **是什么**：.NET 自带的 JSON 序列化（本项目刻意不引第三方库）。
- **本项目**：配置（`settings.json`）、插件状态（`plugins.json`）都用它；`WriteIndented = true` 让人能直接改文件。
- **注意**：**给属性改名/删属性会让老配置文件里的值被忽略**（这里靠"属性缺失就用默认值"来兼容）。

### `FileShare` / `FileStream`
- **是什么**：打开文件时声明"允许别人同时干什么"。
- **本项目**：`new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`
  —— 允许别人写、也允许别人删，这样才能做到"插件正在加载时也不锁文件"。

### `FileSystemWatcher`
- **是什么**：监视目录变化（新增/删除/改名）并触发事件。
- **本项目**：插件目录里 DLL 变了就自动热加载/卸载。
- **坑**：①事件可能**连续触发多次**（一次保存可能引发多个事件）→ 需要防抖/合并；
  ②回调在**线程池线程**上 → 碰界面要 `Dispatcher`；③它不监视子目录（除非设 `IncludeSubdirectories`）。

### `Process` / `ProcessStartInfo`
- **是什么**：启动外部程序（本项目用来打开资源管理器/日志/任务管理器）。
- **注意**：在 .NET Core 及以上，想用系统默认程序打开文件/文件夹，要设 `UseShellExecute = true`。

### `Clipboard` / `SystemSounds`
- **`Clipboard`**：读写系统剪贴板（便签"复制全部"、时钟"复制时间"）。
- **`SystemSounds`**：系统提示音（久坐提醒响一下），不需要额外的音频文件。
- **注意**：剪贴板可能被别的程序占用而抛异常，要 try/catch。

### `StringComparison` / `StringComparer`
- **是什么**：比较字符串时的规则。`OrdinalIgnoreCase` = 不区分大小写的逐字节比较，最快也最稳。
- **为什么本项目到处用它**：插件 ID、路径比较如果用文化相关的比较，在某些语言环境下会出现
  "看起来一样却不相等"的诡异 bug。

### `HashSet<T>` / `Dictionary<TKey, TValue>`
- **`HashSet`**：只关心"在不在"的集合（查重、白名单），查找是 O(1)。
- **`Dictionary`**：键值映射（本项目存"插件 ID → 状态"）。
- **坑**：默认用对象哈希，涉及字符串/路径时记得传 `StringComparer.OrdinalIgnoreCase`。

### `IEnumerable<T>` 与 `yield return`
- **是什么**：`yield return` 让方法变成"按需一个个产出元素"的迭代器，不用先造好整个列表。
- **本项目**：插件的 `GetMenuEntries()` / `GetHotkeys()` / `GetSettingsSections()` 都这么写 ——
  调用一次才产出几条，简单又省内存。

### `record` / `record struct` / `required` / `init` / 可空引用
- **`record`**：语法糖，自动生成相等性比较与 `ToString()`（本项目 `PluginInfo` 用它当"只读数据"）。
- **`record struct`**：值类型的 record（本项目 `FullscreenState`），小数据量拷贝比重更划算。
- **`required` / `init`**：创建时必须赋值 / 只能在初始化时赋值（配置类用起来很稳）。
- **可空引用 `string?`**：编译期提醒"这个值可能是 null"。本项目开启了这个检查，
  所以你会看到大量 `?? 默认值` 与 `is not null` 判断 —— 它们不是啰嗦，而是真的在防崩溃。

### `Func<T>` / `Action<T>` / `EventHandler`
- **`Func<...>`**：有返回值的委托（"一段可传递的代码"）；**`Action<...>`**：无返回值。
- **`EventHandler`**：标准事件签名 `(object? sender, EventArgs e)`。
- **本项目**：`PluginSettingItem` 用 `Func<object?>` / `Action<object?>` 让插件把"怎么读怎么写"塞给宿主，
  于是宿主不需要知道插件是怎么存配置的 —— 这是"统一设置模型"的关键。

---

## 6. 本项目的"自造词"速查

| 词 | 含义 |
| --- | --- |
| **宿主 / Host** | `MiniBar.App`，提供任务栏与插件运行的"地基" |
| **契约 / Contract** | `MiniBar.Sdk`，宿主与插件唯一共享的程序集 |
| **能力 / Capability** | 插件可选实现的接口（8 个）：图标、内嵌内容、面板、迷你内容、菜单、热键、拖放、设置 |
| **描述符 / `PluginDescriptor`** | 宿主眼里的"一个插件"：元数据 + 运行状态 + 界面绑定的对象 |
| **候选 / `PluginCandidate`** | 探测阶段的产物：只有字符串信息（id/名称/图标/能力），不含任何插件类型 |
| **探测 / Probe** | 在临时 ALC 里只读元数据，判断"这 DLL 是不是合法插件" |
| **内嵌内容 / Widget** | 插件画在任务栏显示区里的自定义界面（时钟读数、CPU 读数） |
| **面板 / Flyout** | 点图标弹出的浮层，内容是插件提供的 |
| **迷你模式** | 有别的程序全屏时，任务栏隐藏、只留一个小窗显示插件的紧凑内容 |
| **AppBar** | 向系统申请占用屏幕边缘的机制（系统任务栏也是 AppBar） |
| **热插拔** | 运行期加载/卸载插件，不重启程序 |
| **`ClickHandled`** | 插件"我已经自己处理了这次点击"的标记，宿主据此决定要不要套用默认行为 |
| **`Tag = "Interactive"`** | 插件声明"这块元素我自己处理点击，宿主别当成图标点击" |
| **重复文件** | 同一插件 id 存在两份（内置目录 + 用户目录）时，非胜出的那一份 |

---

## 7. 遇到不懂的东西时的排查套路

1. **看文件头注释**：本项目每个文件开头都写了"这个文件负责什么、涉及哪些概念"。
2. **看调用方**：按 `F12`（转到定义）之外，更有效的是**找谁在调用它**（VS 里右键 → 查找所有引用）。
3. **看日志**：`%APPDATA%\MiniBar\logs\minibar.log`。
   把 `settings.json` 里的 `VerboseLogging` 改成 `true` 能打开 Debug 级日志，信息量翻倍。
4. **看插件管理器**：每个插件的状态、能力、文件路径、加载错误都在设置窗口的「插件管理」页里。
5. **改一改试试**：本项目每个功能都做了"验证过"的注释（例如"实测 1920×1080 上工作区 1042 → 967"），
   改动后可以用 `Interop` 里的方法自己打印数值核对。
