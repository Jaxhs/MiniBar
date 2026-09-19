using System.IO;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.App.UI;
using MiniBar.Sdk;

namespace MiniBar.App;

/// <summary>
/// 程序入口与生命周期协调。
///
/// 启动顺序（有意为之）：
///   配置 → 主题 → 快捷键管理器 → 插件宿主 → 能力门面 → 任务栏窗口 → 置顶守卫 → 全屏监视 → 异步扫描插件
/// 这样窗口一出现就是可用的“空壳”，插件随后热插拔进来，不需要重启程序。
/// </summary>
public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private BarWindow? _bar;
    private int _unhandledCount;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _ = StartAsync(e.Args);
    }

    private async Task StartAsync(string[] args)
    {
        try
        {
            AppPaths.EnsureCreated();

            var settings = new SettingsService();
            AppLog.Verbose = settings.Settings.VerboseLogging;
            AppLog.Info("======================== MiniBar 启动 ========================");
            AppLog.Info($"程序目录：{AppPaths.BaseDirectory}");

            // 单实例：第二个实例只负责把路径转发进来
            _instance = new SingleInstanceCoordinator(OnExternalPathsReceived);
            if (!_instance.IsFirstInstance)
            {
                var forwarded = ExtractPaths(args);
                if (forwarded.Length > 0)
                {
                    _instance.SendPaths(forwarded);
                }

                AppLog.Info("已有实例在运行，本进程退出。");
                Shutdown();
                return;
            }

            AppServices.Settings = settings;

            AppServices.Theme = new ThemeService();
            AppServices.Theme.Apply(settings.Settings.Theme);

            AppServices.Hotkeys = new HotkeyManager();
            AppServices.Hotkeys.Triggered += OnHotkeyTriggered;

            var plugins = new PluginHost(settings);
            AppServices.Plugins = plugins;

            var shell = new ShellService(plugins, settings);
            AppServices.Shell = shell;

            // 先建全屏监视器，让任务栏窗口在初始化时就能把自己加入忽略列表
            var fullscreen = new FullscreenWatcher(settings.Settings.FullscreenPollMs)
            {
                TreatQuietTimeAsFullscreen = settings.Settings.TreatQuietHoursAsFullscreen,
            };
            fullscreen.Changed += OnFullscreenChanged;
            AppServices.Fullscreen = fullscreen;

            _bar = new BarWindow(settings, plugins, shell);
            shell.AttachBar(_bar);
            _bar.Show();

            AppServices.Hotkeys.Attach(_bar);
            RegisterHostHotkeys();

            AppServices.Topmost = new TopmostGuard(_bar, settings.Settings.TopmostGuardIntervalMs)
            {
                Enabled = settings.Settings.ReassertTopmost,
            };

            if (settings.Settings.ReassertTopmost)
            {
                AppServices.Topmost.Start();
            }

            if (settings.Settings.AutoEnterMiniMode)
            {
                fullscreen.Enabled = true;
            }

            StartIdleTrim();

            // 插件扫描与加载（异步，不阻塞窗口出现）
            await plugins.StartAsync().ConfigureAwait(true);

            var pinned = plugins.PinnedPlugins.Count();
            AppLog.Info($"启动完成：插件 {plugins.Plugins.Count} 个，固定 {pinned} 个，已加载 {plugins.Plugins.Count(p => p.IsLoaded)} 个");

            // 处理“拖到 exe 上”启动的情况
            var initialPaths = ExtractPaths(args);
            if (initialPaths.Length > 0)
            {
                shell.HandleDrop(initialPaths, PluginDropTarget.Bar, null, DisplayService.GetCursorPositionPixels());
            }

            TrimWorkingSetIfEnabled();
        }
        catch (Exception ex)
        {
            AppLog.Error("启动失败", ex);
            MessageBox.Show($"MiniBar 启动失败：\n\n{ex}", "MiniBar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static string[] ExtractPaths(IEnumerable<string> args) =>
        args.Where(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith('-'))
            .Select(a => a.Trim('"'))
            .Where(a => File.Exists(a) || Directory.Exists(a))
            .ToArray();

    // ---------------------------------------------------------------- 全屏 → 迷你模式

    private void OnFullscreenChanged(object? sender, FullscreenState state)
    {
        var settings = AppServices.Settings.Settings;

        if (_shuttingDown)
        {
            return;
        }

        if (state.IsFullscreen)
        {
            if (!settings.AutoEnterMiniMode)
            {
                return;
            }

            if (AppServices.Shell.ResolveMiniPlugin() is { } miniSource)
            {
                AppLog.Info($"检测到全屏程序（{state.WindowClass}），切换到迷你模式，内容：{miniSource.Name}");
                AppServices.Shell.EnterMiniMode(string.Empty);
            }
            else if (settings.HideBarWhenFullscreen)
            {
                // 没有任何插件提供紧凑内容：干脆把任务栏让出去，避免干扰全屏程序
                AppLog.Info("检测到全屏程序且没有可用的紧凑内容插件，隐藏任务栏");
                _bar?.SetBarVisible(false);
            }
        }
        else
        {
            if (AppServices.Shell.IsMiniMode)
            {
                AppServices.Shell.ExitMiniMode();
            }
            else if (_bar is not null && !_bar.IsVisible)
            {
                _bar.SetBarVisible(true);
            }
        }
    }

    // ---------------------------------------------------------------- 快捷键

    /// <summary>宿主自身的快捷键用它作为“插件 ID”，与插件快捷键走同一条注册与分发通道。</summary>
    private const string HostHotkeyOwner = "minibar.host";

    private void RegisterHostHotkeys()
    {
        var hotkeys = AppServices.Hotkeys;
        if (hotkeys is null)
        {
            return;
        }

        // 全屏时自动进迷你模式，但用户也需要一个手动开关来强制切换
        hotkeys.Register(HostHotkeyOwner, "toggleMini", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.M,
            "Ctrl+Alt+M 进入/退出迷你模式");

        hotkeys.Register(HostHotkeyOwner, "toggleBar", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.H,
            "Ctrl+Alt+H 显示/隐藏任务栏");

        hotkeys.Register(HostHotkeyOwner, "manager", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.P,
            "Ctrl+Alt+P 打开插件管理");
    }

    private void OnHotkeyTriggered(object? sender, HotkeyRegistration registration)
    {
        if (string.Equals(registration.PluginId, HostHotkeyOwner, StringComparison.OrdinalIgnoreCase))
        {
            HandleHostHotkey(registration.HotkeyId);
            return;
        }

        var descriptor = AppServices.Plugins.Find(registration.PluginId);
        if (descriptor?.Hotkeys is not { } hotkeys)
        {
            return;
        }

        try
        {
            hotkeys.OnHotkey(registration.HotkeyId);
        }
        catch (Exception ex)
        {
            AppLog.Error($"插件快捷键处理异常：{registration.PluginId}/{registration.HotkeyId}", ex);
            AppServices.Shell.Notify($"快捷键执行失败：{ex.Message}", NotificationKind.Error);
        }
    }

    private void HandleHostHotkey(string hotkeyId)
    {
        var shell = AppServices.Shell;

        switch (hotkeyId)
        {
            case "toggleMini":
                if (shell.IsMiniMode)
                {
                    shell.ExitMiniMode();
                }
                else
                {
                    shell.EnterMiniMode(string.Empty);
                }

                break;

            case "toggleBar":
                if (_bar is null)
                {
                    return;
                }

                _bar.SetBarVisible(!_bar.IsVisible);
                break;

            case "manager":
                shell.ShowPluginManager();
                break;
        }
    }

    // ---------------------------------------------------------------- 外部路径（第二个实例转发过来的）

    private void OnExternalPathsReceived(string[] paths)
    {
        try
        {
            // 全屏/迷你状态下被拖文件，先把界面还原出来
            if (AppServices.Shell.IsMiniMode)
            {
                AppServices.Shell.ExitMiniMode();
            }

            _bar?.SetBarVisible(true);
            AppServices.Shell.HandleDrop(paths, PluginDropTarget.Bar, null, DisplayService.GetCursorPositionPixels());
        }
        catch (Exception ex)
        {
            AppLog.Error("处理外部拖入路径失败", ex);
        }
    }

    // ---------------------------------------------------------------- 空闲时归还内存

    private System.Timers.Timer? _idleTrimTimer;

    /// <summary>
    /// 空闲时归还内存。
    /// 用线程池定时器而不是 DispatcherTimer：DispatcherTimer 在 ApplicationIdle 优先级下
    /// 可能因为界面持续有渲染/计时器工作而迟迟不被调度，而这里只是一次纯 Win32 调用，
    /// 放在后台线程上更可靠。
    /// </summary>
    private void StartIdleTrim()
    {
        if (!AppServices.Settings.Settings.TrimWorkingSetOnIdle)
        {
            return;
        }

        _idleTrimTimer = new System.Timers.Timer(10_000) { AutoReset = true };

        _idleTrimTimer.Elapsed += (_, _) =>
        {
            try
            {
                if (!AppServices.Settings.Settings.TrimWorkingSetOnIdle)
                {
                    return;
                }

                if (NativeMethods.GetIdleTime().TotalSeconds >= AppServices.Settings.Settings.IdleTrimSeconds)
                {
                    TrimWorkingSetIfEnabled();
                }
            }
            catch
            {
                // 后台定时器里绝不能抛异常
            }
        };

        _idleTrimTimer.Start();

        // 启动完成后先做一次：把探测插件、JIT、资源加载阶段留下的空闲页还给系统
        Dispatcher.BeginInvoke(new Action(TrimWorkingSetIfEnabled), DispatcherPriority.ApplicationIdle);
    }

    private static void TrimWorkingSetIfEnabled()
    {
        if (!AppServices.Settings.Settings.TrimWorkingSetOnIdle)
        {
            return;
        }

        AppServices.TrimMemory();
        AppLog.Debug("已裁剪工作集");
    }

    // ---------------------------------------------------------------- 退出

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("未处理的界面异常", e.Exception);
        _unhandledCount++;

        // 少量异常按“可恢复”处理，避免小毛病把常驻程序弄崩；连续出错则退出，避免静默错乱
        if (_unhandledCount <= 5)
        {
            e.Handled = true;
            try
            {
                AppServices.Shell?.Notify($"发生异常：{e.Exception.Message}", NotificationKind.Error);
            }
            catch
            {
                // ignore
            }

            return;
        }

        e.Handled = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        AppLog.Info("MiniBar 正在退出…");

        _idleTrimTimer?.Stop();

        try
        {
            AppServices.Fullscreen?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            AppServices.Hotkeys?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            AppServices.Shell?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            AppServices.Plugins?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            AppServices.Settings?.SaveNow();
        }
        catch
        {
            // ignore
        }

        _instance?.Dispose();
        AppLog.Info("MiniBar 已退出");
        AppLog.Shutdown();

        base.OnExit(e);
    }
}
