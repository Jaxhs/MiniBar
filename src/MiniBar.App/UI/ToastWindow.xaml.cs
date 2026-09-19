using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MiniBar.App.Interop;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>轻量通知（复用同一个窗口实例，不重复创建）。</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;

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

    public BarWindow? Anchor { get; set; }

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
