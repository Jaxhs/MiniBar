using System.Collections.Generic;
using System.Windows.Threading;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

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
