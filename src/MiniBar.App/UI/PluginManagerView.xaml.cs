using System.Collections.Specialized;
using System.Diagnostics;
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
/// 插件管理界面（用户控件版）。
///
/// <para>
/// 原先是独立的 <c>PluginManagerWindow</c> 窗口，后来合并进设置窗口的「插件管理」页 ——
/// 好处是"看插件、管插件、改插件设置"在同一个窗口里完成，不用在多个窗口之间来回切。
/// </para>
///
/// <para><b>它负责的事</b>：</para>
/// <list type="bullet">
///   <item>列出所有插件（含被判定为"重复文件"的那些），显示状态、能力、文件与错误；</item>
///   <item>单个插件：打开界面 / 固定 / 启用禁用 / 重新加载 / 打开设置 / 卸载并删除；</item>
///   <item>批量：全部重新加载、全部卸载、重新扫描目录、打开插件目录；</item>
///   <item>同时是<b>拖放区</b>：把 DLL 拖到这块区域上就会热加载。</item>
/// </list>
///
/// <para>
/// 数据源直接绑定 <see cref="PluginHost.Plugins"/>（一个 ObservableCollection），
/// 所以插件增删时列表会自己刷新，不需要手动重建。
/// </para>
/// </summary>
public partial class PluginManagerView : UserControl
{
    private readonly PluginHost _plugins;
    private readonly ShellService _shell;
    private readonly SettingsService _settings;
    private bool _loadingUi;

    public PluginManagerView(PluginHost plugins, ShellService shell, SettingsService settings)
    {
        _plugins = plugins;
        _shell = shell;
        _settings = settings;

        InitializeComponent();

        PluginList.ItemsSource = _plugins.Plugins;

        // 初始化界面控件时先立个"正在装填"的旗子：否则给 CheckBox 赋初值会触发 Checked 事件，
        // 把一个刚读出来的值又写回去（虽然结果一样，但会白白多写一次配置）
        _loadingUi = true;
        AutoLoadCheck.IsChecked = settings.Settings.AutoLoadPluginOnDllDrop;
        _loadingUi = false;

        UserDirText.Text = $"用户插件目录（可写，放进来即被发现）：{AppPaths.UserPluginDirectory}";

        ((INotifyCollectionChanged)_plugins.Plugins).CollectionChanged += OnPluginsCollectionChanged;
        _plugins.LayoutChanged += OnLayoutChanged;

        // 控件从设置窗口的导航切走时会被"卸载"，此时必须退订，否则插件增删会去碰已经不在界面上的元素
        Unloaded += OnUnloaded;

        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        // UserControl 没有窗口级的右键处理，自己订阅
        MouseRightButtonUp += OnMouseRightButtonUp;

        UpdateEmptyState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ((INotifyCollectionChanged)_plugins.Plugins).CollectionChanged -= OnPluginsCollectionChanged;
        _plugins.LayoutChanged -= OnLayoutChanged;
    }

