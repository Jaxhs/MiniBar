using System.Windows.Interop;
using System.Windows.Shell;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 插件可以随时创建的通用浮窗 —— 这就是“插件的内容可以出现在任意位置”的落点。
/// 插件通过 IShellService.CreateWindow 拿到一个 IPluginWindow，背后就是本类。
///
/// 关键概念 / 新手须知：
///   - WindowChrome vs AllowsTransparency：如果用 AllowsTransparency=True 做无边框自绘，会丢掉系统原生投影、
///     边缘拖动缩放、Aero 吸附等。改用 WindowChrome 可以在“保留系统边框行为(投影/缩放)”的同时，把标题栏高度设为 0、
///     自己画一个标题栏，体验更原生。
///   - 实现 IPluginWindow：插件拿到的接口只暴露 Content/Title/Show/Hide 等，本类用“显式接口实现”
///     (FrameworkElement IPluginWindow.Content) 来提供，避免和 Window 自带的 Content/Title 属性命名冲突。
///   - Topmost：插件可要求浮窗置顶(Pin)，对应标题栏上的图钉按钮。
///   - 这是 partial class：XAML 自动生成的部分与本文件合并成同一个类，所以这里能直接访问 TitleText/BodyHost 等 x:Name 字段。
/// </summary>
public partial class FloatingWindow : Window, IPluginWindow
{
    private readonly PluginWindowOptions _options;
    private FrameworkElement? _body;

    /// <summary>
    /// 构造：按插件给的标题/内容/选项(options)配置窗口——置顶图标、最小尺寸、是否可缩放、是否显示标题栏
    /// （不显示时用 WindowChrome 把 CaptionHeight 设为 0 实现纯自绘）。最后挂 SourceInitialized 钩子，
    /// 在窗口真正有 HWND 后套样式、设深色标题栏、忽略全屏检测、定位。
    /// </summary>
    public FloatingWindow(string title, FrameworkElement content, PluginWindowOptions options)
    {
        _options = options;

        InitializeComponent();

        Title = title;
        TitleText.Text = title;

        Topmost = options.Topmost;
        PinGlyph.Text = options.Topmost ? "\uE77A" : "\uE718";
        PinGlyph.Opacity = options.Topmost ? 1.0 : 0.45;

        Width = Math.Max(120, options.Width);
        Height = Math.Max(80, options.Height);

        if (options.MinWidth is { } minWidth)
        {
            MinWidth = minWidth;
        }

        if (options.MinHeight is { } minHeight)
        {
            MinHeight = minHeight;
        }

        if (!options.Resizable)
        {
            ResizeMode = ResizeMode.NoResize;
        }

        if (!options.ShowTitleBar)
        {
            TitleBar.Visibility = Visibility.Collapsed;
            WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = options.Resizable ? new Thickness(6) : new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });
        }

        SetBody(content);

        SourceInitialized += (_, _) =>
        {
            WindowDressingService.Apply(this, WindowDressing.Interactive);
            WindowDressingService.SetDarkTitleBar(this, AppServices.Theme?.IsDark ?? false);
            AppServices.Fullscreen?.IgnoreWindow(new WindowInteropHelper(this).Handle);
            PositionWindow();
        };
    }

    /// <summary>
    /// 显式实现 IPluginWindow.Content。
    /// 为什么显式实现：Window 自己已经有 object Content 和 string Title 属性，若直接写 public 会被认为是同一成员。
    /// 用“接口名.成员名”的写法把插件要的强类型版本藏到接口后面，Window 自身的属性不受影响。插件只能透过 IPluginWindow 访问它。
    /// </summary>
    FrameworkElement IPluginWindow.Content
    {
        get => _body!;
        set => SetBody(value);
    }

    string IPluginWindow.Title
    {
        get => Title;
        set
        {
            Title = value ?? string.Empty;
            TitleText.Text = value ?? string.Empty;
        }
    }

    void IPluginWindow.Show() => Show();

    void IPluginWindow.Hide() => Hide();

    void IPluginWindow.Activate() => Activate();

    void IPluginWindow.Close() => Close();

    /// <summary>把插件给的内容元素塞进 BodyHost（一个 ContentControl）。同时缓存到 _body 供接口读取。</summary>
    private void SetBody(FrameworkElement? content)
    {
        _body = content;
        BodyHost.Content = content;
    }

    /// <summary>
    /// 初次显示时定位：默认居中到光标所在显示器；若插件给了 ScreenPosition 就用它。
    /// 用 DPI 缩放把 WPF 尺寸换算成像素，并用 Clamp 限制在工作区内（不会被甩到屏幕外）。
    /// 必须在 SourceInitialized 之后做，因为那时才有 HWND、MoveWindow 才能生效。
    /// </summary>
    private void PositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 居中到光标所在显示器
        var cursor = DisplayService.GetCursorPositionPixels();
        var display = DisplayService.FromPixel((int)cursor.X, (int)cursor.Y) ?? DisplayService.GetPrimary();
        var work = display.WorkArea;
        var scale = DisplayService.GetScale(hwnd);

        var widthPx = Width * scale;
        var heightPx = Height * scale;

        var left = _options.ScreenPosition?.X ?? (work.Left + ((work.Width - widthPx) / 2));
        var top = _options.ScreenPosition?.Y ?? (work.Top + ((work.Height - heightPx) / 2));

        left = Math.Max(work.Left, Math.Min(left, work.Right - Math.Min(widthPx, work.Width)));
        top = Math.Max(work.Top, Math.Min(top, work.Bottom - Math.Min(heightPx, work.Height)));

        DisplayService.MoveWindow(hwnd, left, top, Topmost);
    }

    /// <summary>
    /// 标题栏关闭按钮：若插件要求“关闭=隐藏(HideInsteadOfClose)”就 Hide 而不是 Close，
    /// 这样插件还能再次 Show 复用同一窗口；否则真正 Close（窗口销毁、资源释放）。
    /// </summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_options.HideInsteadOfClose)
        {
            Hide();
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// 标题栏图钉按钮：切换 Topmost（置顶/取消置顶）。
    /// 置顶时更新图标并显示，再 BringToTop 让自己立刻浮到最上层；取消置顶则回到普通层级。
    /// </summary>
    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinGlyph.Text = Topmost ? "\uE77A" : "\uE718";
        PinGlyph.Opacity = Topmost ? 1.0 : 0.45;

        if (Topmost)
        {
            WindowDressingService.BringToTop(this, true);
        }
    }
}
