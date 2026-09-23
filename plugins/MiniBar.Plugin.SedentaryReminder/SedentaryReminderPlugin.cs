using System;
using System.Collections.Generic;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiniBar.Sdk;

namespace MiniBar.Plugin.SedentaryReminder;

/// <summary>
/// 示例插件：久坐提醒（每隔一段时间提醒你起来活动一下）。
///
/// <para>
/// 这个插件是**功能最完整的一个范例**，同时用到了 7 种能力，适合照着学：
/// 任务栏图标、任务栏内嵌内容（实时倒计时）、面板内容、右键菜单、全局快捷键、设置项。
/// 代码里每个方法上面都写了"为什么这么做"，注释比代码多是有意的。
/// </para>
///
/// <para><b>核心思路：不数"跳了多少次"，而是记住一个时间点。</b></para>
/// <para>
/// 很多人的第一反应是"每秒给计数器加 1"，但那样一旦电脑休眠、或线程被卡住，
/// 计数就偏了。这里改成记录 <c>_sitSince</c>（当前这一轮"坐着"是从什么时候开始的），
/// 每次想知道"坐了多久"就现算 <c>DateTime.Now - _sitSince</c>。
/// 好处：睡觉/休眠/系统卡顿都不影响结果，逻辑也更容易看懂。
/// </para>
///
/// <para><b>离线时怎么办？</b>用宿主提供的 <see cref="IPluginContext.UserIdleTime"/>
/// 判断"用户是不是已经离开座位了"。离开超过 N 分钟就认为他其实活动过了，自动重新计时 ——
/// 这样不会出现"人出去吃饭两小时，回来一坐下就被提醒"的尴尬。</para>
/// </summary>
[PluginManifest(
    "minibar.sedentary",
    "久坐提醒",
    Description = "到点提醒你站起来活动；面板里有今日统计与一键「我起来活动了」",
    Author = "MiniBar Samples",
    Version = "1.0.0",
    Icon = "emoji:🧘",
    Order = 40,
    DefaultPinned = true)]
