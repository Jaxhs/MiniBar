using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

public enum WindowDressing
{
    /// <summary>任务栏主窗口：不进 Alt+Tab、不接受激活。</summary>
    TaskBar,

    /// <summary>迷你窗口：不进 Alt+Tab，默认不接受激活（全屏游戏时不抢焦点）。</summary>
    MiniOverlay,

    /// <summary>普通浮层：可激活、可选择文本。</summary>
    Interactive,
}

/// <summary>
/// 窗口外观与置顶保持。
///
/// 关于“常驻显示、不被其它最大化窗口遮挡”：
///   被最大化的窗口并不是 TOPMOST 窗口，所以只要本程序是 TOPMOST 就永远压在最上面。
///   但某些程序（游戏、播放器、部分安装器）会用 TOPMOST 或独占全屏把置顶抢走 —— 所以需要
///   1) 周期性重申 HWND_TOPMOST（TopmostGuard）；
///   2) 检测到真正的全屏时主动隐藏任务栏、只留迷你窗口（FullscreenWatcher）。
/// </summary>
public static class WindowDressingService
{
    public static void Apply(Window window, WindowDressing dressing, bool noActivate = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 一律不进 Alt+Tab、不在任务栏出现
        NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW, true);

        switch (dressing)
        {
            case WindowDressing.MiniOverlay:
                // 全屏游戏/演示时点击迷你窗口不应抢走焦点，所以默认 NOACTIVATE
                NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE, true);
                break;

            default:
                NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE, noActivate);
                break;
        }

        // Win11：圆角（不支持时静默失败）
        var preferRound = 2;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preferRound, sizeof(int));
    }

    public static void SetNoActivate(Window window, bool noActivate)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE, noActivate);
    }

    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var value = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public static void BringToTop(Window window, bool topmost = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(hwnd, topmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}

/// <summary>
/// 周期性重申置顶。
/// 只做一次 SetWindowPos 的代价极小（微秒级），但能覆盖“被别的 TOPMOST 窗口盖住”这一常见场景。
/// 只有在本程序可见时才动作，所以全屏隐藏期间不会产生任何系统调用。
/// </summary>
public sealed class TopmostGuard : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private bool _enabled = true;

    public TopmostGuard(Window window, int intervalMs)
    {
        _window = window;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(500, intervalMs)),
        };
        _timer.Tick += (_, _) => Tick();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    public void Start()
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Tick()
    {
        if (!_enabled || !_window.IsVisible || _window.Visibility != Visibility.Visible)
        {
            return;
        }

        WindowDressingService.BringToTop(_window, true);
    }

    public void Dispose() => _timer.Stop();
}
