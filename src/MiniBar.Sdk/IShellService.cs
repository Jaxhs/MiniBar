using System.Windows;

namespace MiniBar.Sdk;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
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

    void OpenPanel(string pluginId);

    void ClosePanel(string pluginId);

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

    void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);

    // ---- 浮窗 ----

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

    bool IsPluginLoaded(string pluginId);

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
    bool IsVisible { get; }

    FrameworkElement Content { get; set; }

    string Title { get; set; }

    void Show();

    void Hide();

    void Activate();

    void Close();

    /// <summary>窗口关闭（含用户点击关闭按钮）时触发。</summary>
    event EventHandler? Closed;
}
