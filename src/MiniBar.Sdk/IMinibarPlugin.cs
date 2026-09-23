using System.Windows;

namespace MiniBar.Sdk;

/// <summary>
/// 所有插件的入口。实现类必须标注 <see cref="PluginManifestAttribute"/>，且必须有一个公共无参构造函数。
/// 除本接口外，插件通过实现若干“能力接口”（<c>Capabilities</c> 目录）来声明自己要参与哪些位置。
/// </summary>
public interface IMinibarPlugin : IDisposable
{
    /// <summary>
    /// 初始化。始终在 UI 线程调用，且在插件被启用后、任何能力回调之前。
    /// 不要在这里做重活，耗时初始化请异步并自行同步状态。
    /// </summary>
    void Initialize(IPluginContext context);
}

public interface IPluginLogger
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

/// <summary>宿主注入给插件的运行环境。</summary>
public interface IPluginContext
{
    /// <summary>清单里的 <see cref="PluginManifestAttribute.Id"/>。</summary>
    string PluginId { get; }

    /// <summary>插件 DLL 所在目录（可能只读）。</summary>
    string PluginDirectory { get; }

    /// <summary>插件专属可写数据目录，宿主保证存在。</summary>
    string DataDirectory { get; }

    IPluginLogger Logger { get; }

    /// <summary>当前主题。主题变化时触发 <see cref="ThemeChanged"/>。</summary>
    PluginTheme Theme { get; }

    /// <summary>宿主能力（开面板、浮动窗口、通知、热插拔等）。</summary>
    IShellService Shell { get; }

    /// <summary>是否处于迷你模式（有其他程序全屏）。</summary>
    bool IsMiniMode { get; }

    /// <summary>
    /// 用户已经多久没有键鼠操作了（宿主调用 Win32 <c>GetLastInputInfo</c> 得到，开销极小）。
    /// 典型用途：久坐提醒要区分"人在座位上"和"人离开了" —— 离开就不要计时，也不该弹提醒。
    /// </summary>
    TimeSpan UserIdleTime { get; }

    /// <summary>主题变化通知。</summary>
    event EventHandler? ThemeChanged;

    /// <summary>
    /// 请求宿主重新读取本插件的图标 / 徽标 / 提示文本。
    /// 图标内容变化时（例如时钟的秒针、未读数）调用它即可，宿主会合并同一帧内的多次请求。
    /// </summary>
    void InvalidateBarItem();

    /// <summary>读取插件私有配置（持久化到宿主配置文件中，按插件 ID 隔离）。</summary>
    T? GetSetting<T>(string key, T? defaultValue = default);

    /// <summary>写入插件私有配置。</summary>
    void SetSetting<T>(string key, T? value);

    /// <summary>弹出插件自己的浮动窗口（内容是任意 WPF 元素，出现在屏幕任意位置）。</summary>
    IPluginWindow CreateWindow(string title, FrameworkElement content, PluginWindowOptions? options = null);
}
