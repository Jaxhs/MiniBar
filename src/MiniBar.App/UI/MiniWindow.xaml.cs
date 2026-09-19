using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 迷你模式窗口：当有其它程序全屏（游戏、视频、演示）时，任务栏让位，
/// 只留这个小浮层显示指定插件的紧凑内容。
///
/// 默认带 WS_EX_NOACTIVATE：点它不会把全屏程序切出去，只当作一个信息面板。
/// </summary>
public partial class MiniWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ShellService _shell;
    private PluginDescriptor? _descriptor;

    public MiniWindow(SettingsService settings, ShellService shell)
    {
        _settings = settings;
        _shell = shell;

        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowDressingService.Apply(this, WindowDressing.MiniOverlay, noActivate: !_settings.Settings.MiniWindowAcceptsFocus);
            WindowDressingService.SetDarkTitleBar(this, AppServices.Theme?.IsDark ?? false);
            AppServices.Fullscreen?.IgnoreWindow(new WindowInteropHelper(this).Handle);
        };

        SizeChanged += (_, _) => Reposition();

        MouseRightButtonUp += OnRightButtonUp;
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>用于确定迷你窗口贴哪块屏幕 —— 通常是那个全屏窗口所在的显示器。</summary>
    public IntPtr AnchorWindow { get; set; } = IntPtr.Zero;

    public string? CurrentPluginId => _descriptor?.Id;

    public void ShowContent(PluginDescriptor descriptor, FrameworkElement content)
    {
        _descriptor = descriptor;

        if (descriptor.Compact is { } compact)
        {
            MiniBorder.MinWidth = Math.Max(80, compact.PreferredCompactWidth);
            MiniBorder.MinHeight = Math.Max(28, compact.PreferredCompactHeight);
        }

        MiniContent.Content = content;
    }

    public void ShowPlaceholder(string message)
    {
        _descriptor = null;
        MiniBorder.MinWidth = 180;
        MiniBorder.MinHeight = 34;
        MiniContent.Content = new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedForegroundBrush"),
        };
    }

    public void ReleaseContent()
    {
        MiniContent.Content = null;

        try
        {
            _descriptor?.Compact?.ReleaseCompactContent();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"释放迷你内容失败：{_descriptor?.Id}", ex);
        }

        _descriptor = null;
    }

    public void ShowMinimizedOverlay()
    {
        ApplySettings();

        if (!IsVisible)
        {
            Show();
        }

        Reposition();

        // 不抢焦点：全屏程序会继续保持前台
        if (_settings.Settings.MiniWindowAcceptsFocus)
        {
            Activate();
        }

        WindowDressingService.BringToTop(this);
    }

    public void ApplySettings()
    {
        WindowDressingService.SetNoActivate(this, !_settings.Settings.MiniWindowAcceptsFocus);
    }

    public void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var display = AnchorWindow != IntPtr.Zero
            ? DisplayService.FromWindow(AnchorWindow)
            : null;

        display ??= DisplayService.GetByIndex(_settings.Settings.MonitorIndex);

        var scale = DisplayService.GetScale(hwnd);
        var size = new Size(Math.Max(60, ActualWidth) * scale, Math.Max(28, ActualHeight) * scale);
        var rect = DisplayService.ComputeAnchorRect(display, _settings.Settings.MiniWindowAnchor, size, _settings.Settings.Margin * scale);

        DisplayService.MoveWindow(hwnd, rect.Left, rect.Top);
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("MiniBarContextMenuStyle") };

        menu.Items.Add(MenuBuilder.Item("退出迷你模式", () => _shell.ExitMiniMode(), "glyph:E713"));

        var compactPlugins = _shell.GetPlugins()
            .Where(p => p.Enabled && p.Loaded && p.Capabilities.Contains(PluginCapabilities.Compact))
            .ToList();

        if (compactPlugins.Count > 0)
        {
            menu.Items.Add(MenuBuilder.Sep());
            var sourceMenu = new MenuItem
            {
                Header = "显示内容",
                Style = (Style)FindResource("MiniBarMenuItemStyle"),
            };

            foreach (var info in compactPlugins)
            {
                var captured = info;
                sourceMenu.Items.Add(MenuBuilder.Item(info.Name,
                    () => _shell.SetMiniModePlugin(captured.Id),
                    isChecked: string.Equals(_descriptor?.Id, info.Id, StringComparison.OrdinalIgnoreCase),
                    isCheckable: true));
            }

            menu.Items.Add(sourceMenu);
        }

        var entries = _shell.CollectMenuEntries(PluginMenuTarget.MiniWindow, _descriptor?.Id);
        if (entries.Count > 0)
        {
            menu.Items.Add(MenuBuilder.Sep());
            foreach (var item in MenuBuilder.Convert(entries, _shell.InvokeMenuEntry))
            {
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(MenuBuilder.Sep());
        menu.Items.Add(MenuBuilder.Item("插件管理…", () => _shell.ShowPluginManager(), "glyph:E8FD"));

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        e.Handled = true;
        _shell.HandleDrop(paths, PluginDropTarget.MiniWindow, _descriptor?.Id, DisplayService.GetCursorPositionPixels());
    }
}
