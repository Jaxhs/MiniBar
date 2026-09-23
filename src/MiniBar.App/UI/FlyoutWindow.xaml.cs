using System.Windows.Interop;
using System.Windows.Threading;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 插件界面浮层。同一时刻只存在一个实例并被复用 —— 关闭时把内容置空，
/// 让插件的 <c>ReleaseContent</c> 有机会停掉计时器、释放内存。
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly SettingsService _settings;
    private PluginDescriptor? _descriptor;
    private bool _closingSelf;

    public FlyoutWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            Interop.WindowDressingService.Apply(this, WindowDressing.Interactive);
            Interop.WindowDressingService.SetDarkTitleBar(this, AppServices.Theme?.IsDark ?? false);
            AppServices.Fullscreen?.IgnoreWindow(new WindowInteropHelper(this).Handle);
        };

        Deactivated += OnDeactivated;
        SizeChanged += (_, _) => Reposition();
        Closed += (_, _) => PanelClosed?.Invoke(this, EventArgs.Empty);
    }

    public BarWindow? Bar { get; set; }

    public string? CurrentPluginId => _descriptor?.Id;

    public bool IsPinnedOpen { get; private set; }

    public event EventHandler? PanelClosed;

    public void ShowPanel(PluginDescriptor descriptor, FrameworkElement content, IPanelHost panelHost)
    {
        _descriptor = descriptor;
        _closingSelf = false;

        TitleText.Text = descriptor.Panel?.PanelTitle ?? descriptor.DisplayName;
        TitleText.ToolTip = TitleText.Text; // 标题太长被省略号截断时，鼠标悬停能看全
        ContentHost.Content = content;

        ApplySizeConstraints(descriptor);

        if (!IsVisible)
        {
            Show();
        }

        Reposition();
        Activate();
        AppServices.Topmost?.Tick();
    }

    /// <summary>
    /// 计算浮层的尺寸约束。
    ///
    /// 宽度：插件声明 <c>PreferredWidth</c>，再用当前屏幕宽度兜底；
    /// 高度：**不写死** —— 窗体是 <c>SizeToContent="Height"</c>，内容有多少就长多高，
    ///       但 <c>MaxHeight</c> 顶到"屏幕可用高度 - 任务栏 - 间隙"，
    ///       于是内容一多就出现滚动条，而不是被切掉看不见（这正是"插件内容过多显示不全"的修法）。
    /// </summary>
    private void ApplySizeConstraints(PluginDescriptor? descriptor)
    {
        var scale = DisplayService.GetScale(new WindowInteropHelper(this).Handle);
        var work = ResolveDisplay().WorkArea;

        // 工作区是物理像素，WPF 的 Width/Height/MaxHeight 是 DIP（逻辑像素），所以要除以缩放比
        var maxWidthDip = Math.Max(240, (work.Width / scale) - 24);

        // 任务栏自身的厚度 + 间隙 + 窗体投影留白，都从可用高度里扣掉
        var barThickness = AppServices.Settings.Settings.UseAppBar
            ? AppServices.Settings.Settings.AppBarThickness
            : 60;

        var maxHeightDip = Math.Max(200, (work.Height / scale) - barThickness - 48);

        Width = Math.Clamp(descriptor?.Panel?.PreferredWidth ?? 380, 240, maxWidthDip);
        MaxWidth = maxWidthDip;
        MaxHeight = maxHeightDip;
    }

    /// <summary>浮层所在显示器：优先跟随任务栏，其次跟随设置里的显示器序号。</summary>
    private DisplayInfo ResolveDisplay()
    {
        if (Bar is not null)
        {
            var barHwnd = new WindowInteropHelper(Bar).Handle;
            if (DisplayService.FromWindow(barHwnd) is { } fromBar)
            {
                return fromBar;
            }
        }

        return DisplayService.GetByIndex(_settings.Settings.MonitorIndex);
    }

    /// <summary>释放内容并隐藏（不销毁窗口，下次复用）。</summary>
    public void ClosePanel()
    {
        _closingSelf = true;
        ContentHost.Content = null;
        _descriptor = null;
        IsPinnedOpen = false;
        PinGlyph.Text = "\uE718";

        Hide();

        // 释放视觉效果上的引用，让面板内容尽快被回收
        GC.Collect(0, GCCollectionMode.Optimized);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsPinnedOpen || !_settings.Settings.CloseFlyoutOnDeactivate || _closingSelf)
        {
            return;
        }

        // 菜单/弹窗/浮动窗口都算“还在本程序里”，不能因为点它们就把面板关掉
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible || IsPinnedOpen)
            {
                return;
            }

            if (NativeMethods.IsOwnProcessForeground())
            {
                return;
            }

            PanelClosed?.Invoke(this, EventArgs.Empty);
        }), DispatcherPriority.Background);
    }

    /// <summary>相对任务栏浮动：底部任务栏 → 浮层在其正上方；左右任务栏 → 浮层在其内侧。</summary>
    public void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var scale = DisplayService.GetScale(hwnd);
        var widthPx = ActualWidth * scale;
        var heightPx = ActualHeight * scale;

        var anchor = Bar?.GetPixelRect() ?? Rect.Empty;
        var display = ResolveDisplay();

        var work = display.WorkArea;
        double left;
        double top;
        const double gap = 10;

        if (anchor.IsEmpty)
        {
            left = work.Left + ((work.Width - widthPx) / 2);
            top = work.Bottom - heightPx - gap;
        }
        else
        {
            switch (_settings.Settings.Edge)
            {
                case DockEdge.Top:
                    left = anchor.Left + ((anchor.Width - widthPx) / 2);
                    top = anchor.Bottom + gap;
                    break;

                case DockEdge.Left:
                    left = anchor.Right + gap;
                    top = anchor.Top + ((anchor.Height - heightPx) / 2);
                    break;

                case DockEdge.Right:
                    left = anchor.Left - widthPx - gap;
                    top = anchor.Top + ((anchor.Height - heightPx) / 2);
                    break;

                default:
                    left = anchor.Left + ((anchor.Width - widthPx) / 2);
                    top = anchor.Top - heightPx - gap;
                    break;
            }
        }

        // 越界时贴边，避免浮层被切掉
        left = Math.Max(work.Left + 4, Math.Min(left, work.Right - widthPx - 4));
        top = Math.Max(work.Top + 4, Math.Min(top, work.Bottom - heightPx - 4));

        DisplayService.MoveWindow(hwnd, left, top);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => PanelClosed?.Invoke(this, EventArgs.Empty);

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        IsPinnedOpen = !IsPinnedOpen;
        PinGlyph.Text = IsPinnedOpen ? "\uE77A" : "\uE718";
        PinButton.ToolTip = IsPinnedOpen ? "取消钉住" : "钉住：失去焦点时不自动关闭";
        AppLog.Debug($"面板钉住状态：{IsPinnedOpen}");
    }
}
