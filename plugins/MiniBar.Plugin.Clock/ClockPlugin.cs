using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiniBar.Sdk;

namespace MiniBar.Plugin.Clock;

/// <summary>
/// 示例插件：时钟。
///
/// 它一次性演示了全部能力：
///   · ITaskButtonPlugin   —— 任务栏图标（未固定时出现在溢出菜单里）
///   · IBarWidgetPlugin    —— 直接把时间画在任务栏显示区里
///   · IPanelContentPlugin —— 点图标弹出的世界时钟面板
///   · ICompactContentPlugin —— 全屏时迷你窗口里的紧凑时间
///   · IContextMenuPlugin  —— 往宿主右键菜单里加项（任务栏 / 图标 / 迷你窗口三处）
///   · IHotkeyPlugin       —— 全局快捷键 Ctrl+Alt+T
///
/// 关键约定：只有真正显示出来的界面才持有计时器，被收起时立刻释放 —— 宿主可以长期低占用。
/// </summary>
[PluginManifest(
    "minibar.clock",
    "时钟",
    Description = "任务栏内嵌时间显示，面板里可看多时区时间",
    Author = "MiniBar Samples",
    Version = "1.0.0",
    Icon = "emoji:🕒",
    Order = 0,
    DefaultPinned = true)]
public sealed class ClockPlugin : IMinibarPlugin, ITaskButtonPlugin, IBarWidgetPlugin,
    IPanelContentPlugin, ICompactContentPlugin, IContextMenuPlugin, IHotkeyPlugin, ISettingsPlugin
{
    private static readonly (string City, string TimeZoneId)[] Cities =
    {
        ("北京", "China Standard Time"),
        ("伦敦", "GMT Standard Time"),
        ("纽约", "Eastern Standard Time"),
        ("东京", "Tokyo Standard Time"),
    };

    private IPluginContext _context = null!;
    private DispatcherTimer? _timer;
    private TextBlock? _barText;
    private TextBlock? _panelClock;
    private TextBlock? _panelDate;
    private TextBlock? _compactText;
    private StackPanel? _cityHost;
    private string _barFormat = "HH:mm:ss";

    /// <summary>格式里带不带秒 —— 决定刷新频率与控件宽度。</summary>
    private bool ShowSeconds => _barFormat.Contains("ss", StringComparison.Ordinal);

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _barFormat = context.GetSetting("BarFormat", "HH:mm:ss") ?? "HH:mm:ss";
        context.Logger.Info($"时钟插件已就绪（任务栏格式 {_barFormat}）");
    }

    public void Dispose()
    {
        StopTimer();
        _barText = null;
        _panelClock = null;
        _panelDate = null;
        _compactText = null;
        _cityHost = null;
    }

    // ---------------------------------------------------------------- 任务栏图标

    public PluginIcon Icon => "emoji:🕒";

    public string DisplayName => "时钟";

    public string? Tooltip => ShowSeconds ? "含秒的当前时间" : "当前时间";

    public void OnClick(BarItemClickContext context)
    {
        // 只有"左键单击"才由插件接管面板开关；中键/双击的语义留给宿主，避免两边各切一次。
        // （context.TogglePanel() 会把 context.ClickHandled 置为 true，宿主便不再重复切换）
        if (context.Kind == BarItemActivationKind.Primary)
        {
            context.TogglePanel();
        }
    }

    // ---------------------------------------------------------------- 任务栏内嵌内容

    /// <summary>
    /// 内嵌内容的建议宽度（DIP）。返回 0 表示"由内容自己撑开"，宿主就不设固定宽度。
    ///
    /// 这里按**当前格式的实际文本**估算，而不是写死 84/62：
    /// 写死宽度时，用户一旦把格式改成 "HH:mm:ss ddd"（例如 "23:22:02 周三"），
    /// 文字就会超出这一格被切掉 —— 也就是"内容过多显示不全"。
    /// </summary>
    public double WidgetWidth => EstimateTextWidth(DateTime.Now.ToString(_barFormat)) + 20;

    /// <summary>粗估一段文字的显示宽度：中日韩字符按 13.5 DIP、其余按 8.6 DIP 计，再留 6 DIP 余量。</summary>
    private static double EstimateTextWidth(string text)
    {
        var width = 6d;

        foreach (var ch in text)
        {
            width += ch >= 0x2E80 ? 13.5 : 8.6;
        }

        return Math.Ceiling(width);
    }

    public FrameworkElement CreateBarWidget()
    {
        _barText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13,
            // 等宽数字，秒跳动时不会左右晃
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Quote("ForegroundBrush"),
            // 万一还是放不下（比如用户填了超长格式），退化成省略号而不是被硬切，悬停可看全
            TextTrimming = TextTrimming.CharacterEllipsis,
            Tag = "Interactive",
        };

        UpdateBarText();
        EnsureTimer();

        return new Border
        {
            Padding = new Thickness(4, 0, 4, 0),
            Child = _barText,
            ToolTip = "点这里打开世界时钟面板",
        };
    }

    public void ReleaseBarWidget()
    {
        _barText = null;
        StopTimerIfIdle();
    }

    // ---------------------------------------------------------------- 面板

    public string PanelTitle => "世界时钟";

    public double PreferredWidth => 300;

    public double PreferredHeight => 250;

    public FrameworkElement CreateContent(IPanelHost host)
    {
        _panelClock = new TextBlock
        {
            FontSize = 40,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Quote("ForegroundBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _panelDate = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 10),
            Foreground = Quote("MutedForegroundBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _cityHost = new StackPanel();

        var root = new StackPanel { Margin = new Thickness(4, 6, 4, 4) };
        root.Children.Add(_panelClock);
        root.Children.Add(_panelDate);
        root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 8),
            Background = Quote("SeparatorBrush"),
        });
        root.Children.Add(_cityHost);

        UpdatePanel();
        BuildCityRows();
        EnsureTimer();

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
    }

    public void ReleaseContent()
    {
        _panelClock = null;
        _panelDate = null;
        _cityHost = null;
        StopTimerIfIdle();
    }

    // ---------------------------------------------------------------- 迷你内容

    public string CompactTitle => "时间";

    // 紧凑内容固定用 HH:mm(:ss) 这样短的格式，所以宽度按"带不带秒"估一下就够了
    public double PreferredCompactWidth =>
        EstimateTextWidth(DateTime.Now.ToString(ShowSeconds ? "HH:mm:ss" : "HH:mm")) + 34;

    public double PreferredCompactHeight => 26;

    public FrameworkElement CreateCompactContent()
    {
        _compactText = new TextBlock
        {
            FontSize = 15,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Quote("ForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        UpdateCompactText();
        EnsureTimer();

        return _compactText;
    }

    public void ReleaseCompactContent()
    {
        _compactText = null;
        StopTimerIfIdle();
    }

    // ---------------------------------------------------------------- 右键菜单

    public IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context)
    {
        yield return PluginMenuEntry.Command("复制当前时间", () =>
        {
            try
            {
                Clipboard.SetText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                _context.Shell.Notify("已复制当前时间", NotificationKind.Success);
            }
            catch (Exception ex)
            {
                _context.Logger.Warn("复制失败", ex);
            }
        }, "glyph:E8C8");

        yield return PluginMenuEntry.Toggle(ShowSeconds ? "任务栏只显示时分" : "任务栏显示秒", ShowSeconds, () =>
        {
            _barFormat = ShowSeconds ? "HH:mm" : "HH:mm:ss";
            _context.SetSetting("BarFormat", _barFormat);
            RestartTimer();
            UpdateBarText();
            _context.InvalidateBarItem();
        });

        yield return PluginMenuEntry.Command("世界时钟面板",
            () => _context.Shell.TogglePanel(_context.PluginId),
            "glyph:E8A7");
    }

    // ---------------------------------------------------------------- 全局快捷键

    public IEnumerable<PluginHotkey> GetHotkeys()
    {
        yield return new PluginHotkey("toggle", "Ctrl+Alt+T 打开/关闭时钟", HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.T);
    }

    public void OnHotkey(string hotkeyId)
    {
        if (hotkeyId == "toggle")
        {
            _context.Shell.TogglePanel(_context.PluginId);
        }
    }

    public void OnHotkeyRegistrationFailed(string hotkeyId, string reason) =>
        _context.Shell.Notify($"时钟快捷键注册失败：{reason}", NotificationKind.Warning);

    // ---------------------------------------------------------------- 内部

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 只显示时分时没必要每秒唤醒
            Interval = ShowSeconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(20),
        };

        _timer.Tick += (_, _) =>
        {
            UpdateBarText();
            UpdatePanel();
            UpdateCompactText();
        };

        _timer.Start();
    }

    private void StopTimerIfIdle()
    {
        if (_barText is null && _panelClock is null && _compactText is null)
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void UpdateBarText() => SetText(_barText, DateTime.Now.ToString(_barFormat));

    private void UpdateCompactText() => SetText(_compactText, DateTime.Now.ToString(ShowSeconds ? "HH:mm:ss" : "HH:mm"));

    /// <summary>格式变化后按新频率重建计时器（带秒需要每秒刷新）。</summary>
    private void RestartTimer()
    {
        StopTimer();
        EnsureTimer();
    }

    // ---------------------------------------------------------------- 设置项（宿主设置窗口里显示）

    public IEnumerable<PluginSettingsSection> GetSettingsSections()
    {
        yield return new PluginSettingsSection
        {
            Title = "时钟",
            Description = "改完立即生效，配置写在插件自己的数据里",
            Icon = "emoji:🕒",
            Items = new[]
            {
                new PluginSettingItem
                {
                    Label = "任务栏时间格式",
                    Kind = PluginSettingKind.Choice,
                    Description = "标准 .NET 时间格式字符串；带不带秒同时决定刷新频率",
                    Choices = new[] { "HH:mm:ss", "HH:mm", "HH:mm:ss ddd", "tt h:mm:ss" },
                    GetValue = () => _barFormat,
                    // 手写 GetValue/SetValue 的写法：写配置 + 立刻应用 + 让宿主刷新图标
                    SetValue = value =>
                    {
                        _barFormat = value as string ?? "HH:mm:ss";
                        _context.SetSetting("BarFormat", _barFormat);
                        RestartTimer();
                        UpdateBarText();
                        UpdateCompactText();
                        PluginSettingItem.Applied(_context, null);
                    },
                },

                PluginSettingItem.Toggle(_context, "PanelShowDate", "面板显示日期", true,
                    "关闭后世界时钟面板只留时间", () => UpdatePanel()),

                PluginSettingItem.Button("复制当前时间", () =>
                {
                    try
                    {
                        Clipboard.SetText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        _context.Shell.Notify("已复制当前时间", NotificationKind.Success);
                    }
                    catch (Exception ex)
                    {
                        _context.Logger.Warn("复制失败", ex);
                    }
                }, "把当前时间（含日期）复制到剪贴板"),

                PluginSettingItem.Note("时区列表内置：北京 / 伦敦 / 纽约 / 东京。"),
            },
        };
    }

    private void UpdatePanel()
    {
        if (_panelClock is null)
        {
            return;
        }

        var now = DateTime.Now;
        SetText(_panelClock, now.ToString("HH:mm:ss"));
        SetText(_panelDate, _context.GetSetting("PanelShowDate", true)
            ? now.ToString("yyyy 年 M 月 d 日 dddd")
            : string.Empty);

        if (_cityHost is not null && _cityHost.Children.Count > 0)
        {
            foreach (var child in _cityHost.Children.OfType<Grid>())
            {
                if (child.Tag is not string tz || child.Children.Count < 2)
                {
                    continue;
                }

                try
                {
                    var time = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, tz);
                    if (child.Children[1] is TextBlock value)
                    {
                        SetText(value, time.ToString("HH:mm"));
                    }
                }
                catch
                {
                    // 时区不可用就保持原样
                }
            }
        }
    }

    private void BuildCityRows()
    {
        if (_cityHost is null)
        {
            return;
        }

        foreach (var (city, timeZoneId) in Cities)
        {
            var row = new Grid { Tag = timeZoneId, Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = city,
                FontSize = 12.5,
                Foreground = Quote("ForegroundBrush"),
            };

            var value = new TextBlock
            {
                Text = "--:--",
                FontSize = 12.5,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                Foreground = Quote("MutedForegroundBrush"),
            };

            Grid.SetColumn(value, 1);
            row.Children.Add(name);
            row.Children.Add(value);
            _cityHost.Children.Add(row);
        }

        UpdatePanel();
    }

    private static void SetText(TextBlock? block, string text)
    {
        if (block is not null && block.Text != text)
        {
            block.Text = text;
        }
    }

    /// <summary>
    /// 从宿主主题取色。插件不依赖宿主的资源键名写死也行 —— 这里示范另一种做法：
    /// 用 PluginTheme 的语义色构造画刷。为了让 XAML 之外也能拿到统一的颜色，统一走这里。
    /// </summary>
    private Brush Quote(string key)
    {
        var theme = _context.Theme;

        return key switch
        {
            "MutedForegroundBrush" => ToBrush(theme.Muted),
            "SeparatorBrush" => ToBrush(theme.Border),
            "AccentBrush" => ToBrush(theme.Accent),
            _ => ToBrush(theme.Foreground),
        };
    }

    private static Brush ToBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
