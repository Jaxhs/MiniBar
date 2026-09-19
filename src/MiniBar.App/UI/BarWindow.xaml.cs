using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.App.ViewModels;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 任务栏主窗口。
///
/// 它同时承担三件事：
///   1) 常驻显示：TOPMOST + 周期性重申置顶，因此在别的窗口最大化时始终可见；
///      被别的 TOPMOST 窗口遮挡或对方独占全屏时，由 FullscreenWatcher 切到迷你模式。
///   2) 任务图标容器：固定、排序（拖拽）、打开/关闭界面、右键菜单、内嵌插件内容。
///   3) 拖放落点：文件/文件夹丢到图标上或空白处都会分发到插件或内置的“加载 DLL”逻辑。
///
/// 低内存取向：窗口本身不做虚拟化（插件数量天然很少），
/// 内嵌内容按插件缓存，插件卸载时立即释放并回收 ALC。
/// </summary>
public partial class BarWindow : Window
{
    private readonly SettingsService _settings;
    private readonly PluginHost _plugins;
    private readonly ShellService _shell;

    private readonly BarViewModel _vm = new();
    private readonly Dictionary<string, FrameworkElement> _widgets = new(StringComparer.OrdinalIgnoreCase);

    private PluginDescriptor? _pressItem;
    private Point _pressPoint;
    private bool _dragging;
    private bool _suppressNextClick;

    public BarWindow(SettingsService settings, PluginHost plugins, ShellService shell)
    {
        _settings = settings;
        _plugins = plugins;
        _shell = shell;

        InitializeComponent();

        DataContext = _vm;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => Reposition();
        Closed += (_, _) => ReleaseAllWidgets();

        _plugins.LayoutChanged += (_, _) => Dispatcher.BeginInvoke(new Action(SyncPlugins));
        _plugins.PluginUnloaded += (_, descriptor) => ReleaseWidget(descriptor.Id);

        ItemsHost.PreviewMouseLeftButtonDown += OnItemsMouseLeftButtonDown;
        ItemsHost.PreviewMouseMove += OnItemsMouseMove;
        ItemsHost.PreviewMouseLeftButtonUp += OnItemsMouseLeftButtonUp;
        ItemsHost.PreviewMouseRightButtonUp += OnItemsMouseRightButtonUp;
        MouseRightButtonUp += OnWindowMouseRightButtonUp;
        ItemsHost.PreviewMouseDown += OnItemsPreviewMouseDown;
        ItemsHost.ItemContainerGenerator.StatusChanged += (_, _) => AttachWidgets();

        DragOver += OnFileDragOver;
        DragLeave += OnFileDragLeave;
        Drop += OnFileDrop;

        SyncPlugins();
    }

    public BarViewModel ViewModel => _vm;

    private HwndSource? _source;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // 不进 Alt+Tab、不在系统任务栏出现 —— 我们本身就是“任务栏”，不该再占系统任务栏一格
        WindowDressingService.Apply(this, WindowDressing.TaskBar);

