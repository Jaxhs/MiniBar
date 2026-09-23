using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MiniBar.App.Interop;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 轻量通知（Toast）：屏幕右下角/任务栏附近的短暂提示，比如“已重新加载插件”“出错：…”。
///
/// 关键概念 / 新手须知：
///   - 复用同一个窗口实例：整个程序只建一个 ToastWindow，反复 Show/Hide 复用，不每次 new（省开销、避免堆积）。
///   - 自动消失：用 DispatcherTimer 计时，到点 Hide。DispatcherTimer 是跑在 UI 线程的计时器，
///     回调里能直接改 WPF 属性，不用来跨线程。点一下也能立即关。
///   - 多个提示“排队”：这里靠调用方错开/覆盖——每次 ShowMessage 都会重置计时器，所以连续提示时
///     后一条会顶掉前一条并重新计时（表现为“最新一条留住”）。真正串行排队由通知服务层控制。
///   - 定位跟随任务栏(Anchor)：Reposition 会算到任务栏旁边、且不被它挡住。
/// </summary>
public partial class ToastWindow : Window
{
    // DispatcherTimer：运行在 UI 线程的定时器。到点触发 Tick，我们在里面停表并 Hide 窗口。
    // Interval 默认 2.5 秒，每次 ShowMessage 会按调用方给的 duration 重新设置。
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// 构造：加载 XAML、建计时器(到点自动 Hide)、套“迷你浮层”窗口样式(WS_EX_NOACTIVATE 之类)、
    /// 绑定点击关闭、尺寸变化时重新定位。整个程序只构造一次，之后复用。
    /// </summary>
    public ToastWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };

        SourceInitialized += (_, _) =>
        {
            WindowDressingService.Apply(this, WindowDressing.MiniOverlay);
        };

        MouseLeftButtonUp += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };

        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>定位参照：通知要贴在哪条任务栏旁边。为 null 时退回主屏右下角。</summary>
    public BarWindow? Anchor { get; set; }

    /// <summary>
    /// 显示一条通知。
    /// 步骤：记锚点 → 填文字 → 按 kind(成功/警告/错误/信息)选字形图标和颜色（用 DynamicResource 引用的画刷，主题变会自动变）
    /// → 按需 Show → 重新定位 → 重置并启动计时器（duration 后自动消失）。
    /// 调用方连续调用时，这里会重置计时器，所以后一条提示顶掉前一条并重新计时。
    /// </summary>
    public void ShowMessage(string message, NotificationKind kind, TimeSpan duration, BarWindow? bar)
    {
        Anchor = bar;
        MessageText.Text = message;

        (IconText.Text, IconText.Foreground) = kind switch
        {
            NotificationKind.Success => ("\uE73E", (Brush)FindResource("AccentBrush")),
            NotificationKind.Warning => ("\uE7BA", (Brush)FindResource("DangerBrush")),
            NotificationKind.Error => ("\uEA39", (Brush)FindResource("DangerBrush")),
            _ => ("\uE946", (Brush)FindResource("AccentBrush")),
        };

        if (!IsVisible)
        {
            Show();
        }

        Reposition();

        _timer.Interval = duration;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// 把通知定位到任务栏旁边、且不被它遮住；没有任务栏参照时退回主屏右下角。
    /// 步骤：算自身像素尺寸 → 取任务栏所在显示器(或主屏) → 默认放在任务栏上方居中，若超出工作区上边界就改放任务栏下方
    /// → 最后用 Clamp 限制在工作区范围内，MoveWindow 移动。没 HWND/不可见时直接返回。
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var scale = DisplayService.GetScale(hwnd);
        var width = Math.Max(80, ActualWidth) * scale;
        var height = Math.Max(28, ActualHeight) * scale;

        var barRect = Anchor?.GetPixelRect() ?? Rect.Empty;
        var display = barRect.IsEmpty
            ? DisplayService.GetPrimary()
            : DisplayService.FromWindow(new WindowInteropHelper(Anchor!).Handle) ?? DisplayService.GetPrimary();

        var work = display.WorkArea;
        double left;
        double top;

        if (!barRect.IsEmpty)
        {
            // 靠近任务栏显示，但不遮住它
            left = barRect.Left + ((barRect.Width - width) / 2);
            top = barRect.Top - height - 8;

            if (top < work.Top)
            {
                top = barRect.Bottom + 8;
            }
        }
        else
        {
            left = work.Left + ((work.Width - width) / 2);
            top = work.Bottom - height - 12;
        }

        left = Math.Max(work.Left + 4, Math.Min(left, work.Right - width - 4));
        top = Math.Max(work.Top + 4, Math.Min(top, work.Bottom - height - 4));

        DisplayService.MoveWindow(hwnd, left, top);
    }
}
