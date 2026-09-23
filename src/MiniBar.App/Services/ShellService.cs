using System.Collections.Generic;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.UI;
using MiniBar.Sdk;

namespace MiniBar.App.Services;

/// <summary>
/// 宿主能力门面：<see cref="IShellService"/> 的【唯一实现】。插件做不了、或不该自己做的事（开面板、迷你模式、通知、浮窗、
/// 热插拔、打开设置/插件管理器）都通过它申请；它同时是“宿主状态变化”的广播中心——主题变化、迷你模式进出都会从这里
/// 通知到所有插件（见 OnThemeChanged / RaiseThemeChanged）。
///
/// 设计要点：
///   · 插件只依赖 IShellService 接口，永远碰不到具体窗口类型，宿主升级窗口实现也不影响插件；
///   · 面板生命周期：每次打开调插件 CreateContent、关闭调 ReleaseContent；插件必须在 ReleaseContent 里停计时器/断开引用
///     （低内存的关键约定）。浮层是【同一个窗口复用】——关闭时把内容置空，不销毁窗口，下次打开直接换内容；
///   · 内存策略：浮层/迷你/通知/管理器/设置窗口都“第一次用到才创建”（惰性），插件面板内容关闭即释放；
///   · 点击语义（踩过坑）：宿主先调插件 OnClick；插件若在 OnClick 里调用 OpenPanel/ClosePanel/TogglePanel/EnterMiniMode，
///     会把 BarItemClickContext.ClickHandled 置 true，宿主就不再套默认行为；否则只要插件实现了 IPanelContentPlugin，
///     宿主自动 toggle 面板。两边都 toggle = 面板开一下马上关。
/// </summary>
public sealed class ShellService : IShellService, IDisposable
{
    private readonly PluginHost _plugins;
    private readonly SettingsService _settings;

    private FlyoutWindow? _flyout;
    private MiniWindow? _mini;
    private ToastWindow? _toast;
    private SettingsWindow? _settingsWindow;
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

    /// <summary>
    /// 打开某插件的面板浮层。任务栏式交互：点同一个已打开图标的面板应“关闭”，所以这里有 toggle 的微妙处理。
    ///
    /// 分步骤：
    ///   1. 查找 descriptor；不是已加载、或不具备面板能力（descriptor.Panel 为 null）直接忽略；
    ///   2. 若当前已是该插件打开且浮层可见 → 改为关闭（ClosePanel），实现“再点一下收起”；
    ///   3. 否则先 ClosePanelCore() 关掉上一个面板（同一时刻只保留一个浮层，最省资源）；
    ///   4. 用 PanelHost 适配宿主与插件：调用插件的 CreateContent(host) 生成界面内容，塞进 EnsureFlyout() 得到的（复用）浮层窗口；
    ///   5. 记录 ActivePanelPlugin、置 IsPanelOpen=true，并通知插件状态变化。
    ///
    /// 为什么复用同一个浮层窗口而不每次 new：频繁开关面板时重复建窗/销窗既慢又容易内存抖动；复用 + ReleaseContent 是最稳的约定。
    /// </summary>
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

    /// <summary>
    /// 关闭面板的核心实现（被 OpenPanel/ClosePanel/TogglePanel/ExitMiniMode 共用）。
    /// 关键点：调用插件的 ReleaseContent() 让插件停计时器、断开引用（这是低内存约定），再把浮层内容清空，最后 TrimMemory。
    /// </summary>
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

    /// <summary>
    /// 惰性获取浮层窗口：第一次才 new 出来并订阅 PanelClosed 事件；之后复用同一个实例（面板“同一窗口复用”策略的核心）。
    /// </summary>
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

    /// <summary>
    /// 重新生成迷你窗口内容：解析出当前迷你插件 → 释放旧内容 → 调插件的 CreateCompactContent() 换上新内容；
    /// 没有合适插件时显示占位提示。每次进出迷你模式或切换内容源都会走这里。
    /// </summary>
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

    /// <summary>重新加载某插件（先卸后载）：跳过重复文件。供界面“重新加载”按钮或诊断用。</summary>
    public bool ReloadPlugin(string pluginId)
    {
        var descriptor = _plugins.Find(pluginId);
        if (descriptor is null || descriptor.IsDuplicate)
        {
            return false;
        }

        _plugins.Reload(descriptor);
        return true;
    }

    /// <summary>把所有插件的 PluginInfo 快照返回（UI/远程查询用，不含内部引用）。</summary>
    public IReadOnlyList<PluginInfo> GetPlugins() => _plugins.GetPluginInfos();

    /// <summary>弹出主菜单（任务栏右键菜单的入口，由宿主窗口实现）。</summary>
    public void ShowMainMenu() => _bar?.ShowMainContextMenu();

    /// <summary>
    /// 打开"插件管理"。
    ///
    /// <para>
    /// 注意：插件管理界面**已经合并进设置窗口**（原来是一个独立窗口），
    /// 所以这里只是把设置窗口打开并定位到「插件管理」页 —— 对外接口保持不变，
    /// 老代码（菜单、快捷键 Ctrl+Alt+P、插件管理器按钮）都不需要改。
    /// </para>
    /// </summary>
    public void ShowPluginManager() => ShowSettings(PluginManagerPage);

    /// <summary>设置窗口里"插件管理"页的伪插件 ID（不会与真实插件 ID 冲突）。</summary>
    internal const string PluginManagerPage = "minibar.page.plugins";

    /// <summary>打开设置窗口（宿主设置 + 所有插件的设置）。</summary>
    public void ShowSettings(string pluginId = "")
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_plugins, _settings, this);
        }

        _settingsWindow.Show();

        if (!string.IsNullOrEmpty(pluginId))
        {
            _settingsWindow.SelectPlugin(pluginId);
        }

        _settingsWindow.Activate();

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
    }

    // ---------------------------------------------------------------- 拖放

    /// <summary>
    /// 统一的拖放处理入口（任务栏把文件/文件夹拖到 MiniBar 上时调用）。
    ///
    /// 分步骤：
    ///   1. 没东西被拖进来直接返回；先判断是不是“单个 .dll”（决定后续兜底动作）；
    ///   2. 组装候选插件顺序：① 拖到的那个目标插件（若它已加载且支持拖放）排第一；② 其余启用的、支持拖放的插件按固定顺序排后面；
    ///   3. 逐个问插件：先 CanHandle 询问“你接不接”，接就 OnDrop 交给它处理；插件把 PluginDropContext.Handled 置 true 表示“我处理完了”；
    ///   4. 任一插件处理后立即 return；插件处理抛异常则提示并 return（不让一个插件拖垮整个拖放）；
    ///   5. 谁都没接 → HandleDropFallback：单个 DLL 按设置决定自动加载插件，其它文件给个“把插件 DLL 拖到这里”的提示。
    ///
    /// 为什么“先问目标插件、再问其它”：让用户拖到特定图标上有明确意图（比如拖文件到某插件），同时保留全局兜底，体验最顺。
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
        _settingsWindow?.Close();
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
