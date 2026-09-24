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
    /// <summary>
    /// 给窗口套上"工具窗口"外观与置顶之外的样式。
    /// 关键是两个<b>扩展窗口样式位</b>（用 NativeMethods.SetExStyle 按位 OR/NOT 改 <c>GWL_EXSTYLE</c>）：
    /// <list type="bullet">
    ///   <item><description><c>WS_EX_TOOLWINDOW</c>：让窗口不出现在 Alt+Tab 切换列表、也不显示在自己任务栏上——本程序是常驻工具条，不该和正常程序抢位置。</description></item>
    ///   <item><description><c>WS_EX_NOACTIVATE</c>：窗口被点中也不抢焦点，迷你窗口/提示气泡用这个，全屏游戏时点击不会把焦点从游戏里抢走。</description></item>
    /// </list>
    /// 另外对 Win11 请求圆角（不支持时静默失败）。窗口句柄同样通过 WindowInteropHelper 取得。
    /// </summary>
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

    /// <summary>
    /// 设置<b>深色标题栏</b>。底层是 <c>DwmSetWindowAttribute</c> 的
    /// <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>：让窗口标题栏（文字与边框）跟随深色外观。
    /// 这是 Win10 20H1 / Win11 才支持的属性，老系统会调用失败，这里静默忽略即可。
    /// 当程序主题切到深色时调用 <c>SetDarkTitleBar(window, true)</c>，浅色时传 false。
    /// </summary>
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
        if (!_enabled || _suspendCount > 0 || !_window.IsVisible || _window.Visibility != Visibility.Visible)
        {
            return;
        }

        WindowDressingService.BringToTop(_window, true);
    }

    // ---------------------------------------------------------------- 暂停（菜单打开期间）

    private int _suspendCount;

    /// <summary>当前是否处于"暂停重申"状态（右键菜单打开期间为真）。</summary>
    public bool IsSuspended => _suspendCount > 0;

    /// <summary>
    /// 暂停"周期性重申置顶"，返回的令牌 <c>Dispose</c> 后自动恢复。
    ///
    /// <para><b>为什么必须能暂停：</b>重申置顶用的是 <c>SetWindowPos(HWND_TOPMOST)</c>，
    /// 而"把一个窗口再设一次 TOPMOST"会把<b>它挪到 TOPMOST 组的最前面</b>。
    /// 菜单的浮层也是 TOPMOST（我们刚把它提上去），于是每过 <c>TopmostGuardIntervalMs</c>
    /// 任务栏就被重新提到菜单上面 —— 实测：菜单刚弹出时 z 序是「菜单 &gt; 任务栏」，
    /// 4.8 秒（跨过一次重申）后变成「任务栏 &gt; 菜单」，也就是用户看到的"菜单还是被遮住"。</para>
    ///
    /// <para>用计数而不是布尔：可能同时挂着多个浮层（子菜单、通知气泡）。</para>
    /// </summary>
    public IDisposable Suspend()
    {
        _suspendCount++;
        return new Suspension(this);
    }

    private void Resume()
    {
        if (_suspendCount > 0)
        {
            _suspendCount--;
        }

        if (_suspendCount == 0)
        {
            // 立刻重申一次，让任务栏回到最前（否则要等下一个计时间隔）
            Tick();
        }
    }

    /// <summary>暂停令牌：Dispose 即恢复（可安全重复 Dispose）。</summary>
    private sealed class Suspension : IDisposable
    {
        private readonly TopmostGuard _owner;
        private bool _disposed;

        public Suspension(TopmostGuard owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Resume();
        }
    }

    public void Dispose() => _timer.Stop();
}
