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
/// 刻意不引入任何 NuGet 包：CPU 与内存都直接读 Win32 计数，磁盘走 DriveInfo，
/// 这样插件目录里只有自己一个 DLL —— 是“插件应该尽量薄”的示范。
///
/// 同时也是“徽标（Badge）”与 <c>InvalidateBarItem</c> 合并刷新机制的示范。
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

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _intervalSeconds = Math.Clamp(context.GetSetting("IntervalSeconds", DefaultIntervalSeconds), 1, 30);
        _showBadge = context.GetSetting("ShowBadge", true);
        context.Logger.Info($"系统监视插件已就绪（每 {_intervalSeconds} 秒采样）");
        Sample();
    }

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

    public string? Tooltip => $"CPU {_cpuPercent:0}% · 内存 {MemoryUsedPercent():0}%";

    public string? Badge => _showBadge ? _badge : null;

    public bool BadgeIsAccent => _cpuPercent >= 80;

    public void OnClick(BarItemClickContext context)
    {
        // 左键单击才由插件接管面板开关；中键的关闭语义交给宿主（详见 BarItemClickContext 的说明）。
        if (context.Kind == BarItemActivationKind.Primary)
        {
            context.TogglePanel();
        }
    }

    // ---------------------------------------------------------------- 内嵌内容

    public double WidgetWidth => 74;

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

    /// <summary>采样间隔变化后重建计时器（立即生效）。</summary>
    private void RestartTimer()
    {
        _timer?.Stop();
        _timer = null;
        EnsureTimer();
    }

    private void StopTimerIfIdle()
    {
        if (_barText is null && _compactText is null && _panelCpu is null)
        {
            _timer?.Stop();
            _timer = null;
        }
    }

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

    private void UpdateBarText()
    {
        var text = $"C{_cpuPercent:0} M{MemoryUsedPercent():0}";
        SetText(_barText, text);

        // 悬停提示里给出完整读数（文字被省略号截断时也不用猜）
        if (_barWidgetBorder is not null)
        {
            _barWidgetBorder.ToolTip = $"CPU {_cpuPercent:0}% · 内存 {MemoryUsedPercent():0}%（点击查看详细指标）";
        }
    }

    private void UpdateCompactText() => SetText(_compactText, $"CPU {_cpuPercent:0}%  RAM {MemoryUsedPercent():0}%");

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

    private static double MemoryUsedPercent()
    {
        var memory = ReadMemory();
        return memory.UsedPercent;
    }

    private static ulong ToTicks(FILETIME time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    // ---------------------------------------------------------------- UI 小工具

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

    private static void SetText(TextBlock? block, string text)
    {
        if (block is not null && block.Text != text)
        {
            block.Text = text;
        }
    }

    private static Brush Muted() => BrushFrom("#FF8A8A94");

    private static Brush Accent() => BrushFrom("#FF33A9FF");

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
