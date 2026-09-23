using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 设置窗口。左侧是"宿主设置 + 每个插件"的导航，右侧按 <see cref="PluginSettingsSection"/>
/// 渲染卡片 —— 宿主自己的设置也走同一套模型，所以插件设置和宿主设置长得完全一样。
/// </summary>
public partial class SettingsWindow : Window
{
    public sealed class NavItem
    {
        public required string Title { get; init; }

        public string Subtitle { get; init; } = string.Empty;

        public PluginIcon Icon { get; init; } = PluginIcon.Default;

        public PluginDescriptor? Descriptor { get; init; }

        /// <summary>已启用且已加载 —— 导航右侧显示一个小圆点。</summary>
        public bool IsActive => Descriptor is { IsEnabled: true, IsLoaded: true };
    }

    private readonly PluginHost _plugins;
    private readonly SettingsService _settings;
    private readonly ShellService _shell;
    private readonly HostSettingsProvider _hostSettings;
    private readonly List<NavItem> _nav = new();

    private bool _rendering;
    private bool _rebuildQueued;

    public SettingsWindow(PluginHost plugins, SettingsService settings, ShellService shell)
    {
        _plugins = plugins;
        _settings = settings;
        _shell = shell;
        _hostSettings = new HostSettingsProvider(settings);

        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowDressingService.SetDarkTitleBar(this, AppServices.Theme?.IsDark ?? false);
        };

        // 插件被禁用/删除时导航也要跟着变
        _plugins.LayoutChanged += OnPluginsChanged;
        Closed += (_, _) => _plugins.LayoutChanged -= OnPluginsChanged;

