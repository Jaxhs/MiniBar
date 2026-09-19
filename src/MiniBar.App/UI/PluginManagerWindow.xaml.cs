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
/// 插件管理器：插件的一站式运维界面 —— 加载、启用、禁用、重新加载、卸载、删除、固定，
/// 同时充当拖放区（把插件 DLL 拖进来即刻热加载）。
/// </summary>
public partial class PluginManagerWindow : Window
{
    private readonly PluginHost _plugins;
    private readonly ShellService _shell;
    private readonly SettingsService _settings;
    private bool _loadingUi;

    public PluginManagerWindow(PluginHost plugins, ShellService shell, SettingsService settings)
    {
        _plugins = plugins;
        _shell = shell;
        _settings = settings;

        InitializeComponent();

        PluginList.ItemsSource = _plugins.Plugins;

        _loadingUi = true;
        AutoLoadCheck.IsChecked = settings.Settings.AutoLoadPluginOnDllDrop;
        _loadingUi = false;

        PathHint.Text = AppPaths.UserPluginDirectory;
        UserDirText.Text = $"用户插件目录：{AppPaths.UserPluginDirectory}";

        ((INotifyCollectionChanged)_plugins.Plugins).CollectionChanged += OnPluginsCollectionChanged;
        _plugins.LayoutChanged += OnLayoutChanged;
        Closed += OnClosed;

        SourceInitialized += (_, _) =>
        {
            WindowDressingService.SetDarkTitleBar(this, AppServices.Theme?.IsDark ?? false);
        };

        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        UpdateEmptyState();
    }

    private void OnClosed(object? sender, EventArgs e)
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

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _shell.Notify("正在重新扫描插件目录…", NotificationKind.Info, TimeSpan.FromSeconds(1.2));
        await _plugins.RescanAsync();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenInExplorer(AppPaths.UserPluginDirectory);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

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
