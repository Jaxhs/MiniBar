using System.IO;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.Services;

/// <summary>
/// 组合根。刻意使用极简的静态定位器而不是 DI 容器：
/// 宿主对象图很小且生命周期与进程一致，少一个容器就少一份内存与启动开销。
/// </summary>
public static class AppServices
{
    public static SettingsService Settings { get; set; } = null!;

    public static ThemeService Theme { get; set; } = null!;

    public static PluginHost Plugins { get; set; } = null!;

    public static ShellService Shell { get; set; } = null!;

    public static HotkeyManager Hotkeys { get; set; } = null!;

    public static TopmostGuard? Topmost { get; set; }

    public static FullscreenWatcher? Fullscreen { get; set; }

    /// <summary>
    /// 把本进程的工作集还给系统。EmptyWorkingSet 只是把页标记为可回收，不是释放内存，
    /// 代价几乎为零；下次访问这些页会重新换入，所以我们只在“界面刚收起来 / 插件刚卸载”这类
    /// 真正常见的空闲点调用它，让任务管理器里的内存读数立刻下来。
    /// 实测：宿主空壳 + 4 个插件从 ~109MB 降到 ~12MB。
    /// </summary>
    public static void TrimMemory(bool force = false)
    {
        if (!force && Settings?.Settings.TrimWorkingSetOnIdle == false)
        {
            return;
        }

        Interop.NativeMethods.TrimWorkingSet();
    }
}

/// <summary>
/// 单个插件的运行上下文实现（IPluginContext）。它是“插件 ↔ 宿主”的双向边界：插件只通过它触达宿主
/// （Shell/Logger/设置/目录），宿主也只通过它在合适的线程回调插件（主题变化）。内部用 Facade 模式把宿主的复杂对象图挡在外面。
/// </summary>
internal sealed class PluginContext : IPluginContext, IDisposable
{
    private readonly PluginHost _host;
    private readonly PluginDescriptor _descriptor;
    private bool _disposed;

    public PluginContext(PluginHost host, PluginDescriptor descriptor)
    {
        _host = host;
        _descriptor = descriptor;
        Logger = new PluginLogger(descriptor.Id);
    }

    public string PluginId => _descriptor.Id;

    public string PluginDirectory => Path.GetDirectoryName(_descriptor.FilePath) ?? AppPaths.BaseDirectory;

    public string DataDirectory => AppPaths.GetPluginDataDirectory(_descriptor.Id);

    public IPluginLogger Logger { get; }

    public PluginTheme Theme => AppServices.Theme.Current;

    public IShellService Shell => AppServices.Shell;

    public bool IsMiniMode => AppServices.Shell?.IsMiniMode ?? false;

    /// <summary>任务栏是否贴在左/右边缘（纵向停靠）。插件据此把内嵌读数排得更紧凑。</summary>
    public bool IsBarVertical => AppServices.Shell?.Bar?.ViewModel.IsVertical ?? false;

    /// <summary>用户空闲时长。宿主已经封装了 GetLastInputInfo，插件不用自己做 P/Invoke。</summary>
    public TimeSpan UserIdleTime
    {
        get
        {
            try
            {
                return Interop.NativeMethods.GetIdleTime();
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    public event EventHandler? ThemeChanged;

    /// <summary>插件通知宿主“我的图标/徽标变了”：转给 PluginHost.InvalidateBarItem 做合并刷新。</summary>
    public void InvalidateBarItem() => _host.InvalidateBarItem(_descriptor);

    /// <summary>读插件自己的设置（按插件 ID 隔离命名空间，不同插件不会串设置）。泛型 T 自动（反）序列化。</summary>
    public T? GetSetting<T>(string key, T? defaultValue = default) =>
        AppServices.Settings.GetPluginSetting(_descriptor.Id, key, defaultValue);

    /// <summary>写插件自己的设置，并落盘（持久化到该插件的设置区）。</summary>
    public void SetSetting<T>(string key, T? value) =>
        AppServices.Settings.SetPluginSetting(_descriptor.Id, key, value);

    /// <summary>让插件开一个独立浮窗（FloatingWindow），返回 IPluginWindow 供插件后续控制关闭/移动。</summary>
    public IPluginWindow CreateWindow(string title, FrameworkElement content, PluginWindowOptions? options = null) =>
        AppServices.Shell.CreateWindow(title, content, options);

    internal void RaiseThemeChanged()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"插件主题回调异常：{PluginId}", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ThemeChanged = null;
    }
}

/// <summary>给日志加上插件前缀，方便在同一个日志文件里区分来源。</summary>
internal sealed class PluginLogger : IPluginLogger
{
    private readonly string _prefix;

    public PluginLogger(string pluginId) => _prefix = $"[{pluginId}] ";

    public void Debug(string message) => AppLog.Debug(_prefix + message);

    public void Info(string message) => AppLog.Info(_prefix + message);

    public void Warn(string message, Exception? exception = null) => AppLog.Warn(_prefix + message, exception);

    public void Error(string message, Exception? exception = null) => AppLog.Error(_prefix + message, exception);
}
