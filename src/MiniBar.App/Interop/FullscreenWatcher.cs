using System.Collections.Generic;
using System.Windows.Threading;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

/// <summary>
/// 一次全屏检测的结果快照。
/// <para>
/// 用 <c>readonly record struct</c>：<c>record</c> 自带按值比较（两个结果所有字段相同就相等，
/// 方便 <see cref="Poll"/> 用 <c>state == Current</c> 判断是否变了），<c>struct</c> 让它分配在栈上、
/// 不用进GC堆（这种小对象每 700ms 创建一个，用 struct 几乎零开销），<c>readonly</c> 保证创建后不可改、线程安全。
/// </para>
/// </summary>
public readonly record struct FullscreenState(
    bool IsFullscreen,
    IntPtr Window,
    string WindowClass,
    string WindowTitle,
    bool IsQuietTime)
{
    public static readonly FullscreenState None = new(false, IntPtr.Zero, string.Empty, string.Empty, false);
}

/// <summary>
/// 全屏检测。判定规则：前台窗口的矩形完整覆盖了它所在显示器的<b>整个</b>屏幕区域
/// （不是工作区），且该窗口可见、未最小化、不是桌面/任务栏这一类壳窗口。
///
/// 只做 GetForegroundWindow + GetWindowRect + MonitorFromWindow + GetMonitorInfo 四次系统调用，
/// 都是纯内存查询，默认 700ms 一次，CPU 占用可忽略。
/// 另外叠加 SHQueryUserNotificationState，把“演示模式 / D3D 独占全屏”也识别出来。
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",              // 桌面
        "WorkerW",
        "Shell_TrayWnd",        // 任务栏
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", // Win11 任务视图/桌面切换
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
        "ApplicationManager_DesktopShellWindow",
    };

    private readonly DispatcherTimer _timer;
    private readonly HashSet<IntPtr> _ignoredWindows = new();
    private readonly object _gate = new();

    public FullscreenWatcher(int pollMs)
    {
        // 这里用 DispatcherTimer，而不是 System.Timers.Timer —— 两者区别很关键：
        //   · System.Timers.Timer 的 Elapsed 在"线程池线程"上触发，回调里不能直接碰 WPF 的 UI 对象
        //     （会抛"跨线程访问"异常），必须 Dispatcher.Invoke 切回 UI 线程；
        //   · DispatcherTimer 直接在 UI 线程（Dispatcher 队列）上触发，回调里能安全读写 WPF 对象，
        //     且用 Background 优先级，保证不抢界面交互的响应。本项目轮询后要更新 UI 状态，所以用它就对了。
        // （对比：AppLog、SettingsService 用 System.Timers.Timer 做"纯后台落盘"，不需要碰 UI，反而更省。）
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200, pollMs)),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public FullscreenState Current { get; private set; } = FullscreenState.None;

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value)
            {
                _timer.Start();
                Poll();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    /// <summary>是否把“专注助手/演示模式”也当成需要进入迷你模式的状态。</summary>
    public bool TreatQuietTimeAsFullscreen { get; set; }

    public event EventHandler<FullscreenState>? Changed;

    /// <summary>把本程序自己的窗口加入忽略列表，避免自己成为前台窗口时误判。</summary>
    public void IgnoreWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (_gate)
        {
            _ignoredWindows.Add(hwnd);
        }
    }

    /// <summary>
    /// 接收来自 AppBar 回调的系统级全屏通知（ABN_FULLSCREENAPP）。
    /// 这是 Shell 主动告诉我们的，比轮询快；但它只在"进入/退出独占全屏"时各来一次，
    /// 而且无边框全屏窗口的判定仍然是轮询更准，所以两者互补：通知用来抢先，轮询用来校正。
    /// </summary>
    public void ReportSystemFullscreen(bool started)
    {
        if (started)
        {
            if (Current.IsFullscreen)
            {
                return;
            }

            var fg = NativeMethods.GetForegroundWindow();
            var state = new FullscreenState(true, fg,
                fg == IntPtr.Zero ? "system" : NativeMethods.GetWindowClass(fg),
                fg == IntPtr.Zero ? "系统全屏通知" : NativeMethods.GetWindowTitle(fg),
                false);

            Current = state;
            AppLog.Info("系统通知：有程序进入全屏");
            Changed?.Invoke(this, state);
            return;
        }

        // 退出全屏：立刻采样校正（可能还有别的全屏程序，交给 Detect 判断）
        AppLog.Info("系统通知：全屏程序已退出");
        Poll();

        if (Current.IsFullscreen)
        {
            Current = FullscreenState.None;
            Changed?.Invoke(this, Current);
        }
    }

    public void Poll()
    {
        var state = Detect();
        if (state == Current)
        {
            return;
        }

        var previous = Current;
        Current = state;
        AppLog.Debug($"全屏状态变化：{previous.IsFullscreen} -> {state.IsFullscreen} " +
                     $"({state.WindowClass} \"{state.WindowTitle}\")");
        Changed?.Invoke(this, state);
    }

    /// <summary>
    /// 轮询式全屏判定（"腿一"）。分步：
    /// ① 先看 <c>SHQueryUserNotificationState</c> 是否处于 D3D 独占全屏 / 演示模式 / 专注助手
    ///    （<see cref="IsQuietTime"/>，这类全屏不一定有盖满屏的窗口）；
    /// ② 取前台窗口 <c>GetForegroundWindow</c>，跳过本程序自己与桌面/任务栏等壳窗口；
    /// ③ 确认它可见、未最小化；用 <c>GetWindowRect</c> 取它的物理像素矩形；
    /// ④ 取它所在显示器的整屏范围（不是工作区），允许 1px 误差，判断矩形是否盖满整屏。
    /// 任一步不满足就判为"非全屏"。这套轮询和 AppBar 的 <c>ABN_FULLSCREENAPP</c> 通知互补：
    /// 通知更快但只在注册期间有效、对无边框全屏判定不准，轮询稳但慢，所以两条腿都要。
    /// </summary>
    private FullscreenState Detect()
    {
        var quiet = IsQuietTime();

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return quiet && TreatQuietTimeAsFullscreen ? new FullscreenState(true, IntPtr.Zero, "quiet", "专注助手", true) : FullscreenState.None;
        }

        lock (_gate)
        {
            if (_ignoredWindows.Contains(fg))
            {
                // 前台是本程序自己：保持上一次的判定，不要因为自己而误判
                return Current;
            }
        }

        var cls = NativeMethods.GetWindowClass(fg);
        if (IgnoredClasses.Contains(cls))
        {
            return FullscreenState.None;
        }

        if (!NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg))
        {
            return FullscreenState.None;
        }

        if (!NativeMethods.GetWindowRect(fg, out var rect))
        {
            return FullscreenState.None;
        }

        var display = DisplayService.FromWindow(fg);
        var bounds = display?.Bounds;
        if (bounds is null)
        {
            return FullscreenState.None;
        }

        // 允许 1px 的边框误差
        const int tolerance = 1;
        var covers = rect.Left <= bounds.Value.Left + tolerance &&
                     rect.Top <= bounds.Value.Top + tolerance &&
                     rect.Right >= bounds.Value.Right - tolerance &&
                     rect.Bottom >= bounds.Value.Bottom - tolerance;

        if (covers)
        {
            return new FullscreenState(true, fg, cls, NativeMethods.GetWindowTitle(fg), quiet);
        }

        if (TreatQuietTimeAsFullscreen && quiet)
        {
            return new FullscreenState(true, fg, cls, NativeMethods.GetWindowTitle(fg), true);
        }

        return FullscreenState.None;
    }

    private static bool IsQuietTime()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0)
            {
                return false;
            }

            return state is NativeMethods.QUNS_BUSY
                or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN
                or NativeMethods.QUNS_PRESENTATION_MODE
                or NativeMethods.QUNS_QUIET_TIME;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _timer.Stop();
}
