using System.Collections.Generic;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.UI;
using MiniBar.Sdk;

namespace MiniBar.App.Services;

/// <summary>
/// 宿主能力门面：把“面板 / 迷你窗口 / 通知 / 浮窗 / 拖放 / 菜单聚合 / 热插拔”统一实现好，
/// 插件只依赖 <see cref="IShellService"/> 接口，与具体窗口实现解耦。
///
/// 内存策略：
///   · 浮层窗口、迷你窗口、通知窗口、插件管理器都只在第一次用到时才创建；
///   · 插件面板内容在关闭时立刻释放（调用插件的 ReleaseContent）；
///   · 同一时刻只保留一个面板浮层 —— 任务栏式交互本来就是这样，也最省资源。
/// </summary>
public sealed class ShellService : IShellService, IDisposable
{
    private readonly PluginHost _plugins;
    private readonly SettingsService _settings;

    private FlyoutWindow? _flyout;
    private MiniWindow? _mini;
    private ToastWindow? _toast;
    private PluginManagerWindow? _manager;
    private readonly List<FloatingWindow> _floatingWindows = new();
    private BarWindow? _bar;

    private string? _miniPluginId;
    private bool _disposed;

    public ShellService(PluginHost plugins, SettingsService settings)
    {
        _plugins = plugins;
        _settings = settings;
        _miniPluginId = settings.Settings.MiniModePluginId;
    }

    public BarWindow? Bar => _bar;

    public FlyoutWindow? Flyout => _flyout;

    public MiniWindow? Mini => _mini;

    public bool IsMiniMode { get; private set; }

    /// <summary>当前正在显示面板的插件（没有则为 null）。</summary>
    public PluginDescriptor? ActivePanelPlugin { get; private set; }

    public IntPtr MainWindowHandle => _bar is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(_bar).Handle;

    public string UserPluginDirectory => AppPaths.UserPluginDirectory;

    public void AttachBar(BarWindow bar) => _bar = bar;

    // ---------------------------------------------------------------- 面板

    public void OpenPanel(string pluginId)
    {
        var descriptor = _plugins.Find(pluginId);
        if (descriptor is null || !descriptor.IsLoaded || descriptor.Panel is null)
        {
            AppLog.Debug($"OpenPanel 忽略：{pluginId}（未加载或无面板能力）");
            return;
        }

        // 再点一次同一个图标 = 关闭（任务栏式切换）
        if (ActivePanelPlugin is not null &&
            string.Equals(ActivePanelPlugin.Id, pluginId, StringComparison.OrdinalIgnoreCase) &&
            _flyout is { IsVisible: true })
        {
            ClosePanel(pluginId);
            return;
        }

        ClosePanelCore();

        try
        {
            var host = new PanelHost(this, descriptor);
            var content = descriptor.Panel.CreateContent(host);
            var flyout = EnsureFlyout();
            flyout.ShowPanel(descriptor, content, host);

            ActivePanelPlugin = descriptor;
            descriptor.IsPanelOpen = true;
            descriptor.NotifyStateChanged();
        }
        catch (Exception ex)
        {
            AppLog.Error($"创建插件面板失败：{pluginId}", ex);
            Notify($"插件面板打开失败：{ex.Message}", NotificationKind.Error);
            ClosePanelCore();
        }
    }

    public void ClosePanel(string pluginId)
    {
        if (ActivePanelPlugin is not null &&
            !string.Equals(ActivePanelPlugin.Id, pluginId, StringComparison.OrdinalIgnoreCase))
        {
            return; // 请求关闭的不是当前面板
        }

        ClosePanelCore();
    }

    public void TogglePanel(string pluginId)
    {
        if (ActivePanelPlugin is not null &&
            string.Equals(ActivePanelPlugin.Id, pluginId, StringComparison.OrdinalIgnoreCase) &&
            _flyout is { IsVisible: true })
        {
            ClosePanelCore();
            return;
        }

        OpenPanel(pluginId);
    }

    public void CloseAllPanels() => ClosePanelCore();

    private void ClosePanelCore()
    {
        var descriptor = ActivePanelPlugin;
        ActivePanelPlugin = null;

        if (descriptor is not null)
        {
            descriptor.IsPanelOpen = false;

            try
            {
                descriptor.Panel?.ReleaseContent();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"插件面板释放异常：{descriptor.Id}", ex);
            }

            descriptor.NotifyStateChanged();
        }

