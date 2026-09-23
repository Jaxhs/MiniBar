/// <summary>
/// 本文件是整个插件系统的“总契约入口”：定义了插件必须实现的根接口 <see cref="IMinibarPlugin"/>，
/// 以及宿主注入给插件的运行环境 <see cref="IPluginContext"/> 与日志接口 <see cref="IPluginLogger"/>。
///
/// <para><b>新手必读 —— 为什么这个程序集（MiniBar.Sdk）必须全进程唯一：</b>
/// 每个插件都运行在自己独立、可回收的 <see cref="System.Runtime.Loader.AssemblyLoadContext"/>（ALC）里，
/// 插件 DLL 用“流式加载”（LoadFromStream）读入，所以宿主不占用插件文件句柄，卸载后能立刻删除文件。
/// 但契约类型（IMinibarPlugin 等）必须让“宿主”和“插件”看到的是同一个类型。
/// 若插件目录里带了 MiniBar.Sdk.dll 副本，插件里的 IMinibarPlugin 与宿主里的就会变成两个不同类型，
/// <c>is IMinibarPlugin</c> 永远为 false，于是插件加载失败。
/// 因此插件工程必须把 MiniBar.Sdk 设为“不复制”的引用（Copy Local = false）。</para>
///
/// <para><b>卸载与 Dispose：</b>插件被禁用/卸载时宿主会调用 <see cref="IDisposable.Dispose"/>。
/// 插件<b>必须在 Dispose 里停掉计时器、断开事件订阅</b>，否则该 ALC 无法被回收，
/// 表现就是“禁用插件后内存不降”。本接口派生自 IDisposable，正是为了强调这一点。</para>
/// </summary>
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

/// <summary>宿主提供的日志接口。插件用它与宿主共用同一个日志通道（文件 / 输出窗），便于排查问题。</summary>
public interface IPluginLogger
{
    /// <summary>调试级：开发期的细节，正式环境通常过滤掉。</summary>
    void Debug(string message);

    /// <summary>信息级：正常的、值得记录的状态变化。</summary>
    void Info(string message);

    /// <summary>警告级：出了点小问题但还能跑，可附带异常。</summary>
    void Warn(string message, Exception? exception = null);

    /// <summary>错误级：功能失败，必须记录，可附带异常。</summary>
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
    /// 任务栏是否以<b>纵向</b>方式停靠（即贴在左/右边缘）。
    ///
    /// <para><b>为什么插件需要知道这个：</b>侧边模式下任务栏只有几十像素宽，
    /// 横向排布的长文本（例如 "C15 M62"、"23:47:45 周三"）会被省略号截断、很难看。
    /// 插件可以据此换用更紧凑的表达：</para>
    /// <list type="bullet">
    ///   <item>时钟：只显示 "HH:mm"；</item>
    ///   <item>系统监视：改成两行 "C15 / M62"；</item>
    ///   <item>久坐提醒："45分" 而不是 "坐45分"。</item>
    /// </list>
    /// <para>宿主在方向变化时会重新调用 <c>CreateBarWidget</c>/<c>CreateCompactContent</c>，
    /// 所以按这个属性决定排版是安全的。</para>
    /// </summary>
    bool IsBarVertical { get; }

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