    private void OnPluginsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateEmptyState));
    }

    private void UpdateEmptyState()
    {
        EmptyHint.Visibility = _plugins.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- 按钮

    private static PluginDescriptor? DescriptorFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as PluginDescriptor;

    private void OnOpenPanelClick(object sender, RoutedEventArgs e)
    {
        if (DescriptorFrom(sender) is { } descriptor)
        {
            _shell.TogglePanel(descriptor.Id);
        }
    }

    private void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        if (DescriptorFrom(sender) is { } descriptor)
        {
            _plugins.SetPinned(descriptor, !descriptor.IsPinned);
        }
    }

    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        if (DescriptorFrom(sender) is { } descriptor)
        {
            _plugins.SetEnabled(descriptor, !descriptor.IsEnabled);
            _shell.Notify(descriptor.IsEnabled
                ? $"已启用：{descriptor.Name}"
                : $"已禁用：{descriptor.Name}（文件与配置保留，不再占用内存）",
                NotificationKind.Success);
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (DescriptorFrom(sender) is { } descriptor)
        {
            _plugins.Reload(descriptor);
            _shell.Notify(descriptor.HasError ? $"重新加载失败：{descriptor.Error}" : $"已重新加载：{descriptor.Name}",
                descriptor.HasError ? NotificationKind.Error : NotificationKind.Success);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (DescriptorFrom(sender) is not { } descriptor)
        {
            return;
        }

        if (_settings.Settings.ConfirmBeforeUninstall)
        {
            var answer = MessageBox.Show(
                $"确定要卸载并删除插件文件吗？\n\n{descriptor.DisplayName}\n{descriptor.FilePath}\n\n" +
                "会先卸载插件（释放 AssemblyLoadContext），再删除插件 DLL（不可恢复）。",
                "卸载并删除插件",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        var ok = _plugins.Unload(descriptor, deleteFile: true);
        _shell.Notify(ok ? $"已卸载并删除：{descriptor.Name}" : $"已卸载，但文件删除失败：{descriptor.Name}",
            ok ? NotificationKind.Success : NotificationKind.Warning);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DescriptorFrom(sender) is { } descriptor)
        {
            _shell.ShowSettings(descriptor.Id);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _shell.Notify("正在重新扫描插件目录…", NotificationKind.Info, TimeSpan.FromSeconds(1.2));
        await _plugins.RescanAsync();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenInExplorer(AppPaths.UserPluginDirectory);

    /// <summary>
    /// "从文件加载…"：用系统的文件选择框挑一个 DLL 并热加载。
    /// 与"把文件拖进来"效果完全一样（内部都走 ShellService.LoadPlugin，最终到 PluginHost.LoadPluginFile）。
    /// </summary>
    private void OnLoadFromFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件 DLL",
            Filter = "MiniBar 插件 (*.dll)|*.dll|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = Directory.Exists(AppPaths.UserPluginDirectory) ? AppPaths.UserPluginDirectory : AppPaths.BaseDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            _shell.LoadPlugin(file);
        }
    }

    private void OnAutoLoadChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
        {
            return;
        }

        _settings.Settings.AutoLoadPluginOnDllDrop = AutoLoadCheck.IsChecked == true;
        _settings.NotifyChanged();
    }

    private void OnReloadAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var descriptor in _plugins.Plugins.Where(p => p.IsEnabled && !p.IsDuplicate).ToArray())
        {
            _plugins.Reload(descriptor);
        }

        _shell.Notify("已重新加载全部启用的插件", NotificationKind.Success);
    }

    private void OnDisableAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var descriptor in _plugins.Plugins.Where(p => p.IsEnabled).ToArray())
        {
            _plugins.SetEnabled(descriptor, false);
        }

        _shell.Notify("已卸载全部插件（文件与配置保留）", NotificationKind.Info);
    }

    // ---------------------------------------------------------------- 拖放

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        e.Handled = true;
        _shell.HandleDrop(paths, PluginDropTarget.PluginManager, null, DisplayService.GetCursorPositionPixels());
    }

    // ---------------------------------------------------------------- 右键

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var descriptor = FindDescriptorUnder(e.OriginalSource);
        var menu = new ContextMenu { Style = (Style)FindResource("MiniBarContextMenuStyle") };

        if (descriptor is not null)
        {
            menu.Items.Add(MenuBuilder.Item($"「{descriptor.DisplayName}」", null, isEnabled: false));
            menu.Items.Add(MenuBuilder.Sep());
        }

        var entries = _shell.CollectMenuEntries(PluginMenuTarget.PluginManager, descriptor?.Id);
        if (entries.Count > 0)
        {
            foreach (var item in MenuBuilder.Convert(entries, _shell.InvokeMenuEntry))
            {
                menu.Items.Add(item);
            }

            menu.Items.Add(MenuBuilder.Sep());
        }

        menu.Items.Add(MenuBuilder.Item("重新扫描插件目录", async () => await _plugins.RescanAsync(), "glyph:E72C"));
        menu.Items.Add(MenuBuilder.Item("打开用户插件目录", () => OpenInExplorer(AppPaths.UserPluginDirectory), "glyph:E838"));
        menu.Items.Add(MenuBuilder.Item("打开配置目录", () => OpenInExplorer(AppPaths.ConfigDirectory), "glyph:E8A5"));

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private PluginDescriptor? FindDescriptorUnder(object? source)
    {
        if (source is not DependencyObject node)
        {
            return null;
        }

        while (node is not null && !ReferenceEquals(node, PluginList))
        {
            if (node is FrameworkElement { DataContext: PluginDescriptor descriptor })
            {
                return descriptor;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开资源管理器失败：{path}", ex);
        }
    }
}
