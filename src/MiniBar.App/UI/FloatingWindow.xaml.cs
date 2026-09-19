using System.Windows.Interop;
using System.Windows.Shell;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 插件可以随时创建的通用浮窗 —— 这就是“插件的内容可以出现在任意位置”的落点。
/// 用 WindowChrome 而不是 AllowsTransparency：保留系统原生投影、缩放与吸附行为，同时完全自绘标题栏。
/// </summary>
public partial class FloatingWindow : Window, IPluginWindow
{
    private readonly PluginWindowOptions _options;
    private FrameworkElement? _body;

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

    /// <summary>Window 自身的 Content 是 object、Title 是普通属性，接口要求更具体的类型，这里显式实现避免冲突。</summary>
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

    private void SetBody(FrameworkElement? content)
    {
        _body = content;
        BodyHost.Content = content;
    }

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
