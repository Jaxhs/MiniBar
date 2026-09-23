/// <summary>
/// 本文件定义宿主能力门面 <see cref="IShellService"/>（插件做不了的事都从这里申请：
/// 开/关面板、迷你模式、通知、浮动窗口、热插拔 DLL 等），以及浮动窗口选项 <see cref="PluginWindowOptions"/>
/// 与浮窗句柄 <see cref="IPluginWindow"/>。
///
/// <para>插件永远通过 <see cref="IPluginContext.Shell"/> 拿到这个门面，而不是直接 new 宿主类型，
/// 这样契约程序集才能保持轻量、且与宿主 UI 解耦。</para>
///
/// <para><b>FrameworkElement 是什么：</b>WPF 里所有能参与界面布局的 UI 元素（按钮、文本、面板……）的共同基类。
/// 这里多处要求插件返回 <see cref="FrameworkElement"/> 而不是某个具体控件，
/// 宿主才能把“任意控件”塞进任务栏或浮窗，而不必关心它到底是什么。</para>
///
/// <para><b>IReadOnlyList&lt;T&gt; 是什么：</b>一种“只能看、不能改”的列表视图（可以读元素、数个数，但不能增删改）。
/// 宿主把列表交给你是为了让你遍历/展示，千万不要试图往里 Add 或 Remove，否则会抛异常。</para>
/// </summary>
using System.Windows;

namespace MiniBar.Sdk;

public enum NotificationKind
{
    /// <summary>普通信息（默认样式）。</summary>
    Info,

    /// <summary>成功（绿色系），例如“已保存”。</summary>
    Success,

    /// <summary>警告（黄色系），例如“配置可能不完整”。</summary>
    Warning,

    /// <summary>错误（红色系），例如“操作失败”。</summary>
    Error,
}

/// <summary>宿主能力门面：插件做不了的事（窗口、热键、通知、热插拔）都从这里申请。</summary>
public interface IShellService
{
    /// <summary>宿主任务栏窗口句柄，可用于 SetWindowPos 之类的自定义定位。</summary>
    IntPtr MainWindowHandle { get; }

    /// <summary>当前是否迷你模式。</summary>
    bool IsMiniMode { get; }

    // ---- 面板（"打开/关闭界面"）----

    /// <summary>打开指定插件的面板（浮层）。</summary>
    void OpenPanel(string pluginId);

    /// <summary>关闭指定插件的面板。</summary>
    void ClosePanel(string pluginId);

    /// <summary>切换指定插件的面板（开↔关）。</summary>
    void TogglePanel(string pluginId);

    /// <summary>关闭当前所有插件面板。</summary>
    void CloseAllPanels();

    // ---- 迷你模式（全屏程序时的小窗口）----

    /// <summary>强制进入迷你模式并指定要显示紧凑内容的插件。</summary>
    void EnterMiniMode(string pluginId);

    /// <summary>退出迷你模式（在检测到全屏时会被下一个检测周期覆盖）。</summary>
    void ExitMiniMode();

    /// <summary>设置迷你模式默认显示的插件。</summary>
    void SetMiniModePlugin(string pluginId);

    // ---- 通知 ----

    /// <summary>弹出一个通知（toast），kind 决定配色，duration 不传则走宿主默认时长。</summary>
    void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);

    // ---- 浮窗 ----

    /// <summary>创建一个宿主托管的浮窗，内容为任意 <see cref="FrameworkElement"/>，出现在屏幕任意位置。</summary>
    IPluginWindow CreateWindow(string title, FrameworkElement content, PluginWindowOptions? options = null);

    // ---- 热插拔 ----

    /// <summary>按 DLL 路径加载插件（等价于把 DLL 拖到任务栏上）。返回新建的插件 ID；已存在时返回 null。</summary>
    string? LoadPlugin(string dllPath);

    /// <summary>卸载插件。<paramref name="deleteFile"/> 为 true 时同时删除插件文件。</summary>
    bool UnloadPlugin(string pluginId, bool deleteFile = false);

    /// <summary>启用/禁用插件（禁用会立即卸载并从任务栏移除，但保留文件与配置）。</summary>
    void SetPluginEnabled(string pluginId, bool enabled);

    /// <summary>
    /// 重新加载插件，等价于插件管理器里的「重新加载」按钮。
    /// 插件自己的设置影响到界面结构时（例如"要不要在任务栏显示读数"）可以调用它重建。
    /// </summary>
    bool ReloadPlugin(string pluginId);

    /// <summary>指定插件当前是否已加载在内存中。</summary>
    bool IsPluginLoaded(string pluginId);

    /// <summary>列出当前所有插件的状态快照（只读），用于插件间协作或诊断。</summary>
    IReadOnlyList<PluginInfo> GetPlugins();

    /// <summary>插件安装目录（用户级可写目录），把 DLL 复制进来即可被自动发现。</summary>
    string UserPluginDirectory { get; }

    /// <summary>把焦点还给任务栏并弹出右键菜单（供键盘/热键场景使用）。</summary>
    void ShowMainMenu();

    /// <summary>打开插件管理器窗口。</summary>
    void ShowPluginManager();

    /// <summary>
    /// 打开宿主设置窗口。宿主设置与所有插件的设置（<see cref="ISettingsPlugin"/>）都在里面，
    /// 传 <paramref name="pluginId"/> 可直接定位到某个插件的设置页。
    /// </summary>
    void ShowSettings(string pluginId = "");
}

/// <summary>创建浮窗时的可选配置（位置、尺寸、置顶、标题栏等）。全部有合理默认值，按需覆盖即可。</summary>
public sealed class PluginWindowOptions
{
    /// <summary>屏幕坐标（像素）。为 null 时由宿主自动居中到鼠标所在显示器。</summary>
    public Point? ScreenPosition { get; set; }

    public double Width { get; set; } = 420;

    public double Height { get; set; } = 320;

    /// <summary>是否置顶（默认 true）。</summary>
    public bool Topmost { get; set; } = true;

    /// <summary>是否显示标题栏（默认 true，可拖动）。</summary>
    public bool ShowTitleBar { get; set; } = true;

    public double? MinWidth { get; set; }

    public double? MinHeight { get; set; }

    public bool Resizable { get; set; } = true;

    /// <summary>关闭时是否询问插件（插件可在 <c>OnClosing</c> 中取消）。</summary>
    public bool HideInsteadOfClose { get; set; }
}

/// <summary>插件持有的一张浮窗句柄。</summary>
public interface IPluginWindow
{
    /// <summary>浮窗当前是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>浮窗里显示的内容（任意 WPF 元素），可随时替换。</summary>
    FrameworkElement Content { get; set; }

    /// <summary>浮窗标题（标题栏显示的文字）。</summary>
    string Title { get; set; }

    /// <summary>显示浮窗。</summary>
    void Show();

    /// <summary>隐藏浮窗（不销毁，可再次 Show）。</summary>
    void Hide();

    /// <summary>把浮窗带到前台并激活（抢焦点）。</summary>
    void Activate();

    /// <summary>关闭并销毁浮窗（释放资源）。</summary>
    void Close();

    /// <summary>窗口关闭（含用户点击关闭按钮）时触发。</summary>
    event EventHandler? Closed;
}
