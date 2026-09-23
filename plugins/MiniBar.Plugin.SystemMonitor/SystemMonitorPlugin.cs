using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MiniBar.Sdk;

namespace MiniBar.Plugin.SystemMonitor;

/// <summary>
/// 示例插件：系统资源监视（CPU / 内存 / 磁盘 / 宿主机进程占用）。
///
/// 这个插件演示了什么：
///   · 在任务栏徽标（Badge）、内嵌读数（Widget）、悬停面板、迷你条这四种"门面"上同时展示同一份实时数据；
///   · 用 <c>DispatcherTimer</c> 周期性采样，并在所有门面都关闭时自动停表以省 CPU；
///   · 用 <c>InvalidateBarItem</c> 让宿主把同一帧内的多次刷新请求合并成一次，避免界面抖动。
///
/// 用了哪些能力：
///   · CPU 走 Win32 的 <c>GetSystemTimes</c>（P/Invoke），内存走 <c>GlobalMemoryStatusEx</c>（P/Invoke）；
///   · 磁盘走纯托管的 <c>DriveInfo</c>，自身进程信息走 <c>System.Diagnostics.Process</c>；
///   · 刻意不引入任何 NuGet 包，插件目录里只有自己一个 DLL —— 是"插件应该尽量薄"的示范。
///
/// 新手能从中学到什么：
///   · 如何用 <c>[DllImport]</c> 把 kernel32 的系统计数器搬进托管代码；
///   · WPF 的 <c>TextBlock</c> / <c>ProgressBar</c> / <c>StackPanel</c> / <c>Border</c> 怎么拼出轻量面板；
///   · "引用计数式"的生命周期：哪层界面活着就保活计时器。
///
/// 设计取舍：
///   · CPU 用"两帧增量"而非瞬时值，因此首次进入返回 0（没有上一帧可比）；
///   · 内存只读物理内存占用率，不区分已提交/分页文件，因为徽标只需要一个稳定数字；
///   · 磁盘只读系统盘，避免遍历所有卷带来额外开销。
/// </summary>
[PluginManifest(
    "minibar.sysmon",
    "系统监视",
    Description = "任务栏上的 CPU / 内存实时读数，面板里有详细指标",
    Author = "MiniBar Samples",
    Version = "1.0.0",
    Icon = "emoji:📈",
    Order = 10,
    DefaultPinned = true)]