        BuildNav();
    }

    private void OnPluginsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(BuildNav));

    /// <summary>从插件管理器跳过来时直接定位到某个插件。</summary>
    public void SelectPlugin(string pluginId)
    {
        var target = _nav.FirstOrDefault(n =>
            n.Descriptor is not null && string.Equals(n.Descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (target is not null)
        {
            NavList.SelectedItem = target;
        }
    }

    // ---------------------------------------------------------------- 导航

    private void BuildNav()
    {
        var selectedId = (NavList.SelectedItem as NavItem)?.Descriptor?.Id;
        var wasHost = NavList.SelectedItem is NavItem { Descriptor: null };

        _nav.Clear();
        _nav.Add(new NavItem
        {
            Title = "宿主设置",
            Subtitle = "任务栏本身的行为",
            Icon = "glyph:E713",
        });

        foreach (var descriptor in _plugins.Plugins.Where(p => !p.IsDuplicate).OrderBy(p => p.Order))
        {
            _nav.Add(new NavItem
            {
                Title = descriptor.DisplayName,
                Subtitle = descriptor.HasSettings ? descriptor.Id : $"{descriptor.Id}（无设置项）",
                Icon = descriptor.Icon,
                Descriptor = descriptor,
            });
        }

        NavList.ItemsSource = null;
        NavList.ItemsSource = _nav;

        if (selectedId is not null)
        {
            SelectPlugin(selectedId);
        }
        else if (wasHost || NavList.SelectedItem is null)
        {
            NavList.SelectedIndex = 0;
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    // ---------------------------------------------------------------- 渲染

    private void Render()
    {
        if (_rendering)
        {
            return;
        }

        _rendering = true;
        try
        {
            ContentHost.Children.Clear();

            if (NavList.SelectedItem is not NavItem item)
            {
                return;
            }

            if (item.Descriptor is null)
            {
                foreach (var section in _hostSettings.Build())
                {
                    ContentHost.Children.Add(BuildSection(section));
                }

                return;
            }

            RenderPlugin(item.Descriptor);
        }
        catch (Exception ex)
        {
            AppLog.Error("渲染设置界面失败", ex);
            ContentHost.Children.Add(new TextBlock
            {
                Text = $"设置界面渲染失败：{ex.Message}",
                Foreground = Brush("DangerBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>改完设置后重建当前页（避免读到旧值），排队到当前事件之后执行。</summary>
    private void RequestRebuild()
    {
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _rebuildQueued = false;
            Render();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RenderPlugin(PluginDescriptor descriptor)
    {
        ContentHost.Children.Add(BuildPluginHeader(descriptor));

        if (!descriptor.IsLoaded)
        {
            ContentHost.Children.Add(BuildNotice(
                descriptor.IsEnabled
                    ? "插件当前未加载，设置项不可用。点上面的「启用 / 重新加载」后再试。"
                    : "插件已禁用。启用后可在此查看它的设置。"));
            return;
        }

        if (descriptor.Settings is not { } settingsPlugin)
        {
            ContentHost.Children.Add(BuildNotice(
                "该插件没有实现 ISettingsPlugin，因此没有可显示的设置项。\n" +
                "插件仍然可以在自己的面板里提供设置，或者把配置写在插件数据目录里。"));
            return;
        }

        try
        {
            var sections = settingsPlugin.GetSettingsSections().ToArray();
            if (sections.Length == 0)
            {
                ContentHost.Children.Add(BuildNotice("该插件声明了设置能力，但当前没有提供任何设置项。"));
                return;
            }

            foreach (var section in sections)
            {
                ContentHost.Children.Add(BuildSection(section));
            }

            AppLog.Debug($"设置界面已渲染插件 {descriptor.Id} 的 {sections.Length} 个分组");
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取插件设置失败：{descriptor.Id}", ex);
            ContentHost.Children.Add(BuildNotice($"读取插件设置失败：{ex.Message}"));
        }
    }

    private FrameworkElement BuildPluginHeader(PluginDescriptor descriptor)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new ContentControl
        {
            Content = descriptor.Icon,
            ContentTemplate = (DataTemplate)FindResource("PluginIconTemplate"),
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        info.Children.Add(new TextBlock
        {
            Text = $"{descriptor.DisplayName}  {descriptor.Version}",
            FontWeight = FontWeights.SemiBold,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{descriptor.Id} · {descriptor.StatusText} · {descriptor.CapabilityText}",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("MutedForegroundBrush"),
        });
        info.Children.Add(new TextBlock
        {
            Text = descriptor.FilePath,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("MutedForegroundBrush"),
            ToolTip = descriptor.FilePath,
        });

        if (descriptor.HasError)
        {
            info.Children.Add(new TextBlock
            {
                Text = descriptor.Error,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("DangerBrush"),
            });
        }

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

        buttons.Children.Add(MakeButton("打开数据目录", () =>
            OpenPath(AppPaths.GetPluginDataDirectory(descriptor.Id)), "插件私有配置与数据"));

        buttons.Children.Add(MakeButton(descriptor.IsEnabled ? "禁用插件" : "启用插件", () =>
        {
            _plugins.SetEnabled(descriptor, !descriptor.IsEnabled);
            RequestRebuild();
        }, "禁用后不加载、不占内存，文件与配置保留"));

        buttons.Children.Add(MakeButton("重新加载", () =>
        {
            _plugins.Reload(descriptor);
            RequestRebuild();
        }, "重新加载插件并重建界面"));

        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        return Card(grid);
    }

    private FrameworkElement BuildSection(PluginSettingsSection section)
    {
        var host = new StackPanel();

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
        };

        if (section.Icon is not null)
        {
            header.Children.Add(new ContentControl
            {
                Content = section.Icon,
                ContentTemplate = (DataTemplate)FindResource("PluginIconTemplate"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        header.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        host.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(section.Description))
        {
            host.Children.Add(new TextBlock
            {
                Text = section.Description,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brush("MutedForegroundBrush"),
            });
        }

        var items = section.Items?.ToArray() ?? Array.Empty<PluginSettingItem>();

        if (items.Length == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "（没有设置项）",
                FontSize = 12,
                Foreground = Brush("MutedForegroundBrush"),
            });
        }

        foreach (var item in items)
        {
            host.Children.Add(BuildItem(item));
        }

        return Card(host);
    }

    private FrameworkElement BuildItem(PluginSettingItem item)
    {
        if (item.Kind == PluginSettingKind.Info)
        {
            var block = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            block.Children.Add(new TextBlock { Text = item.Label, TextWrapping = TextWrapping.Wrap });

            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                block.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("MutedForegroundBrush"),
                });
            }

            return block;
        }

        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };

        label.Children.Add(new TextBlock { Text = item.Label, TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            label.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("MutedForegroundBrush"),
            });
        }

        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var control = CreateControl(item);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
        }

        return row;
    }

    private FrameworkElement? CreateControl(PluginSettingItem item)
    {
        switch (item.Kind)
        {
            case PluginSettingKind.Bool:
            {
                var box = new CheckBox
                {
                    IsChecked = item.GetValue?.Invoke() is bool b && b,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                box.Checked += (_, _) => Commit(item, true);
                box.Unchecked += (_, _) => Commit(item, false);
                return box;
            }

            case PluginSettingKind.Choice:
            {
                var combo = new ComboBox
                {
                    Width = 230,
                    ItemsSource = item.Choices?.ToList() ?? new List<string>(),
                };

                var current = item.GetValue?.Invoke() as string;
                combo.SelectedItem = current;

                if (combo.SelectedItem is null && combo.Items.Count > 0)
                {
                    combo.SelectedIndex = 0;
                }

                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string chosen && !string.Equals(chosen, current, StringComparison.Ordinal))
                    {
                        Commit(item, chosen);
                    }
                };

                return combo;
            }

            case PluginSettingKind.Number:
            {
                var value = ToDouble(item.GetValue?.Invoke());
                var minimum = double.IsNaN(item.Minimum) ? 0 : item.Minimum;
                var maximum = double.IsNaN(item.Maximum) || item.Maximum <= minimum ? minimum + 100 : item.Maximum;
                var step = item.Step <= 0 ? 1 : item.Step;

                var slider = new Slider
                {
                    Minimum = minimum,
                    Maximum = maximum,
                    SmallChange = step,
                    LargeChange = step * 5,
                    TickFrequency = step,
                    IsSnapToTickEnabled = step > 0,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                // 先设初值、再挂事件：否则渲染设置页时这个赋值会触发 ValueChanged，
                // 把配置"顺手"写一遍（滑块类设置项特别容易踩这个坑）
                slider.Value = Math.Clamp(value, minimum, maximum);
                slider.Width = 170;

                var readout = new TextBlock
                {
                    Width = 62,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("MutedForegroundBrush"),
                    Text = FormatNumber(slider.Value, item.Suffix),
                };

                slider.ValueChanged += (_, e) =>
                {
                    readout.Text = FormatNumber(e.NewValue, item.Suffix);
                    Commit(item, e.NewValue, rebuild: false);
                };

                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(slider);
                panel.Children.Add(readout);
                return panel;
            }

            case PluginSettingKind.Folder:
            case PluginSettingKind.File:
            {
                var box = new TextBox
                {
                    Width = 250,
                    Text = item.GetValue?.Invoke() as string ?? string.Empty,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                void CommitText() => Commit(item, box.Text);

                box.LostFocus += (_, _) => CommitText();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        CommitText();
                        RequestRebuild();
                    }
                };

                var browse = MakeButton("浏览…", () =>
                {
                    var picked = item.Kind == PluginSettingKind.Folder ? PickFolder(box.Text) : PickFile(box.Text);
                    if (picked is not null)
                    {
                        box.Text = picked;
                        Commit(item, picked);
                    }
                }, null, compact: true);

                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(box);
                panel.Children.Add(browse);
                return panel;
            }

            case PluginSettingKind.Text:
            {
                var box = new TextBox
                {
                    Width = 250,
                    Text = item.GetValue?.Invoke() as string ?? string.Empty,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                if (item.Placeholder is not null)
                {
                    box.ToolTip = item.Placeholder;
                }

                box.LostFocus += (_, _) => Commit(item, box.Text, rebuild: false);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Commit(item, box.Text);
                    }
                };

                return box;
            }

            case PluginSettingKind.Action:
            {
                var button = MakeButton(item.ActionLabel ?? item.Label, () =>
                {
                    try
                    {
                        item.Invoke?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"设置动作执行失败：{item.Label}", ex);
                    }

                    RequestRebuild();
                }, item.Description);

                return button;
            }

            default:
                return null;
        }
    }

    private void Commit(PluginSettingItem item, object? value, bool rebuild = true)
    {
        try
        {
            item.SetValue?.Invoke(value);
        }
        catch (Exception ex)
        {
            AppLog.Error($"设置写入失败：{item.Label}", ex);
            _shell.Notify($"设置写入失败：{ex.Message}", NotificationKind.Error);
        }

        if (rebuild)
        {
            RequestRebuild();
        }
    }

    // ---------------------------------------------------------------- 小工具

    private FrameworkElement Card(UIElement content) => new Border
    {
        Margin = new Thickness(0, 0, 0, 12),
        Padding = new Thickness(14, 12, 14, 12),
        CornerRadius = new CornerRadius(10),
        Background = (Brush)FindResource("SurfaceBrush"),
        BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1),
        Child = content,
    };

    private FrameworkElement BuildNotice(string text) => Card(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("MutedForegroundBrush"),
    });

    private Button MakeButton(string text, Action action, string? tooltip, bool compact = false)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("PanelButtonStyle"),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = tooltip,
        };

        if (compact)
        {
            button.Padding = new Thickness(8, 4, 8, 4);
            button.FontSize = 11.5;
        }

        button.Click += (_, _) => action();
        return button;
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        string s when double.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };

    private static string FormatNumber(double value, string? suffix)
    {
        var text = Math.Abs(value - Math.Round(value)) < 0.001
            ? Math.Round(value).ToString("0")
            : value.ToString("0.##");

        return string.IsNullOrEmpty(suffix) ? text : $"{text} {suffix}";
    }

    private string? PickFolder(string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件夹", Multiselect = false };

        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickFile(string current)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择文件",
            CheckFileExists = true,
            Filter = "所有文件|*.*",
        };

        var folder = Path.GetDirectoryName(current);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开路径失败：{path}", ex);
        }
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e) => OpenPath(AppPaths.ConfigDirectory);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