        _flyout?.ClosePanel();

        // 面板收起后立刻把界面占用的内存页还给系统
        AppServices.TrimMemory();
    }

    internal void OnFlyoutClosed() => ClosePanelCore();

    internal void ResizePanel(double width, double height)
    {
        if (_flyout is null)
        {
            return;
        }

        _flyout.Width = Math.Clamp(width, 200, 1600);
        _flyout.Height = Math.Clamp(height, 120, 1200);
        _flyout.Reposition();
    }

    internal void SetPanelTitle(string title)
    {
        if (_flyout is { } flyout)
        {
            flyout.Title = title;
        }
    }

    private FlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            _flyout = new FlyoutWindow(_settings);
            _flyout.PanelClosed += (_, _) => ClosePanelCore();
            _flyout.Bar = _bar;
        }

        return _flyout;
    }

    // ---------------------------------------------------------------- 迷你模式

    /// <summary>当前应当用于迷你模式的插件（优先用户指定，其次第一个实现紧凑内容的插件）。</summary>
    public PluginDescriptor? ResolveMiniPlugin()
    {
        if (!string.IsNullOrEmpty(_miniPluginId))
        {
            var configured = _plugins.Find(_miniPluginId!);
            if (configured is { IsLoaded: true, HasCompact: true })
            {
                return configured;
            }
        }

        return _plugins.Plugins.FirstOrDefault(p => p.IsEnabled && p.IsLoaded && p.HasCompact);
    }

    public void SetMiniModePlugin(string pluginId)
    {
        _miniPluginId = pluginId;
        _settings.Settings.MiniModePluginId = pluginId;
        _settings.NotifyChanged();

        foreach (var descriptor in _plugins.Plugins)
        {
            descriptor.IsMiniHost = string.Equals(descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase);
        }

        if (IsMiniMode)
        {
            RefreshMiniContent();
        }
    }

    /// <summary>进入迷你模式：隐藏任务栏，只留一个小窗口显示指定插件的紧凑内容。</summary>
    public void EnterMiniMode(string pluginId)
    {
        if (!string.IsNullOrEmpty(pluginId))
        {
            SetMiniModePlugin(pluginId);
        }

        if (IsMiniMode)
        {
            RefreshMiniContent();
            return;
        }

        IsMiniMode = true;
        ClosePanelCore();

        _bar?.SetBarVisible(false);
        AppServices.Topmost?.Stop();

        var window = EnsureMini();
        if (window is not null)
        {
            RefreshMiniContent();
            window.ShowMinimizedOverlay();
        }

        AppLog.Info($"进入迷你模式，内容插件：{ResolveMiniPlugin()?.Name ?? "（无）"}");
        _bar?.RefreshMiniIndicator();
    }

    public void ExitMiniMode()
    {
        if (!IsMiniMode)
        {
            return;
        }

        IsMiniMode = false;

        _mini?.ReleaseContent();
        _mini?.Hide();

        _bar?.SetBarVisible(true);

        if (_settings.Settings.ReassertTopmost)
        {
            AppServices.Topmost?.Start();
        }

        AppLog.Info("退出迷你模式");
        _bar?.RefreshMiniIndicator();
    }

    private void RefreshMiniContent()
    {
        var descriptor = ResolveMiniPlugin();
        var window = EnsureMini();
        if (window is null)
        {
            return;
        }

        foreach (var plugin in _plugins.Plugins)
        {
            plugin.IsMiniHost = ReferenceEquals(plugin, descriptor);
        }

        if (descriptor?.Compact is null)
        {
            window.ShowPlaceholder("没有提供迷你内容的插件");
            return;
        }

        try
        {
            window.ReleaseContent();
            var content = descriptor.Compact.CreateCompactContent();
            window.ShowContent(descriptor, content);
        }
        catch (Exception ex)
        {
            AppLog.Error($"创建迷你内容失败：{descriptor.Id}", ex);
            window.ShowPlaceholder("迷你内容创建失败");
        }
    }

    private MiniWindow? EnsureMini()
    {
        if (_mini is not null)
        {
            return _mini;
        }

        try
        {
            _mini = new MiniWindow(_settings, this);
            return _mini;
        }
        catch (Exception ex)
        {
            AppLog.Error("创建迷你窗口失败", ex);
            return null;
        }
    }

    // ---------------------------------------------------------------- 通知

    public void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null)
    {
        try
        {
            _toast ??= new ToastWindow();
            _toast.ShowMessage(message, kind, duration ?? TimeSpan.FromSeconds(kind == NotificationKind.Error ? 4 : 2.5), _bar);
        }
        catch (Exception ex)
        {
            AppLog.Warn("显示通知失败", ex);
        }
    }

    // ---------------------------------------------------------------- 浮窗

    public IPluginWindow CreateWindow(string title, FrameworkElement content, PluginWindowOptions? options = null)
    {
        var window = new FloatingWindow(title, content, options ?? new PluginWindowOptions());
        _floatingWindows.Add(window);
        window.Closed += (_, _) =>
        {
            _floatingWindows.Remove(window);
            AppLog.Debug($"浮窗已关闭，剩余 {_floatingWindows.Count} 个");
        };

        return window;
    }

    // ---------------------------------------------------------------- 热插拔

    public string? LoadPlugin(string dllPath)
    {
        var id = _plugins.LoadPluginFile(dllPath, out var message);

        if (message is not null)
        {
            Notify(message, id is null ? NotificationKind.Error : NotificationKind.Success);
        }

        return id;
    }

    public bool UnloadPlugin(string pluginId, bool deleteFile = false)
    {
        var descriptor = _plugins.Find(pluginId);
        if (descriptor is null)
        {
            return false;
        }

        var ok = _plugins.Unload(descriptor, deleteFile);
        Notify(deleteFile ? $"已卸载并删除：{descriptor.Name}" : $"已卸载：{descriptor.Name}",
            ok ? NotificationKind.Success : NotificationKind.Warning);
        return ok;
    }

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        var descriptor = _plugins.Find(pluginId);
        if (descriptor is null)
        {
            return;
        }

        _plugins.SetEnabled(descriptor, enabled);
    }

    public bool IsPluginLoaded(string pluginId) => _plugins.Find(pluginId)?.IsLoaded ?? false;

    public IReadOnlyList<PluginInfo> GetPlugins() => _plugins.GetPluginInfos();

    public void ShowMainMenu() => _bar?.ShowMainContextMenu();

    public void ShowPluginManager()
    {
        if (_manager is null || !_manager.IsLoaded)
        {
            _manager = new PluginManagerWindow(_plugins, this, _settings);
        }

        _manager.Show();
        _manager.Activate();
        if (_manager.WindowState == WindowState.Minimized)
        {
            _manager.WindowState = WindowState.Normal;
        }
    }

    // ---------------------------------------------------------------- 拖放

    /// <summary>
    /// 统一的拖放处理入口。顺序：
    ///   1) 拖到某个图标上时，先问该插件；
    ///   2) 再依次问其它实现了 IDropHandlerPlugin 的插件；
    ///   3) 都没人处理就走内置兜底：DLL → 加载插件；其它 → 提示并给出可执行动作。
    /// </summary>
    public void HandleDrop(IReadOnlyList<string> paths, PluginDropTarget target, string? targetPluginId, Point screenPosition)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var isSingleDll = paths.Count == 1 &&
                          string.Equals(Path.GetExtension(paths[0]), ".dll", StringComparison.OrdinalIgnoreCase);

        var ordered = new List<PluginDescriptor>();

        if (targetPluginId is not null)
        {
            var targetPlugin = _plugins.Find(targetPluginId);
            if (targetPlugin is { IsLoaded: true, HasDropHandler: true })
            {
                ordered.Add(targetPlugin);
            }
        }

        ordered.AddRange(_plugins.GetEnabledPlugins()
            .Where(p => p.IsLoaded && p.HasDropHandler && !ReferenceEquals(p, ordered.FirstOrDefault())));

        foreach (var descriptor in ordered)
        {
            if (descriptor.DropHandler is not { } handler)
            {
                continue;
            }

            try
            {
                var context = new PluginDropContext(descriptor.Facade!, target, targetPluginId, paths, screenPosition)
                {
                    IsPluginDll = isSingleDll,
                    DllPath = isSingleDll ? paths[0] : null,
                };

                if (!handler.CanHandle(context))
                {
                    continue;
                }

                handler.OnDrop(context);

                if (context.Handled)
                {
                    AppLog.Info($"拖放已由插件处理：{descriptor.Name}（{paths.Count} 项）");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"插件拖放处理异常：{descriptor.Id}", ex);
                Notify($"「{descriptor.Name}」处理拖放时出错：{ex.Message}", NotificationKind.Error);
                return;
            }
        }

        HandleDropFallback(paths, isSingleDll);
    }

    private void HandleDropFallback(IReadOnlyList<string> paths, bool isSingleDll)
    {
        if (isSingleDll)
        {
            if (!_settings.Settings.AutoLoadPluginOnDllDrop)
            {
                Notify("已在设置中关闭“拖入 DLL 自动加载插件”", NotificationKind.Warning);
                return;
            }

            LoadPlugin(paths[0]);
            return;
        }

        var summary = paths.Count == 1
            ? paths[0]
            : $"{paths.Count} 个项目（{Path.GetFileName(paths[0])} 等）";

        AppLog.Info($"拖放无人处理：{summary}");
        Notify($"已收到：{summary}\n把插件 DLL 拖到这里即可加载。", NotificationKind.Info, TimeSpan.FromSeconds(3.5));
    }

    // ---------------------------------------------------------------- 菜单聚合

    /// <summary>收集所有菜单插件在指定位置提供的菜单项。</summary>
    public List<PluginMenuEntry> CollectMenuEntries(PluginMenuTarget target, string? targetPluginId,
        IReadOnlyList<string>? dropPaths = null)
    {
        var result = new List<PluginMenuEntry>();

        foreach (var descriptor in _plugins.GetEnabledPlugins())
        {
            if (!descriptor.IsLoaded || descriptor.Menu is not { } menu)
            {
                continue;
            }

            try
            {
                var context = new PluginMenuContext(descriptor.Facade!, target, targetPluginId, dropPaths);
                var entries = menu.GetMenuEntries(context);
                if (entries is null)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry is not null)
                    {
                        result.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"插件菜单生成异常：{descriptor.Id}", ex);
            }
        }

        return result;
    }

    /// <summary>把插件提供的菜单项包一层 try/catch 后执行。</summary>
    public void InvokeMenuEntry(PluginMenuEntry entry)
    {
        try
        {
            entry.Invoke?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error($"插件菜单执行异常：{entry.Header}", ex);
            Notify($"执行「{entry.Header}」失败：{ex.Message}", NotificationKind.Error);
        }
    }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>主题变化时通知所有插件。</summary>
    public void OnThemeChanged() => _plugins.RaiseThemeChanged();

    /// <summary>设置变化后立即应用（位置、透明度、置顶等）。</summary>
    public void ApplySettings()
    {
        var settings = _settings.Settings;

        _bar?.ApplySettings();

        if (IsMiniMode)
        {
            _mini?.ApplySettings();
        }

        AppServices.Topmost?.Stop();
        if (settings.ReassertTopmost && AppServices.Topmost is not null)
        {
            AppServices.Topmost.Enabled = true;
            if (!IsMiniMode)
            {
                AppServices.Topmost.Start();
            }
        }

        if (AppServices.Fullscreen is { } watcher)
        {
            watcher.TreatQuietTimeAsFullscreen = settings.TreatQuietHoursAsFullscreen;
            if (settings.AutoEnterMiniMode)
            {
                watcher.Enabled = true;
            }
            else
            {
                watcher.Enabled = false;
                ExitMiniMode();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var window in _floatingWindows.ToArray())
        {
            try
            {
                window.Close();
            }
            catch
            {
                // ignore
            }
        }

        _floatingWindows.Clear();

        _mini?.ReleaseContent();
        _mini?.Close();
        _manager?.Close();
        _toast?.Close();
        _flyout?.ClosePanel();
    }
}

/// <summary>面板与宿主之间的适配器。</summary>
internal sealed class PanelHost : IPanelHost
{
    private readonly ShellService _shell;
    private readonly PluginDescriptor _descriptor;

    public PanelHost(ShellService shell, PluginDescriptor descriptor)
    {
        _shell = shell;
        _descriptor = descriptor;
    }

    public IPluginContext Plugin => _descriptor.Facade!;

    public bool IsPinned { get; private set; }

    public void Close() => _shell.ClosePanel(_descriptor.Id);

    public void Resize(double width, double height) => _shell.ResizePanel(width, height);

    public void SetTitle(string title) => _shell.SetPanelTitle(title);

    public void SetPinned(bool pinned) => IsPinned = pinned;
}