        AppServices.Fullscreen?.IgnoreWindow(hwnd);

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(OnWndProc);
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 分辨率 / 缩放 / 显示器拓扑变化后重新贴边
        if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplySettings();
                Reposition();
            }));
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        Reposition();

        // 定位完成后再显示，避免在 (0,0) 闪一下
        BeginAnimation(OpacityProperty, null);
        Opacity = _settings.Settings.BarOpacity;

        SyncPlugins();
    }

    // ================================================================ 外观 / 定位

    public void ApplySettings()
    {
        var settings = _settings.Settings;

        _vm.Edge = settings.Edge;
        _vm.ItemsOrientation = settings.Edge is DockEdge.Left or DockEdge.Right
            ? Orientation.Vertical
            : Orientation.Horizontal;
        _vm.ShowLabels = settings.ShowItemLabels;

        // 把手 / 工具条的停靠方向跟随边缘
        var vertical = _vm.IsVertical;
        DockPanel.SetDock(GripButton, vertical ? Dock.Top : Dock.Left);
        DockPanel.SetDock(RightTools, vertical ? Dock.Bottom : Dock.Right);

        GripButton.Width = vertical ? double.NaN : 30;
        GripButton.Height = vertical ? 30 : double.NaN;

        var display = DisplayService.GetByIndex(settings.MonitorIndex);
        var scale = DisplayService.GetScale(new WindowInteropHelper(this).Handle);

        // SizeToContent 下用 MaxWidth/MaxHeight 约束内容，超出的图标会换行（WrapPanel）
        MaxWidth = Math.Max(180, (display.WorkArea.Width / scale) - (settings.Margin * 2));
        MaxHeight = Math.Max(120, (display.WorkArea.Height / scale) - (settings.Margin * 2));

        if (!_shell.IsMiniMode)
        {
            Opacity = settings.BarOpacity;
        }

        RefreshMiniIndicator();
    }

    /// <summary>把窗口贴回设定的边缘（像素级定位，PerMonitorV2 下最稳）。</summary>
    public void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var settings = _settings.Settings;
        var display = DisplayService.GetByIndex(settings.MonitorIndex);
        var scale = DisplayService.GetScale(hwnd);

        var size = new Size(Math.Max(40, ActualWidth) * scale, Math.Max(30, ActualHeight) * scale);
        var rect = DisplayService.ComputeEdgeRect(display, settings.Edge, size, settings.Margin * scale);

        DisplayService.MoveWindow(hwnd, rect.Left, rect.Top, topmost: true);
    }

    /// <summary>窗口当前的屏幕像素矩形（浮层定位用）。</summary>
    public Rect GetPixelRect() => DisplayService.GetWindowRect(new WindowInteropHelper(this).Handle);

    /// <summary>某个任务图标的屏幕像素矩形；找不到时返回 null。</summary>
    public Rect? GetItemPixelRect(string pluginId)
    {
        for (var i = 0; i < _vm.PinnedItems.Count; i++)
        {
            if (!string.Equals(_vm.PinnedItems[i].Id, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                return null;
            }

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var scale = DisplayService.GetScale(hwnd);
                var origin = container.PointToScreen(new Point(0, 0));
                return new Rect(origin.X, origin.Y, container.ActualWidth * scale, container.ActualHeight * scale);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public double GetDpiScale() => DisplayService.GetScale(new WindowInteropHelper(this).Handle);

    public void SetBarVisible(bool visible)
    {
        if (visible)
        {
            Show();
            Opacity = _settings.Settings.BarOpacity;
            Reposition();
            AppServices.Topmost?.Tick();
        }
        else
        {
            Hide();
        }
    }

    public void RefreshMiniIndicator()
    {
        _vm.MiniIndicatorVisible = _shell.IsMiniMode;
        GripButton.ToolTip = _shell.IsMiniMode ? "MiniBar 菜单（当前：迷你模式）" : "MiniBar 菜单";
    }

    // ================================================================ 插件列表同步

    private void SyncPlugins()
    {
        var pinned = _plugins.PinnedPlugins.ToList();
        var overflow = _plugins.OverflowPlugins.ToList();

        // 先回收已经不在列表里的插件内嵌内容
        foreach (var id in _widgets.Keys.ToArray())
        {
            if (pinned.All(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                ReleaseWidget(id);
            }
        }

        BarViewModel.Sync(_vm.PinnedItems, pinned);
        BarViewModel.Sync(_vm.OverflowItems, overflow);

        _vm.HasOverflow = _vm.OverflowItems.Count > 0;
        _vm.IsEmpty = _vm.PinnedItems.Count == 0;

        AttachWidgets();
    }

    /// <summary>
    /// 把插件提供的“显示区内嵌内容”挂到对应图标上（同一插件只创建一次）。
    ///
    /// 用容器的 Loaded 事件而不是立刻查找：DataTemplate 要等布局阶段才真正应用，
    /// 在那之前按模板名查找会失败。同时不做虚拟化（插件数量天然很少），容器会一直保留。
    /// </summary>
    private void AttachWidgets()
    {
        try
        {
            for (var i = 0; i < _vm.PinnedItems.Count; i++)
            {
                if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                {
                    continue;
                }

                container.Loaded -= OnContainerLoaded;
                container.Loaded += OnContainerLoaded;

                if (container.IsLoaded)
                {
                    AttachWidgetToContainer(container);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("挂载插件内嵌内容时出错", ex);
        }
    }

    private void OnContainerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement container)
        {
            container.Loaded -= OnContainerLoaded;
            AttachWidgetToContainer(container);
        }
    }

    private void AttachWidgetToContainer(FrameworkElement container)
    {
        try
        {
            if (container.DataContext is not PluginDescriptor descriptor ||
                !descriptor.HasBarWidget ||
                !descriptor.IsLoaded)
            {
                return;
            }

            if (FindDescendant<ContentControl>(container, "WidgetHost") is not { } host || host.Content is not null)
            {
                return;
            }

            host.Content = EnsureWidget(descriptor);
        }
        catch (Exception ex)
        {
            AppLog.Warn("挂载插件内嵌内容失败", ex);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name)
            {
                return typed;
            }

            if (FindDescendant<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private FrameworkElement? EnsureWidget(PluginDescriptor descriptor)
    {
        if (_widgets.TryGetValue(descriptor.Id, out var cached))
        {
            return cached;
        }

        if (descriptor.BarWidget is not { } provider || !descriptor.IsLoaded)
        {
            return null;
        }

        try
        {
            var widget = provider.CreateBarWidget();
            if (widget is null)
            {
                return null;
            }

            if (provider.WidgetWidth > 0)
            {
                widget.Width = provider.WidgetWidth;
            }

            if (provider.WidgetHeight > 0)
            {
                widget.Height = provider.WidgetHeight;
            }

            _widgets[descriptor.Id] = widget;
            AppLog.Debug($"已挂载插件内嵌内容：{descriptor.Id}");
            return widget;
        }
        catch (Exception ex)
        {
            AppLog.Error($"创建插件内嵌内容失败：{descriptor.Id}", ex);
            return null;
        }
    }

    private void ReleaseWidget(string pluginId)
    {
        if (!_widgets.Remove(pluginId, out _))
        {
            return;
        }

        var descriptor = _plugins.Find(pluginId);
        try
        {
            descriptor?.BarWidget?.ReleaseBarWidget();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"释放插件内嵌内容失败：{pluginId}", ex);
        }
    }

    private void ReleaseAllWidgets()
    {
        foreach (var id in _widgets.Keys.ToArray())
        {
            ReleaseWidget(id);
        }
    }

    // ================================================================ 鼠标交互

    private PluginDescriptor? DescriptorFromSource(object? source)
    {
        if (source is not DependencyObject node)
        {
            return null;
        }

        var container = ItemsControl.ContainerFromElement(ItemsHost, node) as ContentPresenter;
        return container?.Content as PluginDescriptor;
    }

    /// <summary>点击是否落在插件自定义内容里（那样就不该触发任务图标语义）。</summary>
    private bool IsInteractiveHit(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null && !ReferenceEquals(node, ItemsHost))
        {
            if (node is FrameworkElement { Tag: "Interactive" })
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OnItemsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 中键 = 关闭该插件的界面（任务栏习惯）
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        var descriptor = DescriptorFromSource(e.OriginalSource);
        if (descriptor is null || IsInteractiveHit(e.OriginalSource))
        {
            return;
        }

        e.Handled = true;
        _shell.ClosePanel(descriptor.Id);
        InvokePluginClick(descriptor, BarItemActivationKind.Auxiliary, MouseButton.Middle);
    }

    private void OnItemsMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressItem = null;
        _dragging = false;

        if (IsInteractiveHit(e.OriginalSource))
        {
            return;
        }

        var descriptor = DescriptorFromSource(e.OriginalSource);
        if (descriptor is null)
        {
            return;
        }

        _pressItem = descriptor;
        _pressPoint = e.GetPosition(ItemsHost);
        ItemsHost.CaptureMouse();
    }

    private void OnItemsMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(ItemsHost);

        if (!_dragging &&
            (Math.Abs(position.X - _pressPoint.X) > 6 || Math.Abs(position.Y - _pressPoint.Y) > 6))
        {
            _dragging = true;
            AppLog.Debug($"开始拖拽排序：{_pressItem.Name}");
        }

        if (_dragging)
        {
            LiveReorder(_pressItem, position);
        }
    }

    private void OnItemsMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ItemsHost.ReleaseMouseCapture();

        var pressed = _pressItem;
        var dragged = _dragging;

        _pressItem = null;
        _dragging = false;

        if (pressed is null)
        {
            return;
        }

        if (dragged)
        {
            _suppressNextClick = true;
            _plugins.ApplyPinnedOrder(_vm.PinnedItems.ToList());
            AppLog.Info($"固定顺序已保存：{string.Join(" > ", _vm.PinnedItems.Select(p => p.Name))}");
            return;
        }

        if (_suppressNextClick || IsInteractiveHit(e.OriginalSource))
        {
            _suppressNextClick = false;
            return;
        }

        if (e.ClickCount >= 2)
        {
            InvokePluginClick(pressed, BarItemActivationKind.DoubleClick, MouseButton.Left);
            return;
        }

        // 宿主默认行为：标准任务栏语义 —— 点一下切换该插件的界面
        if (!ManagesOwnActivation(pressed) && pressed.HasPanel)
        {
            _shell.TogglePanel(pressed.Id);
        }

        InvokePluginClick(pressed, BarItemActivationKind.Primary, MouseButton.Left);
    }

    private static bool ManagesOwnActivation(PluginDescriptor descriptor)
    {
        try
        {
            return descriptor.TaskButton?.ManagesOwnActivation ?? false;
        }
        catch
        {
            return false;
        }
    }

    private void InvokePluginClick(PluginDescriptor descriptor, BarItemActivationKind kind, MouseButton button)
    {
        if (descriptor.TaskButton is not { } taskButton || descriptor.Facade is null)
        {
            return;
        }

        try
        {
            var context = new BarItemClickContext(
                descriptor.Facade,
                kind,
                descriptor.IsPinned,
                descriptor.IsPanelOpen,
                _shell.IsMiniMode,
                button,
                Keyboard.Modifiers);

            taskButton.OnClick(context);
        }
        catch (Exception ex)
        {
            AppLog.Error($"插件点击处理异常：{descriptor.Id}", ex);
            _shell.Notify($"「{descriptor.DisplayName}」出错：{ex.Message}", NotificationKind.Error);
        }
    }

    /// <summary>拖拽过程中的实时排序：直接对绑定集合做 Move，视觉立即跟随。</summary>
    private void LiveReorder(PluginDescriptor dragged, Point position)
    {
        var current = _vm.PinnedItems.IndexOf(dragged);
        if (current < 0)
        {
            return;
        }

        var target = -1;
        var vertical = _vm.IsVertical;

        for (var i = 0; i < _vm.PinnedItems.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            Rect bounds;
            try
            {
                bounds = container.TransformToAncestor(ItemsHost)
                    .TransformBounds(new Rect(new Point(0, 0), container.RenderSize));
            }
            catch
            {
                continue;
            }

            var middle = vertical ? bounds.Top + (bounds.Height / 2) : bounds.Left + (bounds.Width / 2);
            var value = vertical ? position.Y : position.X;

            if (value < middle)
            {
                target = i;
                break;
            }
        }

        if (target < 0)
        {
            target = _vm.PinnedItems.Count - 1;
        }

        if (target != current)
        {
            _vm.PinnedItems.Move(current, target);
        }
    }

    private void OnItemsMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var descriptor = DescriptorFromSource(e.OriginalSource);
        if (descriptor is null || IsInteractiveHit(e.OriginalSource))
        {
            return; // 交给窗口级处理 —— 弹出宿主主菜单
        }

        e.Handled = true;
        ShowItemContextMenu(descriptor);
    }

    /// <summary>任务栏任意空白处（含把手、工具按钮）右键都弹主菜单。</summary>
    private void OnWindowMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;
        ShowMainContextMenu();
    }

    // ================================================================ 右键菜单

    public void ShowMainContextMenu()
    {
        var menu = BuildMainContextMenu();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildMainContextMenu()
    {
        var settings = _settings.Settings;
        var menu = new ContextMenu { Style = (Style)FindResource("MiniBarContextMenuStyle") };

        menu.Items.Add(MenuBuilder.Item("插件管理…", () => _shell.ShowPluginManager(), "glyph:E8FD"));

        menu.Items.Add(MenuBuilder.Sep());

        var compactPlugins = _plugins.Plugins
            .Where(p => p.IsEnabled && p.IsLoaded && p.HasCompact)
            .ToList();

        var miniItem = MenuBuilder.Item(
            _shell.IsMiniMode ? "退出迷你模式" : "进入迷你模式",
            () =>
            {
                if (_shell.IsMiniMode)
                {
                    _shell.ExitMiniMode();
                }
                else
                {
                    _shell.EnterMiniMode(string.Empty);
                }
            },
            "glyph:E9A7",
            hint: "全屏程序出现时自动进入",
            isEnabled: compactPlugins.Count > 0 || _shell.IsMiniMode,
            isChecked: _shell.IsMiniMode);

        menu.Items.Add(miniItem);

        if (compactPlugins.Count > 0)
        {
            var sourceMenu = new MenuItem
            {
                Header = "迷你模式显示内容",
                Style = (Style)FindResource("MiniBarMenuItemStyle"),
                Icon = MenuBuilder.Item("x", null).Icon,
            };

            var current = _shell.ResolveMiniPlugin();
            foreach (var descriptor in compactPlugins)
            {
                var captured = descriptor;
                sourceMenu.Items.Add(MenuBuilder.Item(
                    descriptor.DisplayName,
                    () => _shell.SetMiniModePlugin(captured.Id),
                    isChecked: ReferenceEquals(current, descriptor),
                    isCheckable: true));
            }

            menu.Items.Add(sourceMenu);
        }

        menu.Items.Add(MenuBuilder.Item("自动进入迷你模式（检测到全屏时）",
            () =>
            {
                settings.AutoEnterMiniMode = !settings.AutoEnterMiniMode;
                _settings.NotifyChanged();
                _shell.ApplySettings();
            },
            hint: null, isChecked: settings.AutoEnterMiniMode, isCheckable: true));

        menu.Items.Add(MenuBuilder.Item("全屏时彻底隐藏任务栏",
            () =>
            {
                settings.HideBarWhenFullscreen = !settings.HideBarWhenFullscreen;
                _settings.NotifyChanged();
            },
            isChecked: settings.HideBarWhenFullscreen, isCheckable: true));

        menu.Items.Add(MenuBuilder.Sep());

        // 任务栏位置
        var edgeMenu = new MenuItem
        {
            Header = "任务栏位置",
            Style = (Style)FindResource("MiniBarMenuItemStyle"),
        };
        foreach (var (edge, label, glyph) in new[]
                 {
                     (DockEdge.Bottom, "底部", "glyph:E74B"),
                     (DockEdge.Top, "顶部", "glyph:E74A"),
                     (DockEdge.Left, "左侧", "glyph:E76B"),
                     (DockEdge.Right, "右侧", "glyph:E76C"),
                 })
        {
            var captured = edge;
            edgeMenu.Items.Add(MenuBuilder.Item(label,
                () =>
                {
                    settings.Edge = captured;
                    _settings.NotifyChanged();
                    _shell.ApplySettings();
                },
                glyph, isChecked: settings.Edge == edge, isCheckable: true));
        }

        menu.Items.Add(edgeMenu);

        var themeMenu = new MenuItem
        {
            Header = "主题",
            Style = (Style)FindResource("MiniBarMenuItemStyle"),
        };
        foreach (var (name, label) in new[] { ("Light", "浅色"), ("Dark", "深色"), ("System", "跟随系统") })
        {
            var captured = name;
            themeMenu.Items.Add(MenuBuilder.Item(label,
                () =>
                {
                    settings.Theme = captured;
                    _settings.NotifyChanged();
                    AppServices.Theme.Apply(captured);
                    _shell.OnThemeChanged();
                },
                isChecked: string.Equals(settings.Theme, name, StringComparison.OrdinalIgnoreCase),
                isCheckable: true));
        }

        menu.Items.Add(themeMenu);

        menu.Items.Add(MenuBuilder.Item("显示插件文字标签",
            () =>
            {
                settings.ShowItemLabels = !settings.ShowItemLabels;
                _settings.NotifyChanged();
                _shell.ApplySettings();
            },
            isChecked: settings.ShowItemLabels, isCheckable: true));

        menu.Items.Add(MenuBuilder.Item("空闲时释放内存",
            () =>
            {
                settings.TrimWorkingSetOnIdle = !settings.TrimWorkingSetOnIdle;
                _settings.NotifyChanged();
            },
            hint: "把未使用内存还给系统",
            isChecked: settings.TrimWorkingSetOnIdle, isCheckable: true));

        menu.Items.Add(MenuBuilder.Sep());

        // 插件追加的菜单项（位置 = 任务栏空白处）
        var pluginEntries = _shell.CollectMenuEntries(PluginMenuTarget.BarBackground, null);
        if (pluginEntries.Count > 0)
        {
            foreach (var item in MenuBuilder.Convert(pluginEntries, _shell.InvokeMenuEntry))
            {
                menu.Items.Add(item);
            }

            menu.Items.Add(MenuBuilder.Sep());
        }

        menu.Items.Add(MenuBuilder.Item("打开插件目录", () => OpenInExplorer(AppPaths.UserPluginDirectory), "glyph:E838"));
        menu.Items.Add(MenuBuilder.Item("打开配置目录", () => OpenInExplorer(AppPaths.ConfigDirectory), "glyph:E8A5"));
        menu.Items.Add(MenuBuilder.Item("查看日志", () => OpenInExplorer(AppPaths.LogFile), "glyph:E9D9"));

        menu.Items.Add(MenuBuilder.Sep());
        menu.Items.Add(MenuBuilder.Item("退出 MiniBar", () => Application.Current.Shutdown(), "glyph:E7E8"));

        return menu;
    }

    private void ShowItemContextMenu(PluginDescriptor descriptor)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("MiniBarContextMenuStyle") };
        var settings = _settings.Settings;

        if (descriptor.HasPanel)
        {
            menu.Items.Add(MenuBuilder.Item(
                descriptor.IsPanelOpen ? "关闭界面" : "打开界面",
                () => _shell.TogglePanel(descriptor.Id),
                descriptor.IsPanelOpen ? "glyph:E70D" : "glyph:E8A7"));
        }

        if (descriptor.HasCompact)
        {
            var isMiniSource = ReferenceEquals(_shell.ResolveMiniPlugin(), descriptor);
            menu.Items.Add(MenuBuilder.Item("用作迷你模式内容",
                () => _shell.SetMiniModePlugin(isMiniSource ? string.Empty : descriptor.Id),
                "glyph:E9A7",
                isChecked: isMiniSource,
                isCheckable: true));
        }

        if (descriptor.HasTaskButton || descriptor.HasBarWidget)
        {
            menu.Items.Add(MenuBuilder.Sep());
            menu.Items.Add(MenuBuilder.Item(
                descriptor.IsPinned ? "从任务栏取消固定" : "固定到任务栏",
                () => _plugins.SetPinned(descriptor, !descriptor.IsPinned),
                descriptor.IsPinned ? "glyph:E77A" : "glyph:E718"));
        }

        if (descriptor.IsPinned && _vm.PinnedItems.Count > 1)
        {
            menu.Items.Add(MenuBuilder.Item("移到最前",
                () =>
                {
                    _vm.PinnedItems.Move(_vm.PinnedItems.IndexOf(descriptor), 0);
                    _plugins.ApplyPinnedOrder(_vm.PinnedItems.ToList());
                },
                "glyph:E74A"));

            menu.Items.Add(MenuBuilder.Item("移到最后",
                () =>
                {
                    _vm.PinnedItems.Move(_vm.PinnedItems.IndexOf(descriptor), _vm.PinnedItems.Count - 1);
                    _plugins.ApplyPinnedOrder(_vm.PinnedItems.ToList());
                },
                "glyph:E74B"));
        }

        var pluginEntries = _shell.CollectMenuEntries(PluginMenuTarget.BarItem, descriptor.Id);
        if (pluginEntries.Count > 0)
        {
            menu.Items.Add(MenuBuilder.Sep());
            foreach (var item in MenuBuilder.Convert(pluginEntries, _shell.InvokeMenuEntry))
            {
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(MenuBuilder.Sep());

        menu.Items.Add(MenuBuilder.Item("重新加载插件", () =>
        {
            _plugins.Reload(descriptor);
            _shell.Notify($"已重新加载：{descriptor.Name}", NotificationKind.Success);
        }, "glyph:E72C"));

        menu.Items.Add(MenuBuilder.Item("禁用插件", () =>
        {
            _plugins.SetEnabled(descriptor, false);
            _shell.Notify($"已禁用：{descriptor.Name}（文件与配置保留）", NotificationKind.Info);
        }, "glyph:E769"));

        menu.Items.Add(MenuBuilder.Item("打开所在目录",
            () => OpenInExplorer(Path.GetDirectoryName(descriptor.FilePath) ?? AppPaths.UserPluginDirectory),
            "glyph:E838"));

        menu.Items.Add(MenuBuilder.Item("打开插件数据目录",
            () => OpenInExplorer(AppPaths.GetPluginDataDirectory(descriptor.Id)),
            "glyph:E8A5"));

        menu.Items.Add(MenuBuilder.Sep());

        var canDelete = IsInsidePluginDirectory(descriptor.FilePath);
        menu.Items.Add(MenuBuilder.Item("卸载并删除插件文件", () =>
            {
                if (settings.ConfirmBeforeUninstall)
                {
                    var answer = MessageBox.Show(
                        $"确定要卸载并删除插件文件吗？\n\n{descriptor.DisplayName}\n{descriptor.FilePath}\n\n" +
                        "该操作会先卸载插件，再删除插件 DLL 及其空目录（不可恢复）。",
                        "卸载并删除插件",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning,
                        MessageBoxResult.Cancel);

                    if (answer != MessageBoxResult.OK)
                    {
                        return;
                    }
                }

                _plugins.Unload(descriptor, deleteFile: true);
            },
            "glyph:E74D",
            isEnabled: canDelete));

        if (descriptor.HasError)
        {
            menu.Items.Add(MenuBuilder.Sep());
            menu.Items.Add(MenuBuilder.Item($"加载错误：{descriptor.Error}", null, "glyph:E783", isEnabled: false));
        }

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void ShowOverflowMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("MiniBarContextMenuStyle") };

        foreach (var descriptor in _vm.OverflowItems)
        {
            var submenu = new MenuItem
            {
                Header = descriptor.DisplayName,
                Style = (Style)FindResource("MiniBarMenuItemStyle"),
            };

            if (descriptor.HasPanel)
            {
                submenu.Items.Add(MenuBuilder.Item("打开界面", () => _shell.TogglePanel(descriptor.Id), "glyph:E8A7"));
            }

            submenu.Items.Add(MenuBuilder.Item("固定到任务栏",
                () => _plugins.SetPinned(descriptor, true),
                "glyph:E718"));

            submenu.Items.Add(MenuBuilder.Item("禁用插件",
                () => _plugins.SetEnabled(descriptor, false),
                "glyph:E769"));

            menu.Items.Add(submenu);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(MenuBuilder.Item("没有未固定的插件", null, isEnabled: false));
        }

        menu.Items.Add(MenuBuilder.Sep());
        menu.Items.Add(MenuBuilder.Item("插件管理…", () => _shell.ShowPluginManager(), "glyph:E8FD"));

        menu.PlacementTarget = OverflowButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ================================================================ 拖放

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        ShellBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        e.Handled = true;
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        ShellBorder.BorderBrush = (Brush)FindResource("BarBorderBrush");
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        OnFileDragLeave(sender, e);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        e.Handled = true;

        var target = DescriptorFromSource(e.OriginalSource);
        var targetPluginId = IsInteractiveHit(e.OriginalSource) ? null : target?.Id;

        _shell.HandleDrop(
            paths,
            target is null ? PluginDropTarget.Bar : PluginDropTarget.BarItem,
            targetPluginId,
            DisplayService.GetCursorPositionPixels());
    }

    // ================================================================ 按钮

    private void OnGripClick(object sender, RoutedEventArgs e) => ShowMainContextMenu();

    private void OnOverflowClick(object sender, RoutedEventArgs e) => ShowOverflowMenu();

    private void OnAddClick(object sender, RoutedEventArgs e) => _shell.ShowPluginManager();

    // ================================================================ 工具

    private static bool IsInsidePluginDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(AppPaths.UserPluginDirectory), StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(Path.GetFullPath(AppPaths.BuiltInPluginDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开资源管理器失败：{path}", ex);
        }
    }
}