public sealed class SystemMonitorPlugin : IMinibarPlugin, ITaskButtonPlugin, IBarWidgetPlugin,
    IPanelContentPlugin, ICompactContentPlugin, IContextMenuPlugin, ISettingsPlugin
{
    private const int DefaultIntervalSeconds = 2;

    private int _intervalSeconds = DefaultIntervalSeconds;
    private bool _showBadge = true;

    private IPluginContext _context = null!;
    private DispatcherTimer? _timer;

    private TextBlock? _barText;

    /// <summary>内嵌内容外面那层 Border（用来把"完整读数"同步到悬停提示上）。</summary>
    private Border? _barWidgetBorder;
    private TextBlock? _compactText;
    private TextBlock? _panelCpu;
    private TextBlock? _panelMemory;
    private TextBlock? _panelDisk;
    private TextBlock? _panelSelf;
    private ProgressBar? _cpuBar;
    private ProgressBar? _memBar;

    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private double _cpuPercent;
    private string? _badge;

    /// <summary>插件入口：拿到宿主上下文、读用户设置、做首次采样。
    /// <para><b>为什么在这里：</b>初始化是读取用户设置（采样间隔、是否显示徽标）的唯一时机；
    /// 读完立即 <c>Sample()</c> 一次，让任务栏在插件刚露面时就有读数，而不是空白等待一个间隔。</para>
    /// <para><b>Math.Clamp：</b>把"间隔秒数"夹在 1~30 之间，防止用户在设置里误填 0 或极大值导致空转或几乎不刷新。</para></summary>
    public void Initialize(IPluginContext context)
    {
        _context = context;
        _intervalSeconds = Math.Clamp(context.GetSetting("IntervalSeconds", DefaultIntervalSeconds), 1, 30);
        _showBadge = context.GetSetting("ShowBadge", true);
        context.Logger.Info($"系统监视插件已就绪（每 {_intervalSeconds} 秒采样）");
        Sample();
    }

    /// <summary>释放资源：停表、断开所有 UI 引用。
    /// <para><b>为什么手动置 null：</b><c>DispatcherTimer</c> 的 <c>Tick</c> 回调会间接引用整个插件；
    /// 不显式 Stop 并清空字段，GC 收不掉，面板关了插件还在后台跑。</para></summary>
    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _barText = null;
        _compactText = null;
        _panelCpu = null;
        _panelMemory = null;
        _panelDisk = null;
        _panelSelf = null;
    }

    // ---------------------------------------------------------------- 任务栏

    public PluginIcon Icon => "emoji:📈";

    public string DisplayName => "系统监视";

    /// <summary>鼠标悬停在任务栏图标上时显示的提示文本（CPU 与内存占用）。
    /// <para>宿主每次刷新图标时都会读它，所以这里只做字符串拼接，不做任何耗时计算。</para>
    /// </summary>
    public string? Tooltip => $"CPU {_cpuPercent:0}% · 内存 {MemoryUsedPercent():0}%";

    public string? Badge => _showBadge ? _badge : null;

    /// <summary>徽标是否用"强调色"绘制：CPU 很高（≥80%）时变醒目红/橙，提醒用户机器在吃紧。</summary>
    public bool BadgeIsAccent => _cpuPercent >= 80;

    /// <summary>点击任务栏图标的处理：只在左键单击时自己开关面板。
    /// <para>中键（关闭界面）与双击的语义留给宿主，避免两边各切换一次导致「面板开一下马上关」。</para>
    /// </summary>
    public void OnClick(BarItemClickContext context)
    {
        // 左键单击才由插件接管面板开关；中键的关闭语义交给宿主（详见 BarItemClickContext 的说明）。
        if (context.Kind == BarItemActivationKind.Primary)
        {
            context.TogglePanel();
        }
    }

    // ---------------------------------------------------------------- 内嵌内容

    /// <summary>
    /// 内嵌读数的建议宽度。
    /// 侧边（左/右）模式下任务栏只有几十像素宽，横排的 "C15 M62" 必然被截掉，
    /// 所以那时改走竖排两行，宽度也收窄 —— 反正 "C15" 这种四字符占不了 74 DIP。
    /// </summary>
    public double WidgetWidth => _context.IsBarVertical ? 44 : 74;

    /// <summary>创建任务栏上的内嵌读数控件（一个 <c>TextBlock</c> 包在 <c>Border</c> 里）。
    /// <para><b>为什么要 Border 包一层：</b>悬停提示（Tooltip）挂在 Border 上而不是 TextBlock 上，
    /// 这样文字被 <c>TextTrimming.CharacterEllipsis</c> 省略号截断时，鼠标移到任意位置都能看到完整读数。</para>
    /// <para><b>TextBlock：</b>WPF 的轻量只读文本控件；这里不放滚动视图，因为字数极少、要的是"一眼看完"。</para>
    /// <para><b>为什么这里就 EnsureTimer：</b>Widget 一露面就要开始采样，否则读数会停在旧值。</para></summary>
    /// <summary>创建任务栏上的内嵌读数（形如 C15 M62）。
    /// <para><b>为什么要自己算宽度：</b>数字位数会变（9% → 15%），如果返回 0（自动宽度），
    /// 任务栏会跟着左右抖动；这里用 WidgetWidth 给一个固定宽度把格子钉住。</para>
    /// <para>返回的元素里<b>不要</b>设置 Tag = "Interactive"，否则点它不会切换面板。</para>
    /// </summary>
    public FrameworkElement CreateBarWidget()
    {
        _barText = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Accent(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            // 空间不够时用省略号收尾（不会被硬切），悬停时提示里能看到完整读数
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        UpdateBarText();
        EnsureTimer();

        _barWidgetBorder = new Border
        {
            Padding = new Thickness(4, 0, 4, 0),
            Child = _barText,
            ToolTip = "点击查看详细指标",
        };

        return _barWidgetBorder;
    }

    /// <summary>内嵌读数被移除（任务栏重建 / 插件禁用）时调用。
    /// <para>把界面字段置 null 并让计时器有机会停掉 —— 这是插件能被真正卸载的关键约定。</para>
    /// </summary>
    public void ReleaseBarWidget()
    {
        _barText = null;
        _barWidgetBorder = null;
        StopTimerIfIdle();
    }

    // ---------------------------------------------------------------- 面板

    public string PanelTitle => "系统监视";

    public double PreferredWidth => 320;

    public double PreferredHeight => 300;

    /// <summary>创建面板内容（CPU / 内存 / 磁盘 / MiniBar 自身占用）。
    /// <para>界面全部用代码拼：宿主不认识插件的类型，不会给我们注入 XAML；
    /// 颜色一律取自 IPluginContext.Theme，这样深浅主题切换时插件界面会跟着变。</para>
    /// </summary>
    public FrameworkElement CreateContent(IPanelHost host)
    {
        _cpuBar = MakeBar();
        _memBar = MakeBar();
        _panelCpu = MakeValue();
        _panelMemory = MakeValue();
        _panelDisk = MakeValue();
        _panelSelf = MakeValue();

        var root = new StackPanel { Margin = new Thickness(6, 6, 6, 6) };

        root.Children.Add(MakeLabel("CPU 使用率"));
        root.Children.Add(_cpuBar);
        root.Children.Add(_panelCpu);

        root.Children.Add(MakeLabel("内存"));
        root.Children.Add(_memBar);
        root.Children.Add(_panelMemory);

        root.Children.Add(MakeLabel("磁盘"));
        root.Children.Add(_panelDisk);

        root.Children.Add(MakeLabel("MiniBar 自身"));
        root.Children.Add(_panelSelf);

        root.Children.Add(new TextBlock
        {
            Text = $"每 {_intervalSeconds} 秒刷新一次。关掉面板后计时器会停止，不占 CPU。",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted(),
        });

        UpdatePanel();
        EnsureTimer();

        return root;
    }

    /// <summary>面板关闭：释放界面引用（宿主只会丢掉内容，不会替我们停计时器）。</summary>
    public void ReleaseContent()
    {
        _cpuBar = null;
        _memBar = null;
        _panelCpu = null;
        _panelMemory = null;
        _panelDisk = null;
        _panelSelf = null;
        StopTimerIfIdle();
    }

    // ---------------------------------------------------------------- 迷你内容

    public string CompactTitle => "资源";

    public double PreferredCompactWidth => 150;

    public double PreferredCompactHeight => 26;

    /// <summary>迷你模式（有别的程序全屏）时显示的紧凑内容：只要一行 CPU / RAM 读数。</summary>
    public FrameworkElement CreateCompactContent()
    {
        _compactText = new TextBlock
        {
            FontSize = 13,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            Foreground = Accent(),
            VerticalAlignment = VerticalAlignment.Center,
        };

        UpdateCompactText();
        EnsureTimer();

        return _compactText;
    }

    /// <summary>紧凑内容被移除时调用：清引用并让心跳有机会降频。</summary>
    public void ReleaseCompactContent()
    {
        _compactText = null;
        StopTimerIfIdle();
    }

    // ---------------------------------------------------------------- 菜单

    public IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context)
    {
        yield return PluginMenuEntry.Command("立即刷新一次", () =>
        {
            Sample();
            _context.InvalidateBarItem();
        }, "glyph:E72C");

        yield return PluginMenuEntry.Command("打开任务管理器",
            () => Launch("taskmgr.exe", null),
            "glyph:E9D9");
    }

    // ---------------------------------------------------------------- 设置项（宿主设置窗口里显示）

    public IEnumerable<PluginSettingsSection> GetSettingsSections()
    {
        yield return new PluginSettingsSection
        {
            Title = "系统监视",
            Description = "任务栏读数与采样频率",
            Icon = "emoji:📈",
            Items = new[]
            {
                PluginSettingItem.Number(_context, "IntervalSeconds", "采样间隔", DefaultIntervalSeconds,
                    1, 30, 1, "秒", "每次采样只是一次 Win32 调用，间隔短也几乎不占 CPU",
                    () =>
                    {
                        _intervalSeconds = Math.Clamp(
                            _context.GetSetting("IntervalSeconds", DefaultIntervalSeconds), 1, 30);
                        RestartTimer();
                        Sample();
                    }),

                PluginSettingItem.Toggle(_context, "ShowBadge", "图标上显示 CPU 徽标", true,
                    "关闭后任务栏图标右上角不再显示百分比",
                    () =>
                    {
                        _showBadge = _context.GetSetting("ShowBadge", true);
                        Sample();
                    }),

                PluginSettingItem.Button("立即采样一次", Sample, "马上刷新一次读数"),
                PluginSettingItem.Button("打开任务管理器", () => Launch("taskmgr.exe", null)),
                PluginSettingItem.Note("数据来自 Win32（GetSystemTimes / GlobalMemoryStatusEx），插件零第三方依赖。"),
            },
        };
    }

    // ---------------------------------------------------------------- 采样

    /// <summary>按需启动心跳计时器（已经启动过就直接返回）。
    /// <para>用 DispatcherTimer 而不是 System.Timers.Timer：前者跑在界面线程上，Tick 里可以直接改
    /// TextBlock；后者跑在线程池线程，改界面必须自己 Dispatcher.Invoke。</para>
    /// </summary>
    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(_intervalSeconds),
        };

        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    /// <summary>采样间隔变化后重建计时器（立即生效）。
    /// <para>不直接改 <c>Interval</c> 后指望它生效——运行中改间隔在某些 WPF 版本下不重新调度，
    /// 所以干脆 Stop + 置 null + 重新 EnsureTimer，最稳。</para></summary>
    /// <summary>采样间隔被用户改了：重建计时器让它立即生效。</summary>
    private void RestartTimer()
    {
        _timer?.Stop();
        _timer = null;
        EnsureTimer();
    }

    /// <summary>三种界面（任务栏读数 / 面板 / 紧凑内容）都不在显示时就把心跳停掉。
    /// <para><b>这是低占用的关键：</b>没人看的时候一次系统调用都不做。</para>
    /// </summary>
    private void StopTimerIfIdle()
    {
        if (_barText is null && _compactText is null && _panelCpu is null)
        {
            _timer?.Stop();
            _timer = null;
        }
    }

    /// <summary>一次完整采样：算 CPU%、更新徽标文字、刷新三处门面，最后 <c>InvalidateBarItem</c> 让宿主重绘任务栏。
    /// <para><b>为什么集中在一个方法：</b>手动刷新、定时刷新、菜单刷新走的都是同一份逻辑，
    /// 抽出来就不会出现"某处刷新了某处没刷新"的不一致。</para>
    /// <para>徽标变了就 Invalidate：宿主会把同一帧内的多次请求合并成一次重绘，不会抖。</para></summary>
    /// <summary>采样一次并刷新所有可见界面。
    /// <para>顺序：读 CPU → 更新读数与徽标 → 通知宿主刷新图标（宿主会把同一帧内的多次请求合并）。</para>
    /// </summary>
    private void Sample()
    {
        _cpuPercent = ReadCpuPercent();
        _badge = $"{_cpuPercent:0}%";

        UpdateBarText();
        UpdateCompactText();
        UpdatePanel();

        // 徽标变了，让宿主刷新图标（宿主会把同一帧内的多次请求合并）
        _context.InvalidateBarItem();
    }

    /// <summary>刷新任务栏读数，并把完整读数同步到悬停提示（文字被省略号截断时也能看全）。</summary>
    private void UpdateBarText()
    {
        // 侧边（纵向）模式下任务栏很窄，一行放不下两个读数 —— 改成上下两行 "C15" / "M62"。
        var text = _context.IsBarVertical
            ? $"C{_cpuPercent:0}\nM{MemoryUsedPercent():0}"
            : $"C{_cpuPercent:0} M{MemoryUsedPercent():0}";

        SetText(_barText, text);

        if (_barText is not null)
        {
            _barText.TextAlignment = _context.IsBarVertical ? TextAlignment.Center : TextAlignment.Left;
        }

        // 悬停提示里给出完整读数（文字被省略号截断时也不用猜）
        if (_barWidgetBorder is not null)
        {
            _barWidgetBorder.ToolTip = $"CPU {_cpuPercent:0}% · 内存 {MemoryUsedPercent():0}%（点击查看详细指标）";
        }
    }

    private void UpdateCompactText() => SetText(_compactText, $"CPU {_cpuPercent:0}%  RAM {MemoryUsedPercent():0}%");

    /// <summary>刷新面板四组读数（CPU/内存/磁盘/自身）。</summary>
    private void UpdatePanel()
    {
        if (_panelCpu is null)
        {
            return;
        }

        var memory = ReadMemory();

        if (_cpuBar is not null)
        {
            _cpuBar.Value = _cpuPercent;
        }

        if (_memBar is not null)
        {
            _memBar.Value = memory.TotalMb <= 0 ? 0 : (memory.UsedMb / memory.TotalMb) * 100;
        }

        SetText(_panelCpu, $"{_cpuPercent:0.0} %");
        SetText(_panelMemory, $"{memory.UsedMb:0} / {memory.TotalMb:0} MB（{memory.UsedPercent:0}%）");

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            SetText(_panelDisk, $"{drive.Name} 可用 {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.0} GB " +
                                $"/ 共 {drive.TotalSize / 1024.0 / 1024 / 1024:0.0} GB");
        }
        catch
        {
            SetText(_panelDisk, "不可用");
        }

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetText(_panelSelf, $"内存 {self.WorkingSet64 / 1024.0 / 1024:0.0} MB · 线程 {self.Threads.Count} · " +
                            $"运行 {DateTime.Now - self.StartTime:hh\\:mm\\:ss}");
    }

    // ---------------------------------------------------------------- Win32 采样

    /// <summary>读 CPU 占用率。
    /// <para><b>GetSystemTimes 的原理：</b>它给的是系统开机以来累计的空闲 / 内核 / 用户时间（单位 100ns）。
    /// CPU 使用率无法直接读到，只能「两次采样求差」：本次与上次的差值里，非空闲部分占多少比例。</para>
    /// <para>所以第一次调用必然不准（没有上一次的数据可比）—— 这里用字段缓存上次的值，
    /// 第一次返回 0，第二次起才准。</para>
    /// </summary>
    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTicks = ToTicks(idle);
        var kernelTicks = ToTicks(kernel);
        var userTicks = ToTicks(user);

        if (_lastKernel == 0 && _lastUser == 0)
        {
            _lastIdle = idleTicks;
            _lastKernel = kernelTicks;
            _lastUser = userTicks;
            return 0;
        }

        var idleDelta = idleTicks - _lastIdle;
        var totalDelta = (kernelTicks - _lastKernel) + (userTicks - _lastUser);

        _lastIdle = idleTicks;
        _lastKernel = kernelTicks;
        _lastUser = userTicks;

        if (totalDelta == 0)
        {
            return _cpuPercent;
        }

        var usage = (1.0 - ((double)idleDelta / totalDelta)) * 100.0;
        return Math.Clamp(usage, 0, 100);
    }

    /// <summary>读物理内存总量 / 已用量 / 占用百分比。
    /// <para><b>GlobalMemoryStatusEx 的坑：</b>调用前必须先把 dwLength 设成结构体大小，
    /// 否则函数直接返回失败 —— 这是相当多 Win32 结构体的通用约定。</para>
    /// <para>返回值用元组（ValueTuple）一次带回三个数，比专门定义一个小类更轻。</para>
    /// </summary>
    private static (double TotalMb, double UsedMb, double UsedPercent) ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0);
        }

        const double mb = 1024.0 * 1024.0;
        var total = status.ullTotalPhys / mb;
        var available = status.ullAvailPhys / mb;
        var used = total - available;

        return (total, used, total <= 0 ? 0 : (used / total) * 100);
    }

    /// <summary>只要百分比时的便捷封装（任务栏提示与徽标都用它）。</summary>
    private static double MemoryUsedPercent()
    {
        var memory = ReadMemory();
        return memory.UsedPercent;
    }

    /// <summary>把 Win32 的 FILETIME（高低两个 32 位字段）拼成一个 64 位整数。
    /// <para>左移 32 位后按位或上低 32 位；ulong 是无符号的，不必担心符号位。</para>
    /// </summary>
    private static ulong ToTicks(FILETIME time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    // ---------------------------------------------------------------- UI 小工具

    /// <summary>做一个小号灰色标签（面板里那些 CPU / 内存 标题）。</summary>
    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(0, 10, 0, 4),
    };

    private static TextBlock MakeValue() => new()
    {
        FontSize = 12.5,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
    };

    private static ProgressBar MakeBar() => new()
    {
        Height = 6,
        Minimum = 0,
        Maximum = 100,
        BorderThickness = new Thickness(0),
        Foreground = Accent(),
        Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
    };

    /// <summary>安全地给 TextBlock 赋文字：空引用不崩，文字没变就不重设（避免无谓重绘）。</summary>
    /// <summary>给 TextBlock 赋值的空安全小工具：字段可能已经被 Release 置为 null。
    /// <para>只在文字真的变化时赋值，避免无意义的界面重绘。</para>
    /// </summary>
    private static void SetText(TextBlock? block, string text)
    {
        if (block is not null && block.Text != text)
        {
            block.Text = text;
        }
    }

    private static Brush Muted() => BrushFrom("#FF8A8A94");

    private static Brush Accent() => BrushFrom("#FF33A9FF");

    /// <summary>把 #AARRGGBB 字符串转成画刷。
    /// <para>Freeze() 之后画刷不可变，可以跨线程复用，渲染也更快。</para>
    /// </summary>
    private static Brush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static void Launch(string file, string? arguments)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arguments ?? string.Empty)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    /// <summary>镜像 Win32 的 MEMORYSTATUSEX：系统物理/虚拟内存统计。
    /// <para>dwLength 必须预先填成结构体大小（见 <c>ReadMemory</c> 里的 Marshal.SizeOf）；
    /// ullTotalPhys / ullAvailPhys 分别是总物理内存与可用物理内存（字节）。</para></summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    /// <summary>声明 kernel32 的 GlobalMemoryStatusEx。
    /// <para><b>ref MEMORYSTATUSEX：</b>用 ref 而非 out，是因为调用前我们要先填 dwLength 字段（自描述大小），
    /// 函数再就地写回其余字段；out 做不到"先入参后出参"。</para></summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
