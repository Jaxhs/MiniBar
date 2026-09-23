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
/// 只留这个小浮层显示指定插件的紧凑内容（ICompactContentPlugin）。
///
/// 关键概念 / 新手须知：
///   - WS_EX_NOACTIVATE：一种 Win32 扩展窗口样式，意思是“这个窗口永远不抢焦点”。
///     点它不会把正在全屏的游戏/视频切到后台，所以它只能当信息面板，不能当输入窗口。
///     由 WindowDressingService.Apply(..., MiniOverlay) 套上，MiniWindowAcceptsFocus 关掉时尤其如此。
///   - 锚点(AnchorCorner)：迷你窗口贴在屏幕哪个角，由设置 MiniWindowAnchor 决定；
///     它贴在哪块屏由 AnchorWindow（全屏窗口的 HWND）推断，没有就退回主显示器。
///   - 内容来自插件：ShowContent 接收一个 FrameworkElement，由插件的 ICompactContentPlugin 提供。
///   - 同样支持文件拖放（AllowDrop + DragOver/Drop），落到这里也走 ShellService.HandleDrop。
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

    /// <summary>用于确定迷你窗口贴哪块屏幕的“参照窗口”句柄(HWND) —— 通常是那个全屏窗口，没有就退回主屏。</summary>
    public IntPtr AnchorWindow { get; set; } = IntPtr.Zero;

    public string? CurrentPluginId => _descriptor?.Id;

    /// <summary>
    /// 把某插件的紧凑内容显示进迷你窗口。
    /// 先按插件声明的 PreferredCompactWidth/Height 设最小尺寸（保证内容不被压没），
    /// 再把 content 塞进 MiniContent（一个 ContentControl）。记住用的是哪个插件，释放时才找得到对应插件回收。
    /// </summary>
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

    /// <summary>
    /// 没有可用插件时显示占位文字（比如“未选择迷你模式内容”）。
    /// 临时放一个 TextBlock，样式用主题里的 MutedForegroundBrush（随主题自动变深浅色）。
    /// </summary>
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

    /// <summary>
    /// 清空当前内容，并调用插件的 ReleaseCompactContent 回收资源（try/catch 防止插件释放出错拖垮宿主）。
    /// 切换插件 / 退出迷你模式时调用，避免旧内容的内存一直占着。
    /// </summary>
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

    /// <summary>
    /// 显示迷你浮层（被 FullscreenWatcher 在检测到全屏时调用）。
    /// 先应用设置（决定要不要接受焦点）、按需 Show、重新贴边定位；
    /// 默认不抢焦点(Activate 只在 MiniWindowAcceptsFocus 时调用)，最后 BringToTop 让自己显示在最上层但不夺焦。
    /// </summary>
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

    /// <summary>
    /// 应用迷你窗口相关设置：根据 MiniWindowAcceptsFocus 决定要不要“可激活(抢焦点)”。
    /// 默认 false —— 不抢焦点，这样点迷你窗口不会把全屏程序切到后台。
    /// </summary>
    public void ApplySettings()
    {
        WindowDressingService.SetNoActivate(this, !_settings.Settings.MiniWindowAcceptsFocus);
    }

    /// <summary>
    /// 把迷你窗口贴到 AnchorCorner 指定的屏幕角（由设置 MiniWindowAnchor 决定）。
    /// 步骤：取参照显示器(AnchorWindow→全屏窗口所在屏，否则设置里的显示器) → 按 DPI 缩放把自身尺寸换算成像素
    /// → 调用 DisplayService.ComputeAnchorRect 算出贴角矩形 → MoveWindow 移动过去。
    /// 没 HWND 或不可见时直接返回，避免空操作。
    /// </summary>
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

    /// <summary>
    /// 迷你窗口上右键抬起：弹出迷你模式菜单（退出迷你模式、选择显示哪个插件内容、插件追加项、插件管理）。
    /// 设 e.Handled=true 表示已处理，阻止继续冒泡。
    /// </summary>
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

    /// <summary>
    /// 文件拖到迷你窗口上方：只接受文件拖放（DataFormats.FileDrop），否则显示“不可放下”。
    /// 与任务栏同理，需 AllowDrop=true 且设 e.Handled=true。
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 文件落到迷你窗口：取出路径，交给 ShellService.HandleDrop 分发，落点标记为 MiniWindow（带上当前插件 Id）。
    /// 这样插件也能响应从迷你窗口拖进来的文件；内置兜底同样处理单个 DLL 热加载。
    /// </summary>
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
