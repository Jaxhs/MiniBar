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
    // 三个核心服务都通过构造函数注入（依赖注入）：
    private readonly SettingsService _settings;   // 读取/保存用户设置（停靠边、透明度、是否用 AppBar 等）
    private readonly PluginHost _plugins;         // 插件宿主：固定、排序、加载/卸载、热插拔
    private readonly ShellService _shell;         // 对外门面：开关面板、迷你模式、通知、拖放分发

    // 主窗口自己的视图模型（MVVM）。ItemsControl 直接绑定它的集合来显示任务图标。
    private readonly BarViewModel _vm = new();
    // 插件“内嵌内容(widget)”缓存：key=插件Id，value=插件返回的可视元素。
    // 用缓存让同一插件只创建一次，避免每次布局都重建；插件卸载时从这里面移除并释放。
    private readonly Dictionary<string, FrameworkElement> _widgets = new(StringComparer.OrdinalIgnoreCase);

    // —— 鼠标交互用的临时状态（一次“按下→移动→抬起”周期内有效）——
    private PluginDescriptor? _pressItem;   // 当前按下的那个任务图标对应的插件
    private Point _pressPoint;              // 按下时的鼠标坐标（用来判断是否超过拖拽阈值）
    private bool _dragging;                 // 是否已进入拖拽排序模式
    private bool _suppressNextClick;        // 刚结束拖拽，要吃掉紧接着的 Click，避免又触发“打开面板”

    /// <summary>
    /// 构造函数：完成所有初始化装配。
    /// 步骤：
    ///   1) 保存注入的服务，加载 XAML（InitializeComponent），把 DataContext 设为 _vm；
    ///   2) 关键：在构造阶段就主动建出原生窗口句柄（EnsureNativeWindow），不能等 SourceInitialized，
    ///      因为 WPF 的 Loaded 可能比 SourceInitialized 先触发，那时还没 HWND，AppBar 注册会静默失败；
    ///   3) 挂各种事件：加载完成、尺寸变化、关闭、插件布局变化、鼠标（按下/移动/抬起/右键）、文件拖放；
    ///   4) 首次同步插件列表（SyncPlugins）。
    /// 注意：这里只做“装配”，真正的定位/设置应用放到 OnLoaded/ApplySettings/Reposition 里，
    /// 因为那时布局已经算好，拿到的尺寸才正确。
    /// </summary>
    public BarWindow(SettingsService settings, PluginHost plugins, ShellService shell)
    {
        _settings = settings;
        _plugins = plugins;
        _shell = shell;

        InitializeComponent();

        DataContext = _vm;

        // 关键：WPF 的 Loaded 有可能早于 SourceInitialized 触发（实测如此），
        // 那时候还没有 HWND，AppBar 会注册失败、Reposition 也会因为拿不到句柄直接返回。
        // 所以在构造阶段就把句柄建出来，后续所有逻辑都不再依赖事件顺序。
        EnsureNativeWindow();

        SourceInitialized += (_, _) => EnsureNativeWindow();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => Reposition();
        Closed += (_, _) =>
        {
            ReleaseAllWidgets();
            AppBar.Dispose(); // 退出前注销 AppBar，把屏幕空间还给系统
            Tray.Dispose();   // 移除系统托盘图标
        };

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

    /// <summary>对外暴露视图模型，方便其它模块（如设置页、插件代码）读取任务栏当前状态。</summary>
    public BarViewModel ViewModel => _vm;

    /// <summary>AppBar 注册器：注册后本窗口就"像任务栏一样"占住一条屏幕边缘。</summary>
    public AppBarService AppBar { get; } = new();

    /// <summary>系统托盘图标：左键显示/隐藏任务栏、右键菜单、双击打开设置。</summary>
    public TrayIcon Tray { get; } = new();

    // HwndSource：WPF 里“把托管窗口接到 Win32 原生消息循环”的桥。
    // 通过它我们能挂钩子接收原生窗口消息（AppBar 回调、分辨率/缩放变化等）。
    private HwndSource? _source;
    private bool _nativeWindowReady;       // EnsureNativeWindow 是否已完成（保证只执行一次，幂等）
    private long _lastAppBarSet;           // 最近一次自己调用 ABM_SETPOS 的时间戳（用于 AppBar 防回环）

    /// <summary>
    /// 提前把原生窗口与 AppBar 通道准备好（幂等：被多处调用也只执行一次）。
    ///
    /// 为什么必须在构造阶段调用：
    ///   WPF 的窗口句柄(HWND)平时是“懒创建”的——要等到窗口真正开始显示、SourceInitialized 触发后才会有。
    ///   但实测 Loaded 事件可能比 SourceInitialized 还早到约 177ms，那时 HWND 还是 IntPtr.Zero。
    ///   如果 AppBar 注册、首次 Reposition 等到 SourceInitialized 才做，就会因为拿不到 HWND 而静默失败
    ///   （注册不上去、定位函数直接 return），表现就是“任务栏不贴边 / 最大化窗口盖住我们”。
    ///   所以这里用 WindowInteropHelper.EnsureHandle() 主动、提前把 HWND 建出来。
    ///
    /// 步骤：
    ///   1) 幂等判断：已经建过就直接返回；
    ///   2) EnsureHandle() 强制创建 HWND（WindowInteropHelper 是 WPF 用来访问底层 HWND 的帮手类）；
    ///   3) 套用“任务栏”样式：不进 Alt+Tab、不占系统任务栏（WS_EX_TOOLWINDOW 那一类）；
    ///   4) 让全屏检测忽略本窗口（否则自己会误判成全屏）；
    ///   5) HwndSource.FromHwnd 拿到消息桥，AddHook 挂上 OnWndProc 接收原生消息；
    ///   6) AppBar.Attach 接管 HWND，之后注册/定位都走 AppBar 服务；订阅它的位置变化与全屏回调。
    /// </summary>
    private void EnsureNativeWindow()
    {
        if (_nativeWindowReady)
        {
            return;
        }

        // WindowInteropHelper：WPF 的 Window 与 Win32 HWND 之间的“翻译官”。
        // EnsureHandle()：不等系统默认时机，现在就创建并返回 HWND（IntPtr）。
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _nativeWindowReady = true;

        // 不进 Alt+Tab、不在系统任务栏出现 —— 我们本身就是“任务栏”，不该再占系统任务栏一格
        WindowDressingService.Apply(this, WindowDressing.TaskBar);

        AppServices.Fullscreen?.IgnoreWindow(hwnd);

        // HwndSource：把 HWND 包成能收 WPF/Win32 消息的对象；AddHook 注册一个回调(HwndSourceHook)，
        // 以后这个窗口收到的每一条原生消息都会先经过 OnWndProc。
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(OnWndProc);

        // AppBar：向系统申请一条边缘空间，之后最大化窗口会自动避开我们
        AppBar.Attach(hwnd);
        AppBar.PositionChanged += (_, _) => OnAppBarPositionChanged();
        AppBar.FullscreenAppChanged += (_, started) => AppServices.Fullscreen?.ReportSystemFullscreen(started);

        // 系统托盘图标：也挂在本窗口名下（由 OnWndProc 转交鼠标消息），按设置决定显隐
        Tray.Attach(hwnd);
        ApplyTrayVisibility();
    }

    /// <summary>
    /// 让系统托盘图标的显隐跟随设置。窗口显示且设置开启就 Show，否则 Hide。
    /// 设置里开关托盘、启动、退出时都会走到这里（幂等）。
    /// </summary>
    public void ApplyTrayVisibility()
    {
        var want = _settings.Settings.EnableTrayIcon && !_shell.IsMiniMode;

        if (want && !Tray.IsVisible)
        {
            Tray.Show($"MiniBar · {_settings.Settings.Edge} 边");
        }
        else if (!want && Tray.IsVisible)
        {
            Tray.Hide();
        }
    }

    /// <summary>
    /// Shell 要求重新安排位置。这里必须防回环：我们自己调 ABM_SETPOS 之后，
    /// Shell 往往紧接着发一次 ABN_POSCHANGED（通知的就是我们自己刚做的事），
    /// 若无条件响应就会变成"设位置→被通知→再设位置"的死循环，白烧 CPU。
    /// </summary>
    private void OnAppBarPositionChanged()
    {
        if (Environment.TickCount64 - _lastAppBarSet < 1000)
        {
            AppLog.Debug("忽略自身 SETPOS 引起的 AppBar 位置通知");
            return;
        }

        // Dispatcher.BeginInvoke：把 Reposition 抛回 UI 线程的“下一帧”执行（默认 DispatcherPriority.Normal）。
        // 因为 AppBar 通知来自原生消息线程，而改 WPF 布局必须在 UI 线程；BeginInvoke 还能合并/延后，避免同一时刻连环重排。
        Dispatcher.BeginInvoke(new Action(Reposition));
    }

    /// <summary>
    /// 原生窗口消息钩子（HwndSourceHook 签名）。窗口收到的每一条 Win32 消息都会先到这里。
    /// 我们在这里接三类系统通知：
    ///   1) AppBar 回调：Shell 告诉我们要改位置 / 有全屏程序 / 状态变化（交给 AppBar.HandleMessage）；
    ///   2) WM_DISPLAYCHANGE / WM_DPICHANGED：分辨率或系统缩放变了，要重新读取设置并贴边；
    ///   3) 其它消息原样放过。
    /// 收到后通常用 Dispatcher.BeginInvoke 把处理“抛回 UI 线程的下一帧”再执行，
    /// 因为消息可能来自非 UI 线程，而改 WPF 属性必须在 UI 线程。
    /// </summary>
    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Shell 发来的 AppBar 通知（位置变化 / 有程序全屏 / 状态变化）
        if (AppBar.HandleMessage(msg, wParam, lParam))
        {
            handled = true;
            return IntPtr.Zero;
        }

        // 系统托盘图标发来的鼠标消息：由 OnTrayMessage 按左键/右键/双击分流
        if (msg == TrayIcon.CallbackMessage)
        {
            handled = true;
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            Dispatcher.BeginInvoke(new Action(() => OnTrayMessage(mouseMessage)));
            return IntPtr.Zero;
        }

        // 分辨率 / 缩放 / 显示器拓扑变化后重新贴边
        if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            // 同样抛回 UI 线程，避免在原生消息回调里直接改 WPF 属性（会跨线程异常）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplySettings();
                Reposition();
            }));
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 窗口真正加载完成（布局已算好、HWND 已确定）后调用。
    /// 此时才安全地应用设置、贴边定位、同步插件，最后再显示出来，
    /// 避免窗口在 (0,0) 闪一下再跳到目标位置。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureNativeWindow();
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
        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = DisplayService.GetScale(hwnd);

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

        // 占用屏幕空间时尽量贴住边缘（薄薄留一点边给投影），悬浮时留出完整投影空间
        ShellBorder.Margin = settings.UseAppBar
            ? new Thickness(4, 3, 4, 3)
            : new Thickness(7);

        if (settings.UseAppBar)
        {
            // 占用屏幕空间模式：厚度由设置决定，长度交给系统批给的那条边缘
            // （注意 MaxWidth 不能设成 NaN —— WPF 会抛 ArgumentException，它的默认值就是 PositiveInfinity）
            SizeToContent = SizeToContent.Manual;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;

            if (vertical)
            {
                Width = Math.Max(28, settings.AppBarThickness);
                Height = double.NaN;
            }
            else
            {
                Height = Math.Max(28, settings.AppBarThickness);
                Width = double.NaN;
            }
        }
        else
        {
            // 悬浮胶囊模式：内容自适应大小，用 MaxWidth/MaxHeight 约束换行
            SizeToContent = SizeToContent.WidthAndHeight;

            if (!double.IsNaN(Width))
            {
                Width = double.NaN;
            }

            if (!double.IsNaN(Height))
            {
                Height = double.NaN;
            }

            MaxWidth = Math.Max(180, (display.WorkArea.Width / scale) - (settings.Margin * 2));
            MaxHeight = Math.Max(120, (display.WorkArea.Height / scale) - (settings.Margin * 2));
        }

        SyncAppBarRegistration();

        if (!_shell.IsMiniMode)
        {
            Opacity = settings.BarOpacity;
        }

        RefreshMiniIndicator();
    }

    /// <summary>
    /// 让 AppBar 的注册状态跟随设置与可见性：
    /// 只有"要用 AppBar + 正显示着 + 不在迷你模式"时才占住屏幕空间，
    /// 其余情况一律归还（否则用户桌面上会永久留一条空位）。
    /// </summary>
    private void SyncAppBarRegistration()
    {
        var shouldRegister = _settings.Settings.UseAppBar && IsVisible && !_shell.IsMiniMode;

        if (shouldRegister && !AppBar.IsRegistered)
        {
            AppBar.Register();
        }
        else if (!shouldRegister && AppBar.IsRegistered)
        {
            AppBar.Unregister();
        }
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

        if (AppBar.IsRegistered)
        {
            // AppBar 模式：由 Shell 决定我们占哪一条（它会避开系统任务栏与其它 AppBar）
            var granted = AppBar.SetPosition(
                settings.Edge,
                display.Bounds,
                settings.AppBarThickness * scale,
                settings.Margin * scale);

            _lastAppBarSet = Environment.TickCount64;
            if (granted is { Width: > 0, Height: > 0 })
            {
                // 长度交给系统批的矩形，厚度用设置值；只在真的不同的时候改，
                // 否则会触发 SizeChanged → Reposition 的死循环。
                // 注意 NaN 的坑：ApplySettings 在 AppBar 模式下会把长度方向设成 NaN（= 由内容决定），
                // 而 "NaN - x > 0.5" 恒为 false，如果不显式处理 NaN，宽度就永远不会被赋值，
                // 任务栏就会停在内容宽度（几百像素）而不是铺满整条边。
                var wantsWidth = granted.Value.Width / scale;
                var wantsHeight = granted.Value.Height / scale;

                var widthDiff = double.IsNaN(Width) ? double.MaxValue : Math.Abs(Width - wantsWidth);
                var heightDiff = double.IsNaN(Height) ? double.MaxValue : Math.Abs(Height - wantsHeight);

                if (_vm.IsVertical)
                {
                    if (widthDiff > 0.5)
                    {
                        Width = wantsWidth;
                    }

                    if (heightDiff > 0.5)
                    {
                        Height = wantsHeight;
                    }
                }
                else
                {
                    if (heightDiff > 0.5)
                    {
                        Height = wantsHeight;
                    }

                    if (widthDiff > 0.5)
                    {
                        Width = wantsWidth;
                    }
                }

                DisplayService.MoveWindow(hwnd, granted.Value.Left, granted.Value.Top, topmost: true);
                return;
            }
        }

        // 悬浮模式：按内容尺寸贴到边缘居中
        var size = new Size(Math.Max(40, ActualWidth) * scale, Math.Max(30, ActualHeight) * scale);
        var rect = DisplayService.ComputeEdgeRect(display, settings.Edge, size, settings.Margin * scale);

        DisplayService.MoveWindow(hwnd, rect.Left, rect.Top, topmost: true);
    }

    /// <summary>窗口当前的屏幕像素矩形（浮层定位用）。Rect 是“左上角+宽高”的矩形结构，单位=物理像素。</summary>
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

            // ItemContainerGenerator：ItemsControl 把“数据项”变成“可视容器(ContentPresenter)”的工厂。
            // ContainerFromIndex 拿第 i 个图标对应的那个可视容器，才能进一步算它的屏幕位置。
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                return null;
            }

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var scale = DisplayService.GetScale(hwnd);
                // PointToScreen：把控件本地坐标(0,0 左上角)换算成屏幕坐标。
                var origin = container.PointToScreen(new Point(0, 0));
                // 容器尺寸是 WPF 单位，乘以缩放比得到物理像素，组合成屏幕矩形。
                return new Rect(origin.X, origin.Y, container.ActualWidth * scale, container.ActualHeight * scale);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 取当前窗口所在显示器的 DPI 缩放比（例如 150% 缩放返回 1.5）。
    /// 很多 Win32 坐标/尺寸是“物理像素”，而 WPF 内部用“与设备无关的单位(96DPI 基准)”，
    /// 两者换算时要乘/除这个比值，否则在高缩放屏上定位会偏移。
    /// </summary>
    public double GetDpiScale() => DisplayService.GetScale(new WindowInteropHelper(this).Handle);

    /// <summary>
    /// 显隐任务栏。显示时重新注册 AppBar 占用屏幕空间、重新贴边；隐藏时必须 ABM_REMOVE 把空间还给系统，
    /// 否则桌面上会永久留一条我们占着的空位（别的窗口最大化也绕开它）。
    /// </summary>
    public void SetBarVisible(bool visible)
    {
        if (visible)
        {
            Show();
            Opacity = _settings.Settings.BarOpacity;

            // 重新占用屏幕空间（隐藏期间已归还）
            SyncAppBarRegistration();
            Reposition();
            AppServices.Topmost?.Tick();
        }
        else
        {
            // 隐藏前必须把占用的屏幕空间还给系统，否则桌面上会永久留一条空位
            AppBar.Unregister();
            Hide();
        }
    }

    /// <summary>
    /// 刷新“是否处于迷你模式”的小指示（把手上的提示文字会变化），让用户一眼看出当前状态。
    /// </summary>
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

    /// <summary>
    /// 某个任务图标容器(ContentPresenter)布局完成（Loaded）后触发。
    /// 此时 DataTemplate 已应用、WidgetHost 已经存在，可以安全地把插件内嵌内容挂上去。
    /// 只挂一次：进来就取消订阅自身，避免重复挂载。
    /// </summary>
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

    /// <summary>
    /// 在视觉树里按名字查找某个后代元素（泛型 T，必须是 FrameworkElement）。
    /// 用来在任务图标模板里找到名为 "WidgetHost" 的 ContentControl（插件内嵌内容的落点）。
    /// 用 VisualTreeHelper 递归遍历：先从子节点数往下走，命中名字与类型就返回，否则继续往更深一层找。
    /// 不这么做就只能靠 x:Name 字段，但 ItemsControl 的模板项是动态生成的，拿不到强名字段，所以必须靠视觉树查找。
    /// </summary>
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

    /// <summary>
    /// 拿到（或首次创建并缓存）某插件的内嵌内容元素。
    /// 同一插件只 CreateBarWidget 一次，之后都从 _widgets 缓存取，避免反复重建造成界面抖动。
    /// 插件返回的 WidgetWidth/WidgetHeight：&gt;0 才显式设宽高；WidgetWidth &lt;= 0 表示“自动宽度”（交给布局决定），
    /// 这对应需求里的“WidgetWidth <= 0 表示自动宽度”。
    /// 插件代码可能抛异常：这里 try/catch 住，失败只记日志并返回 null，绝不连累宿主崩。
    /// </summary>
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

    /// <summary>
    /// 释放某个插件的内嵌内容：从缓存里移除，并调用插件自己的 ReleaseBarWidget 回收资源。
    /// 插件卸载 / 不再固定时调用，避免缓存越积越多、内存泄漏。try/catch 防止插件释放逻辑出错拖垮宿主。
    /// </summary>
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

    /// <summary>窗口关闭前统一释放所有插件内嵌内容（逐个调用 ReleaseWidget）。</summary>
    private void ReleaseAllWidgets()
    {
        foreach (var id in _widgets.Keys.ToArray())
        {
            ReleaseWidget(id);
        }
    }

    // ================================================================ 鼠标交互

    /// <summary>
    /// 从被点击的元素(OriginalSource)反查出它属于哪个插件。
    /// 做法：ItemsControl.ContainerFromElement 能由“视觉树里的某个子元素”找到它所在的那个容器(ContentPresenter)，
    /// 而这个容器的 DataContext 正是绑定进去的 PluginDescriptor（插件描述）。找不到就返回 null。
    /// </summary>
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
        // 从被点到的元素沿视觉树往上爬，直到遇到任务栏整体(ItemsHost)为止。
        while (node is not null && !ReferenceEquals(node, ItemsHost))
        {
            // Tag 是 WPF 元素上一个“随便塞任意对象”的口袋属性。插件在自己内嵌内容的最外层
            // 打上 Tag="Interactive"，相当于声明“这是我自己会处理的控件”。
            // 看到这个标记就认为用户点的是插件控件，宿主不该再套“点一下切换面板”的默认行为。
            // （任务栏模板里插件内嵌内容那一层故意不加这个 Tag，否则点读数会打不开面板。）
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

    /// <summary>
    /// 鼠标按下预处理（隧道阶段）：只处理中键。中键=关闭该插件的面板（模拟系统任务栏习惯）。
    /// 若点在插件自绘内容(IsInteractiveHit)上，则交给插件自己处理，宿主不插手。设 e.Handled=true 阻止继续冒泡。
    /// </summary>
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

    /// <summary>
    /// 左键在任务栏上按下：记录“按下了哪个插件”和按下坐标，并捕获鼠标。
    /// 捕获鼠标(Mouse.Capture)后，即使指针移出窗口范围，后续 MouseMove/Up 仍会发到本窗口，
    /// 这样拖拽到窗口外也不会“丢手”。若点在插件自绘内容(IsInteractiveHit)上则直接放行、不进入拖拽逻辑。
    /// </summary>
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

    /// <summary>
    /// 鼠标移动：判断是否进入“拖拽排序”。
    /// 关键：先比较按下点与当前点的距离，超过阈值(6px)才算拖拽。不判断阈值的话，手轻微一抖就会被当成拖拽，
    /// 用户只是想点一下却把图标挪了。进入拖拽后实时调用 LiveReorder 让图标跟随鼠标。
    /// </summary>
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

    /// <summary>
    /// 左键抬起：决定这次“按下→抬起”到底是点击还是拖拽结束。
    ///   - 先松开鼠标捕获；
    ///   - 若刚拖拽过：吃掉下一次 Click(_suppressNextClick)、把新顺序落盘(ApplyPinnedOrder)；
    ///   - 否则若是插件自绘内容/需要抑制：直接忽略；
    ///   - 双击：交给插件 DoubleClick 语义；
    ///   - 普通单击：先让插件 OnClick 处理，插件没动过界面(ManagesOwnActivation/ClickHandled)才套用
    ///     “点一下切换面板”。顺序很关键——若宿主先 toggle 一次、插件再 toggle 一次，面板会开了又立刻关（闪一下）。
    /// </summary>
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

        // 顺序很关键：**先**让插件处理（它可以在 OnClick 里自己开关面板、进迷你模式），
        // 插件没动过界面时，宿主才套用"标准任务栏语义：点一下切换该插件的界面"。
        // 如果反过来，宿主 toggle 一次、插件再 toggle 一次，面板就会开了立刻又关 —— 表现为"闪一下"。
        var handledByPlugin = InvokePluginClick(pressed, BarItemActivationKind.Primary, MouseButton.Left);

        if (!handledByPlugin && !ManagesOwnActivation(pressed) && pressed.HasPanel)
        {
            _shell.TogglePanel(pressed.Id);
        }
    }

    /// <summary>
    /// 插件是否“自己管理激活态”。返回 true 表示插件在 OnClick 里自行决定开关面板，宿主就不要再默认 toggle。
    /// 读取时 try/catch：插件代码若抛异常，保守当作“不自己管理”，仍套用宿主默认行为，避免宿主崩。
    /// </summary>
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

    /// <summary>把点击交给插件；返回 true 表示插件已经自己处理了界面（宿主不要再套默认行为）。</summary>
    private bool InvokePluginClick(PluginDescriptor descriptor, BarItemActivationKind kind, MouseButton button)
    {
        if (descriptor.TaskButton is not { } taskButton || descriptor.Facade is null)
        {
            return false;
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
            return context.ClickHandled;
        }
        catch (Exception ex)
        {
            AppLog.Error($"插件点击处理异常：{descriptor.Id}", ex);
            _shell.Notify($"「{descriptor.DisplayName}」出错：{ex.Message}", NotificationKind.Error);
            return false;
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

    /// <summary>
    /// 在某个任务图标上右键抬起：弹出“该插件”的上下文菜单（固定/打开界面/重载/卸载等）。
    /// 若点在插件自绘内容(IsInteractiveHit)上则不当作图标右键，放行给窗口级主菜单处理。
    /// 设 e.Handled=true 阻止事件继续冒泡到窗口级右键逻辑。
    /// </summary>
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

    /// <summary>弹出主菜单（任务栏右键 / 把手 / 托盘右键都走这里）。菜单会出现在鼠标位置。</summary>
    public void ShowMainContextMenu()
    {
        var menu = MainMenuBuilder.Build(_plugins, _settings, _shell, this);

        // 定位（最后一次实验的结论，采用 Point + 相对任务栏边框的鼠标坐标）：
        //   · Bottom/窗口做目标：钉在固定位置，不跟鼠标；
        //   · MousePoint / RelativePoint / AbsolutePoint：PerMonitorV2 缩放下全部偏（右缘对齐鼠标）；
        //   · Point + PlacementTarget=ShellBorder + Mouse.GetPosition(ShellBorder)：交给 WPF 换算 DPI。
        // 提示：Windows 的「菜单对齐方式」（MenuDropAlignment，常见于平板/左手模式）若为 true，
        // 所有右键菜单都会弹在鼠标左侧（右缘贴鼠标）—— 这是系统级设置，不是本程序的定位错误。

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
    }

    /// <summary>
    /// 托盘图标发来的鼠标消息分流：
    ///   左键单击 → 显示/隐藏任务栏；双击 → 打开设置；右键/菜单键 → 弹主菜单。
    /// 注意：回调可能来自非 UI 线程，所以这里抛回 UI 线程再执行。
    /// </summary>
    private void OnTrayMessage(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case TrayIcon.WM_LBUTTONUP:
                SetBarVisible(!IsVisible);
                break;

            case TrayIcon.WM_LBUTTONDBLCLK:
                _shell.ShowSettings();
                break;

            case TrayIcon.WM_RBUTTONUP:
            case TrayIcon.WM_CONTEXTMENU:
                ShowMainContextMenu();
                break;
        }
    }

    /// <summary>
    /// 拼装并弹出“单个插件”的上下文菜单：打开/关闭界面、用作迷你模式内容、固定/取消固定、
    /// 移到最前/最后、插件追加项(PluginMenuTarget.BarItem)、重载、禁用、打开目录、卸载删除等。
    /// 菜单项是否可用会结合插件能力（HasPanel/HasCompact/HasTaskButton）判断。
    /// </summary>
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

    /// <summary>
    /// 弹出“溢出区(未固定插件)”菜单：把每个未固定插件做成一个子菜单，可执行打开界面/固定/禁用。
    /// 没有任何未固定插件时显示一条提示项，最后附“插件管理”。
    /// </summary>
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

    /// <summary>
    /// 文件被拖到窗口上方（悬停）时触发。AllowDrop=true 才能让窗口接收拖放。
    /// 这里只判断拖的是不是文件(DataFormats.FileDrop)，是则把光标效果设为 Copy（显示“可放下”图标）并高亮边框；
    /// 不是则设为 None，表示不接受。必须设 e.Handled=true 表示我们处理了这个事件。
    /// </summary>
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

    /// <summary>拖放离开窗口：把高亮边框恢复正常（去掉拖入时的强调色）。</summary>
    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        ShellBorder.BorderBrush = (Brush)FindResource("BarBorderBrush");
    }

    /// <summary>
    /// 文件真正落在窗口上：取出拖进来的路径数组，判断落点是“图标”还是“空白”，然后交给
    /// ShellService.HandleDrop 分发——先问被命中的目标插件，再按顺序问其它拖放插件，最后内置兜底
    /// （例如单个 DLL 直接热加载）。这样插件能自定义拖放行为，宿主又不至于对未知拖放毫无反应。
    /// </summary>
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

    // 把手(Grip)点击 → 主菜单；溢出按钮 → 溢出菜单；加号 → 插件管理器。
    private void OnGripClick(object sender, RoutedEventArgs e) => ShowMainContextMenu();

    private void OnOverflowClick(object sender, RoutedEventArgs e) => ShowOverflowMenu();

    private void OnAddClick(object sender, RoutedEventArgs e) => _shell.ShowPluginManager();

    // ================================================================ 工具

    /// <summary>
    /// 判断某插件文件是否位于我们的插件目录（用户目录或内置目录）内。
    /// 只有“自家地盘”里的插件才允许“卸载并删除文件”，避免误删用户机器上其它地方的 DLL。
    /// 路径比较前先 Path.GetFullPath 规范化（处理相对路径/大小写），全程 try/catch 防异常。
    /// </summary>
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

    /// <summary>
    /// 用资源管理器打开某个路径：目录就直接打开；文件就 /select 选中它；路径不存在就先建目录再打开。
    /// 用 explorer.exe + UseShellExecute 调起系统外壳。全程 try/catch，打开失败只记日志不崩。
    /// </summary>
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
