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
/// 程序入口与生命周期协调 —— <b>想读懂整个项目，建议从这里开始</b>。
///
/// <para><b>一、这个程序是什么</b></para>
/// <para>
/// 它是一个"空壳"：自己只提供一条任务栏（能占住屏幕边缘、能显示任意插件的内容），
/// 所有具体功能都以 DLL 形式在运行时插进来。所以"程序主体"很小，
/// 真正复杂的是<b>插件的加载/卸载</b>与<b>宿主对插件的服务</b>这两件事。
/// </para>
///
/// <para><b>二、启动顺序（这个顺序是有原因的，不是随手排的）</b></para>
/// <list type="number">
///   <item><b>配置</b>（SettingsService）：后面所有东西都要读配置，所以必须最早；</item>
///   <item><b>主题</b>（ThemeService）：窗口一显示就要用颜色，必须早于任何窗口；</item>
///   <item><b>快捷键管理器</b>：它需要一个窗口才能注册系统热键，所以先建对象、后挂窗口；</item>
///   <item><b>插件宿主</b>（PluginHost）：负责扫描/加载/卸载，但此时还<b>不</b>加载插件；</item>
///   <item><b>能力门面</b>（ShellService）：插件要用到的"开面板、通知、热插拔"都由它提供；</item>
///   <item><b>全屏监视器</b>：要早于任务栏窗口创建，这样窗口能在初始化时把自己登记进"忽略列表"，
///         否则它自己会被当成全屏程序；</item>
///   <item><b>任务栏窗口</b>（BarWindow）：窗口一 Show，用户就看到任务栏了（此时是空的）；</item>
///   <item><b>置顶守卫 / 系统资源兜底</b>：注册退出清理钩子；</item>
///   <item><b>异步扫描插件</b>：最后才开始读磁盘上的 DLL —— 界面已经出来了，加载慢也不影响观感。</item>
/// </list>
///
/// <para><b>三、三条贯穿全项目的原则</b></para>
/// <list type="bullet">
///   <item><b>一切跨边界的东西都要走 MiniBar.Sdk</b>：宿主不认识插件类型，插件也不认识宿主类型；</item>
///   <item><b>系统级资源必须成对释放</b>：AppBar 占位、热键、系统任务栏状态，都要在退出路径上还回去；</item>
///   <item><b>低占用</b>：不引入第三方包、界面按需创建、空闲时把内存还给系统。</item>
/// </list>
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

        // 兜住 UI 线程上没被处理的异常：记日志 + 弹提示，而不是让程序直接崩掉
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 注意这里是"即发即忘"（_ =）：启动过程是异步的（要读磁盘扫插件），
        // 但不能阻塞 WPF 的启动流程，否则窗口出不来。
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
            //
            // "把文件拖到 exe 上"会启动一个新进程。如果直接开第二个任务栏，
            // 屏幕上就会出现两条任务栏、注册两次 AppBar —— 所以这里用命名互斥体判断：
            // 不是第一个实例就通过命名管道把参数发给已运行的实例，然后自己退出。
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

            // AppBar 占用的是系统级资源：必须保证任何退出路径都把它还给系统，
            // 否则一旦异常终止，用户桌面底部会永久空出一条（经验证 taskkill /F 就会这样）。
            // 系统任务栏的自动隐藏同理 —— 不恢复的话用户会"找不到任务栏"。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseSystemResources();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseSystemResources();
            SessionEnding += (_, _) => ReleaseSystemResources();

            // 把"系统任务栏自动隐藏"同步给系统
            ApplySystemTaskbarSetting();

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
            //
            // 为什么要 await 又 ConfigureAwait(true)？
            //   · await  ：后面的步骤（处理命令行路径、裁剪内存）要等插件都挂上才有意义；
            //   · true   ：让后续代码回到 UI 线程继续跑 —— 插件实例化和界面操作都必须在 UI 线程上。
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

        // Ctrl+Alt+, 是 Windows 11 "打开设置" 的惯例键位；Ctrl+Alt+S 太容易被别的程序占用
        hotkeys.Register(HostHotkeyOwner, "settings", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.OemComma,
            "Ctrl+Alt+, 打开设置");
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

            case "settings":
                shell.ShowSettings();
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

    /// <summary>
    /// 释放所有"系统级"资源：AppBar 占用的屏幕空间 + 被改动的系统任务栏状态。
    /// 可被多次调用（幂等），异常一律吞掉 —— 退出路径上绝不能再抛。
    /// </summary>
    private static void ReleaseSystemResources()
    {
        try
        {
            AppServices.Shell?.Bar?.AppBar.Dispose();
        }
        catch
        {
            // 退出路径上不允许再抛异常
        }

        try
        {
            // 移除系统托盘图标（被强杀时它可能残留到下一次鼠标划过，尽量清掉）
            AppServices.Shell?.Bar?.Tray.Dispose();
        }
        catch
        {
            // 同上
        }

        try
        {
            // 没改过任务栏设置的话，Restore 什么都不做
            Interop.SystemTaskbar.Restore();
        }
        catch
        {
            // 同上
        }
    }

    /// <summary>
    /// 把设置里的"系统任务栏自动隐藏"同步到系统：
    /// 开 = 打开自动隐藏，关 = 恢复到程序启动前的状态。幂等，可以随便重复调用。
    /// </summary>
    public static void ApplySystemTaskbarSetting()
    {
        try
        {
            if (AppServices.Settings.Settings.AutoHideSystemTaskbar)
            {
                Interop.SystemTaskbar.SetAutoHide(true);
            }
            else
            {
                Interop.SystemTaskbar.Restore();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("切换系统任务栏自动隐藏失败", ex);
        }
    }

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

        // 正常退出这条路径也要把系统级资源还回去（前面那几个钩子是给异常退出兜底的）
        ReleaseSystemResources();

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