public sealed class SedentaryReminderPlugin : IMinibarPlugin, ITaskButtonPlugin, IBarWidgetPlugin,
    IPanelContentPlugin, IContextMenuPlugin, IHotkeyPlugin, ISettingsPlugin
{
    // ================================================================ 常量与默认值

    /// <summary>默认提醒间隔（分钟）。</summary>
    private const double DefaultIntervalMinutes = 45;

    /// <summary>默认"离开多久算活动过"（分钟）。</summary>
    private const double DefaultAwayMinutes = 10;

    /// <summary>UI 可见时的刷新频率：1 秒一次，倒计时才会跳秒。</summary>
    private static readonly TimeSpan VisibleTick = TimeSpan.FromSeconds(1);

    /// <summary>界面不可见时的刷新频率：10 秒一次足够了（只是判断要不要提醒）。</summary>
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(10);

    private static readonly string[] Tips =
    {
        "站起来走两步，顺便看看远处，缓解视疲劳。",
        "转一转脖子和肩膀，别让颈椎一直保持同一个姿势。",
        "喝杯水。久坐 + 缺水是下午犯困的主要原因。",
        "做 10 个深蹲或踮脚，比喝咖啡提神更快。",
        "把显示器抬高一点，视线微微向下 15° 最省脖子。",
    };

    // ================================================================ 运行期状态（不持久化）

    /// <summary>宿主给的运行环境：配置、日志、主题、Shell 能力都在里面。</summary>
    private IPluginContext _context = null!;

    /// <summary>驱动一切的心跳。注意它是 <see cref="DispatcherTimer"/>（跑在 UI 线程上）。</summary>
    private DispatcherTimer? _timer;

    /// <summary>当前这一轮"坐着"的起点。所有计时都以它为准。</summary>
    private DateTime _sitSince = DateTime.Now;

    /// <summary>已经提醒过、但用户还没确认起来活动 —— 用来避免重复提醒。</summary>
    private bool _awaitingBreak;

    /// <summary>暂停到这个时刻为止（点"推迟 10 分钟"或"暂停 1 小时"时设置）。</summary>
    private DateTime? _pausedUntil;

    /// <summary>上一次心跳的时间，用于"按时间差累加"今日久坐分钟数。</summary>
    private DateTime? _lastTickAt;

    /// <summary>上一次提醒的时间（菜单里显示用）。</summary>
    private DateTime? _lastRemindAt;

    /// <summary>心跳处理方法。存成字段是为了 Stop 时能精确取消订阅同一个委托。</summary>
    private EventHandler? _tickHandler;

    // ---- 需要显示的元素（只在实际显示时才创建，释放后置回 null）----

    private TextBlock? _widgetText;          // 任务栏上的那一小格文字
    private Border? _widgetBorder;
    private TextBlock? _panelHeadline;       // 面板大标题："已坐 42 分钟"
    private TextBlock? _panelStatus;         // 面板状态行
    private TextBlock? _panelStats;          // 面板统计行
    private TextBlock? _panelTip;            // 面板小贴士
    private ProgressBar? _panelProgress;     // 面板进度条

    // ================================================================ 持久化状态（写进宿主配置）
    //
    // 约定：GetSetting/SetSetting 的 key 只在**本插件**范围内有效（宿主按插件 ID 隔离），
    // 所以不用担心和别的插件重名。

    /// <summary>今日已累计坐了多少分钟（跨天会自动清零）。</summary>
    private double _sitMinutesToday;

    /// <summary>今日已经起来活动了多少次。</summary>
    private int _breakCountToday;

    /// <summary>"今日"是哪一天（yyyy-MM-dd），用来判断要不要清零统计。</summary>
    private string _today = string.Empty;

    // ================================================================ 可配置项（带缓存，避免每次都读配置）

    private double _intervalMinutes = DefaultIntervalMinutes;
    private bool _onlyWorkHours = true;
    private string _workStart = "09:00";
    private string _workEnd = "18:00";
    private double _awayMinutes = DefaultAwayMinutes;
    private bool _showWidget = true;
    private bool _beep = true;

    // ================================================================ 生命周期

    public void Initialize(IPluginContext context)
    {
        _context = context;

        // 1) 读配置（有默认值，所以第一次运行也能直接跑）
        LoadSettings();

        // 2) 读统计，并处理"跨天清零"
        _today = context.GetSetting("Today", string.Empty) ?? string.Empty;
        _sitMinutesToday = context.GetSetting("SitMinutesToday", 0d);
        _breakCountToday = context.GetSetting("BreakCountToday", 0);
        RolloverIfNewDay();

        // 3) 上一轮"坐着"的起点也存一下：宿主重启后能接着算，而不是从 0 开始
        var ticks = context.GetSetting("SitSinceTicks", DateTime.Now.Ticks);
        _sitSince = ticks > 0 && ticks <= DateTime.Now.Ticks ? new DateTime(ticks) : DateTime.Now;

        // 4) 启动心跳。**即使用户没在看它，也要跑**，否则到点不会提醒。
        EnsureTimer();

        context.Logger.Info($"久坐提醒已就绪：每 {_intervalMinutes:0} 分钟提醒一次" +
                            $"（今日已坐 {_sitMinutesToday:0} 分钟 / 活动 {_breakCountToday} 次）");
    }

    /// <summary>
    /// 插件被禁用 / 卸载 / 宿主退出时调用。
    ///
    /// <para><b>这里必须把定时器停掉、引用清空</b>，这是可回收 ALC 能真正卸载的前提：
    /// 只要还有一个存活对象引用着插件里的类型，整个 AssemblyLoadContext 就回收不了，
    /// 表现就是"禁用插件后内存不降"。</para>
    /// </summary>
    public void Dispose()
    {
        StopTimer();

        _widgetText = null;
        _widgetBorder = null;
        _panelHeadline = null;
        _panelStatus = null;
        _panelStats = null;
        _panelTip = null;
        _panelProgress = null;

        // 把"这一轮"的起点落盘，宿主重启后接着算
        Persist();
    }

    // ================================================================ 任务栏图标（ITaskButtonPlugin）

    public PluginIcon Icon => "emoji:🧘";

    public string DisplayName => "久坐提醒";

    /// <summary>
    /// 图标右上角的小角标。
    /// 只在"该起来了"的时候显示 —— 平时留空，避免任务栏一直挂着数字显得吵。
    /// </summary>
    public string? Badge => _awaitingBreak ? "该动了" : null;

    /// <summary>角标用强调色（默认是灰的）：需要用户注意时才用。</summary>
    public bool BadgeIsAccent => true;

    public string? Tooltip
    {
        get
        {
            if (IsPaused)
            {
                return $"已暂停，{(_pausedUntil!.Value - DateTime.Now).TotalMinutes:0} 分钟后恢复";
            }

            if (_awaitingBreak)
            {
                return "该起来活动一下了（点开面板可以「我起来活动了」）";
            }

            if (!IsWithinWorkHours(DateTime.Now))
            {
                return $"非工作时段（{_workStart}-{_workEnd}），不提醒";
            }

            return $"已坐 {SittingMinutes:0} 分钟，还有 {Math.Max(0, _intervalMinutes - SittingMinutes):0} 分钟提醒你活动";
        }
    }

    public void OnClick(BarItemClickContext context)
    {
        // 左键单击由插件自己接管（宿主看到 ClickHandled 就不会再切一次，避免"开了马上关"）
        if (context.Kind == BarItemActivationKind.Primary)
        {
            context.TogglePanel();
        }
    }

    // ================================================================ 任务栏内嵌内容（IBarWidgetPlugin）

    /// <summary>
    /// 内嵌内容的建议宽度。返回 0 = 让内容自己撑开。
    /// 这里给一个固定值，避免倒计时从 "9 分" 变成 "10 分" 时任务栏左右抖动。
    /// </summary>
    public double WidgetWidth => 74;

    public FrameworkElement CreateBarWidget()
    {
        // 注意：这里是**用代码创建界面**，宿主不会给我们注入 XAML ——
        // 插件是独立的程序集，宿主不知道里面有什么控件。
        _widgetText = new TextBlock
        {
            FontSize = 12,
            // 中文 + 数字混排，用等宽数字看起来更稳
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // 颜色统一从上下文主题里取，这样深色/浅色主题切换时插件界面会跟着变
            Foreground = ToBrush(_context.Theme.Muted),
        };

        _widgetBorder = new Border
        {
            Padding = new Thickness(4, 0, 4, 0),
            Child = _widgetText,
        };

        UpdateWidget();
        EnsureTimer(); // 内嵌内容显示着 → 需要每秒刷新

        return _widgetBorder;
    }

    /// <summary>内嵌内容被移除时调用：清引用，并决定心跳要不要降频。</summary>
    public void ReleaseBarWidget()
    {
        _widgetText = null;
        _widgetBorder = null;
        UpdateTimerInterval();
    }

    // ================================================================ 面板（IPanelContentPlugin）

    public string PanelTitle => "久坐提醒";

    public double PreferredWidth => 330;

    public double PreferredHeight => 300;

    public FrameworkElement CreateContent(IPanelHost host)
    {
        // 面板内容同样用代码拼。StackPanel = 从上到下一行行堆叠的容器。
        var root = new StackPanel();

        _panelHeadline = new TextBlock
        {
            FontSize = 30,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = ToBrush(_context.Theme.Foreground),
        };

        _panelStatus = new TextBlock
        {
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ToBrush(_context.Theme.Muted),
        };

        _panelProgress = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Margin = new Thickness(0, 0, 0, 12),
            // 系统默认进度条是浅色模板，在深色主题里会"亮瞎眼"，所以显式给主题色
            Foreground = ToBrush(_context.Theme.Accent),
            Background = ToBrush(_context.Theme.Surface),
            BorderBrush = ToBrush(_context.Theme.Border),
            BorderThickness = new Thickness(1),
        };

        _panelStats = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ToBrush(_context.Theme.Foreground),
        };

        _panelTip = new TextBlock
        {
            FontSize = 11.5,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ToBrush(_context.Theme.Muted),
        };

        root.Children.Add(_panelHeadline);
        root.Children.Add(_panelStatus);
        root.Children.Add(_panelProgress);
        root.Children.Add(_panelStats);

        // 三个操作按钮。MakeButton 是自己写的工厂方法，统一外观。
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
        };

        buttons.Children.Add(MakeButton("我起来活动了", TakeBreak, accent: true));
        buttons.Children.Add(MakeButton("推迟 10 分钟", () => Pause(10)));
        buttons.Children.Add(MakeButton("暂停 1 小时", () => Pause(60)));

        root.Children.Add(buttons);
        root.Children.Add(_panelTip);

        UpdatePanel();
        EnsureTimer();

        return root;
    }

    /// <summary>面板关闭：面板上的元素会被宿主丢掉，这里把引用清掉即可（心跳会顺便降频）。</summary>
    public void ReleaseContent()
    {
        _panelHeadline = null;
        _panelStatus = null;
        _panelStats = null;
        _panelTip = null;
        _panelProgress = null;
        UpdateTimerInterval();
    }

    // ================================================================ 右键菜单（IContextMenuPlugin）

    /// <summary>
    /// 往宿主菜单里加项。
    /// <paramref name="context"/>.Target 告诉我们菜单是在哪弹出来的（任务栏空白处 / 某个图标上 / 迷你窗口…），
    /// <c>IsForSelf</c> 表示"这次菜单跟本插件有关"，无关时就不必塞自己的项。
    /// </summary>
    public IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context)
    {
        if (!context.IsForSelf)
        {
            yield break;
        }

        yield return PluginMenuEntry.Command("我起来活动了（重新计时）", TakeBreak, "glyph:E8FB");

        yield return PluginMenuEntry.Command("现在就提醒我一次", () => Remind(force: true), "glyph:E7ED");

        yield return PluginMenuEntry.Separator();

        if (IsPaused)
        {
            yield return PluginMenuEntry.Command("恢复计时", Resume, "glyph:E768");
        }
        else
        {
            yield return PluginMenuEntry.Command("暂停 1 小时", () => Pause(60), "glyph:E769");
        }

        yield return PluginMenuEntry.Command("打开面板", () => _context.Shell.TogglePanel(_context.PluginId),
            "glyph:E8A7");
    }

    // ================================================================ 全局快捷键（IHotkeyPlugin）

    public IEnumerable<PluginHotkey> GetHotkeys()
    {
        yield return new PluginHotkey("toggle", "Ctrl+Alt+B 打开/关闭久坐提醒面板",
            HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.B);
    }

    public void OnHotkey(string hotkeyId)
    {
        if (hotkeyId == "toggle")
        {
            _context.Shell.TogglePanel(_context.PluginId);
        }
    }

    /// <summary>
    /// 组合键被别的程序占用时宿主会回调这里。
    /// 一定要给用户一个反馈，否则他会以为"快捷键坏了"。
    /// </summary>
    public void OnHotkeyRegistrationFailed(string hotkeyId, string reason) =>
        _context.Shell.Notify($"久坐提醒的快捷键没注册上（{reason}），可以在面板里点按钮代替",
            NotificationKind.Warning);

    // ================================================================ 设置项（ISettingsPlugin）

    public IEnumerable<PluginSettingsSection> GetSettingsSections()
    {
        yield return new PluginSettingsSection
        {
            Title = "久坐提醒",
            Description = "改完立即生效；健康类的阈值建议按自己情况微调",
            Icon = "emoji:🧘",
            Items = new[]
            {
                PluginSettingItem.Number(_context, "IntervalMinutes", "提醒间隔", DefaultIntervalMinutes,
                    5, 180, 5, "分钟", "医学上更推荐 30~60 分钟起身一次",
                    () =>
                    {
                        LoadSettings();
                        RefreshEverything();
                    }),

                PluginSettingItem.Toggle(_context, "OnlyWorkHours", "只在工作时段提醒", true,
                    "非工作时段完全不打扰（连计时也不计）",
                    () =>
                    {
                        LoadSettings();
                        RefreshEverything();
                    }),

                PluginSettingItem.Choice(_context, "WorkStart", "上班时间",
                    new[] { "07:00", "08:00", "08:30", "09:00", "09:30", "10:00", "13:00" }, "09:00",
                    onChanged: () => { LoadSettings(); RefreshEverything(); }),

                PluginSettingItem.Choice(_context, "WorkEnd", "下班时间",
                    new[] { "16:00", "17:00", "17:30", "18:00", "18:30", "19:00", "21:00" }, "18:00",
                    onChanged: () => { LoadSettings(); RefreshEverything(); }),

                PluginSettingItem.Number(_context, "AwayMinutes", "离开多久算活动过", DefaultAwayMinutes,
                    1, 120, 1, "分钟",
                    "离开座位超过这个时间（比如去吃饭、开会）就自动重新计时，回来不会被立刻提醒"),

                PluginSettingItem.Toggle(_context, "ShowWidget", "在任务栏显示倒计时", true,
                    "关掉后只保留图标与角标，任务栏更清爽",
                    () =>
                    {
                        LoadSettings();

                        // 让宿主重新问一遍"要不要内嵌内容"
                        _context.Shell.ReloadPlugin(_context.PluginId);
                    }),

                PluginSettingItem.Toggle(_context, "Beep", "提醒时播放提示音", true,
                    "用的是系统提示音（不需要额外的音频文件）"),

                PluginSettingItem.Button("测试一次提醒", () => Remind(force: true),
                    "立刻弹一次通知，用来确认提醒效果", "立即测试"),

                PluginSettingItem.Button("清零今日统计", ResetToday),
                PluginSettingItem.Note("数据保存位置：宿主的 settings.json（按插件隔离），不会上传到任何地方。"),
            },
        };
    }

    // ================================================================ 计时核心

    /// <summary>当前这一轮已经坐了多久（分钟）。直接从时间点现算，不依赖定时器跳了多少次。</summary>
    private double SittingMinutes => Math.Max(0, (DateTime.Now - _sitSince).TotalMinutes);

    private bool IsPaused => _pausedUntil is { } until && until > DateTime.Now;

    /// <summary>心跳。每次"跳"一次就判断：要不要提醒、界面要不要刷新。</summary>
    private void Tick()
    {
        RolloverIfNewDay();

        var now = DateTime.Now;

        // ---- 0) 先把"这一小段"坐着的时间累计到今日统计里 ----
        // 用两次心跳的时间差来累加，而不是"每次 +1"，这样即使计算机休眠了也不会算错。
        if (_lastTickAt is { } last && !IsPaused && IsWithinWorkHours(now))
        {
            var delta = (now - last).TotalMinutes;

            // 超过 5 分钟的间隔说明电脑睡过 / 卡过，只记 5 分钟，避免统计里出现"一口气坐了 8 小时"
            _sitMinutesToday += Math.Min(delta, 5);
        }

        _lastTickAt = now;

        // ---- 1) 人不在：自动重新计时（当作已经活动过了）----
        if (_context.UserIdleTime >= TimeSpan.FromMinutes(_awayMinutes))
        {
            if (_awaitingBreak || SittingMinutes >= 1)
            {
                _context.Logger.Info($"检测到离开座位超过 {_awayMinutes:0} 分钟，重新开始计时");
                _sitSince = now;
                _awaitingBreak = false;
                _context.InvalidateBarItem();
            }

            UpdateWidget();
            UpdatePanel();
            return;
        }

        // ---- 2) 该提醒了吗 ----
        var shouldRemind = !IsPaused
                           && !_awaitingBreak
                           && IsWithinWorkHours(now)
                           && SittingMinutes >= _intervalMinutes;

        if (shouldRemind)
        {
            Remind(force: false);
        }

        UpdateWidget();
        UpdatePanel();
    }

    /// <summary>
    /// 弹提醒：托盘通知 + 可选的提示音 + 角标。
    /// <paramref name="force"/> = true 表示用户手动点"测试"，跳过一切条件判断。
    /// </summary>
    private void Remind(bool force)
    {
        if (!force && (IsPaused || _awaitingBreak || !IsWithinWorkHours(DateTime.Now)))
        {
            return;
        }

        _awaitingBreak = true;
        _lastRemindAt = DateTime.Now;

        if (_beep)
        {
            try
            {
                // SystemSounds 是 .NET 自带的系统提示音，不需要音频文件
                SystemSounds.Asterisk.Play();
            }
            catch
            {
                // 没有音频设备时会抛异常，忽略即可
            }
        }

        _context.Shell.Notify(
            $"已经坐了 {SittingMinutes:0} 分钟，起来活动一下吧（点图标可记录）",
            NotificationKind.Warning,
            TimeSpan.FromSeconds(8));

        _context.InvalidateBarItem(); // 角标要变成"该动了"
        UpdatePanel();

        _context.Logger.Info($"发出久坐提醒（已坐 {SittingMinutes:0} 分钟）");
    }

    /// <summary>「我起来活动了」：重新开始一轮计时，并记一次今日活动。</summary>
    private void TakeBreak()
    {
        _sitSince = DateTime.Now;
        _awaitingBreak = false;
        _pausedUntil = null;
        _breakCountToday++;
        Persist();

        _context.Shell.Notify($"记下了 👍 今日已活动 {_breakCountToday} 次", NotificationKind.Success);
        _context.InvalidateBarItem();
        UpdatePanel();
        UpdateWidget();
    }

    /// <summary>暂停一段时间（期间不提醒、不计入久坐）。</summary>
    private void Pause(int minutes)
    {
        _pausedUntil = DateTime.Now.AddMinutes(minutes);

        // 暂停期间不累计久坐，所以把"这一轮的起点"推到恢复的那一刻
        _sitSince = DateTime.Now.AddMinutes(minutes);
        _awaitingBreak = false;
        _context.InvalidateBarItem();
        UpdatePanel();

        _context.Shell.Notify($"已暂停 {minutes} 分钟", NotificationKind.Info);
    }

    private void Resume()
    {
        _pausedUntil = null;
        _sitSince = DateTime.Now;
        _context.InvalidateBarItem();
        UpdatePanel();
    }

    private void ResetToday()
    {
        _breakCountToday = 0;
        _sitMinutesToday = 0;
        Persist();
        UpdatePanel();
        _context.Shell.Notify("今日统计已清零", NotificationKind.Info);
    }

    // ================================================================ 界面刷新

    private void UpdateWidget()
    {
        if (_widgetText is null)
        {
            return;
        }

        if (IsPaused)
        {
            _widgetText.Text = "已暂停";
            _widgetText.Foreground = ToBrush(_context.Theme.Muted);
            return;
        }

        if (_awaitingBreak)
        {
            _widgetText.Text = "该动了";
            _widgetText.Foreground = ToBrush(_context.Theme.Accent);
            return;
        }

        var left = Math.Max(0, _intervalMinutes - SittingMinutes);
        _widgetText.Text = $"坐{left:0}分";
        _widgetText.Foreground = ToBrush(_context.Theme.Muted);

        _context.InvalidateBarItem(); // 徽标/Tooltip 里也有倒计时，一起刷新
    }

    private void UpdatePanel()
    {
        if (_panelHeadline is null)
        {
            return;
        }

        var sitting = SittingMinutes;

        _panelHeadline.Text = $"{(int)sitting} 分 {((int)(sitting * 60)) % 60:00} 秒";

        if (IsPaused)
        {
            _panelStatus!.Text = $"已暂停，{(_pausedUntil!.Value - DateTime.Now).TotalMinutes:0} 分钟后恢复计时";
        }
        else if (!IsWithinWorkHours(DateTime.Now))
        {
            _panelStatus!.Text = $"非工作时段（{_workStart}-{_workEnd}），不提醒";
        }
        else if (_awaitingBreak)
        {
            _panelStatus!.Text = "该起来活动了！起来走两步，回来点一下按钮即可重新计时。";
        }
        else
        {
            var left = Math.Max(0, _intervalMinutes - sitting);
            _panelStatus!.Text = $"还有 {left:0} 分钟提醒你活动";
        }

        // 进度条：0% = 刚坐下，100% = 该提醒了
        _panelProgress!.Value = Math.Clamp(sitting / Math.Max(1, _intervalMinutes) * 100, 0, 100);

        _panelStats!.Text = $"今日：活动 {_breakCountToday} 次 · 累计坐 {_sitMinutesToday + sitting:0} 分钟" +
                            (_lastRemindAt is { } last ? $"\n上次提醒：{last:HH:mm}" : string.Empty);

        if (string.IsNullOrEmpty(_panelTip!.Text))
        {
            _panelTip.Text = "小贴士：" + Tips[Random.Shared.Next(Tips.Length)];
        }
    }

    /// <summary>设置变化后统一刷新一遍（角标、内嵌内容、面板、心跳频率）。</summary>
    private void RefreshEverything()
    {
        UpdateTimerInterval();
        _context.InvalidateBarItem();
        UpdateWidget();
        UpdatePanel();
    }

    // ================================================================ 配置与统计读写

    private void LoadSettings()
    {
        _intervalMinutes = Math.Clamp(_context.GetSetting("IntervalMinutes", DefaultIntervalMinutes), 1, 600);
        _onlyWorkHours = _context.GetSetting("OnlyWorkHours", true);
        _workStart = _context.GetSetting("WorkStart", "09:00") ?? "09:00";
        _workEnd = _context.GetSetting("WorkEnd", "18:00") ?? "18:00";
        _awayMinutes = Math.Clamp(_context.GetSetting("AwayMinutes", DefaultAwayMinutes), 1, 600);
        _showWidget = _context.GetSetting("ShowWidget", true);
        _beep = _context.GetSetting("Beep", true);
    }

    /// <summary>把当前进度写回宿主配置（宿主对写盘做了防抖合并，可以放心频繁调用）。</summary>
    private void Persist()
    {
        _context.SetSetting("SitSinceTicks", _sitSince.Ticks);
        _context.SetSetting("Today", _today);
        _context.SetSetting("SitMinutesToday", _sitMinutesToday);
        _context.SetSetting("BreakCountToday", _breakCountToday);
    }

    /// <summary>跨天（或者第一次运行）就把今日统计清零。</summary>
    private void RolloverIfNewDay()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_today == today)
        {
            return;
        }

        _today = today;
        _sitMinutesToday = 0;
        _breakCountToday = 0;
        _sitSince = DateTime.Now;
        _awaitingBreak = false;
        Persist();
        _context.Logger.Info("跨天，久坐统计已清零");
    }

    /// <summary>是否在工作时段内（没勾"只在工作时段提醒"就永远算在内）。</summary>
    private bool IsWithinWorkHours(DateTime now)
    {
        if (!_onlyWorkHours)
        {
            return true;
        }

        if (!TimeSpan.TryParse(_workStart, out var start) || !TimeSpan.TryParse(_workEnd, out var end))
        {
            return true;
        }

        var time = now.TimeOfDay;

        // 支持跨零点（比如 22:00 - 06:00）
        return start <= end
            ? time >= start && time <= end
            : time >= start || time <= end;
    }

    // ================================================================ 心跳管理

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        // DispatcherTimer 属于当前 UI 线程（插件回调本来就在 UI 线程上，
        // 所以它可以直接改界面，不需要 Dispatcher.Invoke）。
        _tickHandler = (_, _) => Tick();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = VisibleTick,
        };

        _timer.Tick += _tickHandler;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();

        if (_tickHandler is not null)
        {
            _timer.Tick -= _tickHandler;
        }

        _timer = null;
        _tickHandler = null;
    }

    /// <summary>
    /// 界面看得见时每秒刷新（倒计时才会动），看不见时 10 秒一次（只判断要不要提醒）。
    /// 省电、也符合"低占用"的项目目标。
    /// </summary>
    private void UpdateTimerInterval()
    {
        if (_timer is null)
        {
            return;
        }

        var visible = _widgetText is not null || _panelHeadline is not null;
        _timer.Interval = visible ? VisibleTick : IdleTick;
    }

    // ================================================================ 小工具

    /// <summary>做一个小按钮，外观跟宿主面板里的按钮保持一致。</summary>
    private Button MakeButton(string text, Action action, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 12,
            Foreground = accent ? ToBrush(_context.Theme.AccentForeground) : ToBrush(_context.Theme.Foreground),
            Background = accent ? ToBrush(_context.Theme.Accent) : ToBrush(_context.Theme.Surface),
            BorderBrush = ToBrush(_context.Theme.Border),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };

        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>把主题里的 "#AARRGGBB" 字符串变成 WPF 画刷。</summary>
    private static Brush ToBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); // 冻结后不可变，可以跨线程复用，渲染也更快
        return brush;
    }
}
