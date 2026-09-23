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
/// 设置窗口 —— <b>本程序唯一的"管理界面"</b>。
///
/// <para><b>布局</b>：左侧是导航（宿主设置 → 插件管理 → 每个插件），右侧是按
/// <see cref="PluginSettingsSection"/> 渲染出来的卡片。原来那个独立的"插件管理窗口"已经合并进
/// 「插件管理」这一页，于是"看插件 / 管插件 / 改插件设置"都在同一个窗口里完成。</para>
///
/// <para><b>为什么宿主设置和插件设置长得一样</b>：因为宿主把自己的设置也包装成了
/// <see cref="PluginSettingsSection"/>（见 Services/HostSettingsProvider.cs），
/// 渲染逻辑只有一套 —— 这是"统一模型消除重复代码"的典型做法。</para>
///
/// <para><b>谁负责什么</b></para>
/// <list type="bullet">
///   <item>插件只声明"有哪些设置项、怎么读、怎么写"（GetValue/SetValue）；</item>
///   <item>本窗口负责画控件、把用户改动的值回传；</item>
///   <item>值的持久化由 <c>IPluginContext.GetSetting/SetSetting</c> 完成，落盘在 %APPDATA%\MiniBar\settings.json。</item>
/// </list>
///
/// <para><b>新手看代码的顺序</b>：BuildNav（导航）→ Render（分发）→ BuildSection/BuildItem/CreateControl
/// （渲染一条设置项）→ Commit（写回）。插件侧对应 ISettingsPlugin，示例见久坐提醒插件。</para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// 左侧导航里的一项。三种可能：宿主设置、插件管理页、某个插件。
    /// 用一个类而不是字符串，是为了让模板能同时显示图标、副标题和状态小圆点。
    /// </summary>
    public sealed class NavItem
    {
        /// <summary>导航项标题（宿主设置 / 插件管理 / 插件显示名）。</summary>
        public required string Title { get; init; }

        public string Subtitle { get; init; } = string.Empty;

        /// <summary>导航项图标。PluginIcon 支持 emoji、字体图标、图片三种写法（见 SDK 里的说明）。</summary>
        public PluginIcon Icon { get; init; } = PluginIcon.Default;

        /// <summary>对应的插件；为 null 表示这一项不是插件（是宿主设置或插件管理页）。</summary>
        public PluginDescriptor? Descriptor { get; init; }

        /// <summary>这一项是不是"插件管理"页（不是某个插件，也不是宿主设置）。</summary>
        public bool IsManagerPage { get; init; }

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

    /// <summary>构造设置窗口：记住三个服务、建好左侧导航，并订阅"插件清单变化"事件。</summary>
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

    /// <summary>插件被启用/禁用/删除后重建导航（用 BeginInvoke 延后，避免在插件集合变更的中途改界面）。</summary>
    private void OnPluginsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(BuildNav));

    /// <summary>从插件管理器跳过来时直接定位到某个插件（或插件管理页）。</summary>
    public void SelectPlugin(string pluginId)
    {
        if (string.Equals(pluginId, ShellService.PluginManagerPage, StringComparison.OrdinalIgnoreCase))
        {
            var manager = _nav.FirstOrDefault(n => n.IsManagerPage);

            if (manager is not null)
            {
                NavList.SelectedItem = manager;
            }

            return;
        }

        var target = _nav.FirstOrDefault(n =>
            n.Descriptor is not null && string.Equals(n.Descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (target is not null)
        {
            NavList.SelectedItem = target;
        }
    }

    // ---------------------------------------------------------------- 导航

    /// <summary>重建左侧导航：宿主设置 → 插件管理 → 每个插件（按用户在任务栏上排的顺序）。</summary>
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

        // 插件管理页：加载 / 启用 / 卸载 / 删除 / 使用说明，全都在这一个页面里
        _nav.Add(new NavItem
        {
            Title = "插件管理",
            Subtitle = "加载 · 启用 · 卸载 · 删除 · 使用说明",
            Icon = "glyph:E8FD",
            IsManagerPage = true,
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

    /// <summary>导航选中项变化 → 重画右侧内容。</summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    // ---------------------------------------------------------------- 渲染

    /// <summary>
    /// 重画右侧内容区。这里是整个窗口的"总调度"：
    /// 宿主设置 → 渲染宿主自己那几个分组；插件管理 → 渲染管理控件；某个插件 → 渲染它的设置页。
    /// <para>用 try/catch 包住：插件提供的设置项如果写崩了，也只影响这一页，不至于把整个窗口弄死。</para>
    /// </summary>
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

            if (item.IsManagerPage)
            {
                RenderPluginManager();
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
    /// <summary>
    /// 改完设置后重建当前页（否则读到的是旧值）。
    /// <para>排队到当前事件之后执行：如果直接在 ComboBox 的 SelectionChanged 里重建界面，
    /// 会把正在处理事件的那个控件从树上摘掉，容易出怪问题。</para>
    /// </summary>
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

    /// <summary>
    /// 渲染"插件管理"页：先给一段使用说明，再放插件管理控件（列表 + 拖放区 + 批量操作）。
    ///
    /// <para>
    /// 插件管理控件每次渲染都**新建一个实例**：它没有需要跨渲染保留的状态（开关值都从配置读），
    /// 新建比"复用实例 + 手动从旧父级摘下来"更省事，也避免"元素已经有一个父级"的异常。
    /// </para>
    /// </summary>
    private void RenderPluginManager()
    {
        ContentHost.Children.Add(BuildUsageCard());
        ContentHost.Children.Add(new PluginManagerView(_plugins, _shell, _settings));
    }

    /// <summary>插件管理的使用说明（用户明确要求要"使用说明"，所以写得具体、覆盖常见疑问）。</summary>
    private FrameworkElement BuildUsageCard()
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = "使用说明",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var lines = new (string Glyph, string Text)[]
        {
            ("E896", "装插件：把 DLL 拖到下面这块区域或任务栏上；也可以复制到「用户插件目录」；还可以点「从文件加载…」。全部是热插拔，不用重启程序。"),
            ("E8A7", "打开界面：点任务栏上的图标（或列表里的第一颗按钮）。插件有面板就会弹出来，再点一次关闭；中键点图标是「只关闭，不打开」。"),
            ("E769", "启用 / 禁用：禁用会立刻卸载插件、把内存还给系统，但**DLL 与配置都保留**，随时能再启用。"),
            ("E74D", "卸载并删除：先卸载再删掉 DLL（不可恢复）。只能删这两个插件目录里的文件；从外面拖进来的 DLL 会先复制到用户插件目录，所以不会误删你自己的文件。"),
            ("E718", "固定与排序：这里点图钉固定到任务栏；任务栏上直接拖拽图标就能排序，顺序会记住。"),
            ("E7BA", "「重复文件」是什么：同一个插件 id 在内置目录和用户目录各有一份时，**修改时间新的那份生效**，另一份标成「重复文件」且不加载。把多余的那份删掉即可。"),
            ("E713", "插件设置：左侧导航里每个插件都有自己的设置页；插件没提供设置项时，那里会显示它的数据目录与启用/禁用入口。"),
            ("E9D2", "数据在哪：配置 %APPDATA%\\MiniBar\\settings.json，启用与顺序 plugins.json，插件数据 data\\<插件ID>，日志 logs\\minibar.log；用户插件目录 %LOCALAPPDATA%\\MiniBar\\Plugins。"),
            ("E8FD", "自己写插件：看 README 的《代码阅读导览》与 docs\\插件开发指南.md；plugins\\MiniBar.Plugin.SedentaryReminder 是注释最详细的范例。"),
            ("E765", "快捷键：Ctrl+Alt+, 打开设置，Ctrl+Alt+P 直接跳到这一页，Ctrl+Alt+M 切换迷你模式，Ctrl+Alt+H 显示/隐藏任务栏。"),
        };

        foreach (var (glyph, text) in lines)
        {
            stack.Children.Add(BuildUsageLine(glyph, text));
        }

        return Card(stack);
    }

    /// <summary>说明里的一行：左边一个图标，右边自动换行的文字。</summary>
    private static FrameworkElement BuildUsageLine(string glyph, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = char.ConvertFromUtf32(Convert.ToInt32(glyph, 16)),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Margin = new Thickness(2, 1, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Foreground = (Brush)Application.Current.FindResource("MutedForegroundBrush"),
        };

        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        return row;
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

    /// <summary>插件设置页顶部那张"名片"：图标、名称、版本、状态、能力、文件路径 + 三个操作按钮。</summary>
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
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{descriptor.DisplayName}  {descriptor.Version}",
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{descriptor.Id} · {descriptor.StatusText} · {descriptor.CapabilityText}",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = $"{descriptor.Id}\n{descriptor.CapabilityText}",
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

    /// <summary>把一个设置分组渲染成一张卡片（标题 + 说明 + 若干设置项）。
    /// <para>"分组"这个概念由插件自己声明（PluginSettingsSection），宿主只负责画。</para>
    /// </summary>
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
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = section.Title,
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

    /// <summary>渲染一条设置项：左边是标签与说明，右边是按 Kind 决定的控件。</summary>
    private FrameworkElement BuildItem(PluginSettingItem item)
    {
        if (item.Kind == PluginSettingKind.Info)
        {
            var block = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            block.Children.Add(new TextBlock
            {
                Text = item.Label,
                TextWrapping = TextWrapping.Wrap,
                ToolTip = item.Label,
            });

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

        // 标签可能被窄窗口挤到只剩几个字：用省略号收尾，悬停显示完整文字
        label.Children.Add(new TextBlock
        {
            Text = item.Label,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = item.Label,
        });

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

    /// <summary>
    /// 根据设置项的类型造出对应控件 —— 这就是"宿主渲染、插件提供数据"的落点。
    /// <list type="bullet">
    ///   <item>Bool → CheckBox（勾选/取消各写一次配置）</item>
    ///   <item>Choice → ComboBox（下拉选项由插件给）</item>
    ///   <item>Number → Slider + 数值读数</item>
    ///   <item>Text / Folder / File → TextBox（后两者带"浏览…"按钮）</item>
    ///   <item>Action → 一个按钮</item>
    /// </list>
    /// <para><b>易踩的坑</b>：控件的初值要在<b>订阅事件之前</b>赋值，否则"渲染一次就把配置写一遍"。</para>
    /// </summary>
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

    /// <summary>把用户改动的值交回插件（插件在自己的回调里落盘并即时生效），出错只提示不崩。</summary>
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

    /// <summary>统一外观的卡片容器（圆角 + 主题色背景与边框）。</summary>
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

    /// <summary>做一个小按钮（统一的样式与悬浮提示）。</summary>
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

    /// <summary>把配置里读出来的值（可能是 double / int / 字符串）统一转成 double，供滑块使用。</summary>
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

    /// <summary>弹系统文件夹选择框（.NET 8 的 OpenFolderDialog）。取消时返回 null。</summary>
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

    /// <summary>用资源管理器打开一个目录（不存在就先建出来）。</summary>
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
