using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiniBar.Sdk;

namespace MiniBar.Plugin.QuickNotes;

/// <summary>
/// 示例插件：便签。
///
/// <para><b>它演示了四件事</b></para>
/// <list type="number">
///   <item>面板里放可交互控件（多行文本框），内容按 1 秒<b>防抖</b>自动落盘到插件专属数据目录 ——
///         打字时不写盘，停手一会儿才写，既省磁盘又不会卡界面；</item>
///   <item><see cref="IDropHandlerPlugin"/>：把 .txt / .md 等文本文件拖到任务栏上，内容直接并入便签；</item>
///   <item>徽标（Badge）+ <c>InvalidateBarItem</c>：行数实时显示在任务图标右上角；</item>
///   <item>热键（Ctrl+Alt+N）与右键菜单都能唤出面板，两条入口共用同一段逻辑。</item>
/// </list>
///
/// <para><b>新手最容易漏的两点</b></para>
/// <list type="bullet">
///   <item><c>Dispose</c> 与 <c>ReleaseContent</c> 里必须"停表 + 断事件 + 清引用"，
///         否则插件的 AssemblyLoadContext 回收不掉（表现为「禁用后内存不降」）；</item>
///   <item>点击图标的语义：只在左键单击（<c>Kind == Primary</c>）时自己 <c>TogglePanel()</c>，
///         中键与双击留给宿主 —— 两边都切换会导致「面板开一下马上关」。</item>
/// </list>
///
/// <para>想知道上面这些名词分别是什么，看 <c>docs/新手上路-类与API速查.md</c>。</para>
/// </summary>
[PluginManifest(
    "minibar.notes",
    "便签",
    Description = "随手记；把 .txt/.md 文件拖到任务栏即可并入便签",
    Author = "MiniBar Samples",
    Version = "1.0.0",
    Icon = "emoji:📝",
    Order = 20,
    DefaultPinned = true)]
public sealed class QuickNotesPlugin : IMinibarPlugin, ITaskButtonPlugin, IPanelContentPlugin,
    IContextMenuPlugin, IDropHandlerPlugin, IHotkeyPlugin, ISettingsPlugin
{
    private static readonly string[] TextExtensions = { ".txt", ".md", ".log", ".markdown", ".csv" };

    private IPluginContext _context = null!;
    private string _filePath = string.Empty;
    private TextBox? _editor;
    private TextBlock? _status;
    private DispatcherTimer? _saveTimer;
    private int _lineCount;
    private double _saveDelaySeconds = 1;

    /// <summary>插件初始化：定位数据文件、读回上次的内容、读配置。宿主在"启用插件"时调用一次。</summary>
    public void Initialize(IPluginContext context)
    {
        _context = context;
        _filePath = Path.Combine(context.DataDirectory, "notes.md");
        _saveDelaySeconds = Math.Clamp(context.GetSetting("SaveDelaySeconds", 1d), 0.5, 10);
        _lineCount = CountLines(ReadFromDisk());
        context.Logger.Info($"便签数据文件：{_filePath}");
    }

    /// <summary>插件被禁用 / 卸载 / 宿主退出时调用。
    /// <para>必须在这里停掉防抖计时器、断开文本框事件、清空引用 —— 只要还有活对象引用着插件里的类型，
    /// 可回收的 ALC 就卸不掉（表现为「禁用后内存不降」）。</para>
    /// <para>顺手把内容落盘一次，避免「刚敲完的字还没保存就禁用插件」而丢内容。</para>
    /// </summary>
    public void Dispose()
    {
        Flush();
        _saveTimer?.Stop();
        _saveTimer = null;
        _editor = null;
        _status = null;
    }

    // ---------------------------------------------------------------- 任务栏

    public PluginIcon Icon => "emoji:📝";

    public string DisplayName => "便签";

    public string? Tooltip => _lineCount > 0 ? $"{_lineCount} 行" : "还没有内容，点开写点什么";

    /// <summary>图标角标：显示当前有多少行；空便签时不显示角标（避免一直挂着一个 0）。</summary>
    public string? Badge => _lineCount > 0 ? _lineCount.ToString() : null;

    /// <summary>左键单击 → 插件自己开关面板（宿主看到 ClickHandled 后就不再重复切换）。</summary>
    public void OnClick(BarItemClickContext context)
    {
        // 左键单击 → 插件自己开关面板（宿主看到 ClickHandled 后就不再重复切换）；
        // 中键的"关闭界面"语义由宿主处理，这里不插手。
        if (context.Kind == BarItemActivationKind.Primary)
        {
            context.TogglePanel();
        }
    }

    // ---------------------------------------------------------------- 面板

    /// <summary>面板标题。</summary>
    public string PanelTitle => "便签";

    public double PreferredWidth => 420;

    /// <summary>面板首选高度（像素）。编辑区 + 工具栏 + 状态栏，340 基本一屏。</summary>
    public double PreferredHeight => 340;

    /// <summary>创建面板内容：一个大文本框 + 几个操作按钮 + 状态行。
    /// <para>要点：① AcceptsReturn 打开才能输入多行；② TextChanged 里只重置防抖计时器
    /// （不要每敲一个字就写盘）；③ 状态行里的文件路径很长，所以用 TextTrimming + ToolTip，
    /// 而不是让它撑出一条横向滚动条。</para>
    /// </summary>
    public FrameworkElement CreateContent(IPanelHost host)
    {
        _editor = new TextBox
        {
            Text = ReadFromDisk(),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = BrushFrom(_context.Theme.Foreground),
            CaretBrush = BrushFrom(_context.Theme.Accent),
            FontFamily = new FontFamily("Microsoft YaHei UI, Consolas"),
            FontSize = 13,
        };

        _editor.TextChanged += OnTextChanged;

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };

        toolbar.Children.Add(MakeButton("插入时间戳", () =>
        {
            if (_editor is null)
            {
                return;
            }

            var prefix = _editor.Text.Length > 0 && !_editor.Text.EndsWith('\n') ? "\n" : string.Empty;
            _editor.AppendText($"{prefix}[{DateTime.Now:yyyy-MM-dd HH:mm}] ");
            _editor.CaretIndex = _editor.Text.Length;
            _editor.Focus();
        }));

        toolbar.Children.Add(MakeButton("清空", () =>
        {
            if (_editor is null)
            {
                return;
            }

            if (MessageBox.Show("确定清空便签内容吗？（会立即保存）", "便签",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            _editor.Clear();
        }));

        toolbar.Children.Add(MakeButton("打开数据目录", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _context.Logger.Warn("打开数据目录失败", ex);
            }
        }));

        _status = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = BrushFrom(_context.Theme.Muted),
            // 这行里带着完整的文件路径，一定比面板宽：用省略号收尾 + 悬停看全文，
            // 而不是让它撑出一条横向滚动条（见 FlyoutWindow 里关于横向滚动为什么被禁用的注释）
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_editor, 0);
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(_status, 2);

        grid.Children.Add(_editor);
        grid.Children.Add(toolbar);
        grid.Children.Add(_status);

        UpdateStatus();
        return grid;
    }

    /// <summary>面板关闭：停防抖计时器、断开事件、清引用（低内存的关键约定）。</summary>
    public void ReleaseContent()
    {
        Flush();

        if (_editor is not null)
        {
            _editor.TextChanged -= OnTextChanged;
        }

        _editor = null;
        _status = null;
        _saveTimer?.Stop();
        _saveTimer = null;
    }

    // ---------------------------------------------------------------- 菜单

    /// <summary>右键菜单项（任务栏空白处 / 图标上 / 迷你窗口 / 设置窗口的插件管理页都会收集到）。</summary>
    public IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context)
    {
        yield return PluginMenuEntry.Command("便签面板", () => _context.Shell.TogglePanel(_context.PluginId), "glyph:E8A7");

        yield return PluginMenuEntry.Command("追加一行（含时间戳）", () =>
        {
            var prefix = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "] ";
            AppendText(prefix + Environment.NewLine);
        }, "glyph:E70F");

        yield return PluginMenuEntry.Command("复制全部内容", () =>
        {
            try
            {
                Clipboard.SetText(ReadFromDisk());
                _context.Shell.Notify("便签内容已复制", NotificationKind.Success);
            }
            catch (Exception ex)
            {
                _context.Logger.Warn("复制失败", ex);
            }
        }, "glyph:E8C8");
    }

    // ---------------------------------------------------------------- 快捷键

    /// <summary>声明全局快捷键：Ctrl+Alt+N 打开 / 关闭便签面板。
    /// <para>注册由宿主统一负责（用 Win32 RegisterHotKey），冲突时会回调 OnHotkeyRegistrationFailed。</para>
    /// </summary>
    public IEnumerable<PluginHotkey> GetHotkeys()
    {
        yield return new PluginHotkey("toggle", "Ctrl+Alt+N 打开/关闭便签",
            HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.N);
    }

    /// <summary>快捷键被按下时回调：toggle 对应 hotkeyId 就开/关面板。</summary>
    /// <summary>快捷键被按下（在 UI 线程回调，可以直接操作界面）。</summary>
    public void OnHotkey(string hotkeyId)
    {
        if (hotkeyId == "toggle")
        {
            _context.Shell.TogglePanel(_context.PluginId);
        }
    }

    /// <summary>组合键被别的程序占用时宿主会回调这里 —— 一定要给用户反馈，否则他会以为快捷键坏了。</summary>
    public void OnHotkeyRegistrationFailed(string hotkeyId, string reason) =>
        _context.Shell.Notify($"便签快捷键注册失败：{reason}", NotificationKind.Warning);

    // ---------------------------------------------------------------- 拖放

    /// <summary>是否接管这次拖放：只认领文本类文件（.txt / .md / .log / .csv …）。
    /// <para>这个方法会被宿主频繁调用（拖拽经过时每个移入都会问一次），所以必须快、且不能有副作用。</para>
    /// </summary>
    public bool CanHandle(PluginDropContext context)
    {
        if (context.Paths.Count == 0)
        {
            return false;
        }

        // 只有文本类文件才认领；文件夹与其它类型交给别人
        return context.Paths.All(p => File.Exists(p) &&
                                      TextExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>把拖进来的文本文件内容并入便签。
    /// <para>处理完后要设置 context.Handled = true，表示「我处理了」，宿主的兜底逻辑才不会重复处理。</para>
    /// </summary>
    public void OnDrop(PluginDropContext context)
    {
        var added = 0;

        foreach (var path in context.Paths)
        {
            try
            {
                var text = File.ReadAllText(path);
                var name = Path.GetFileName(path);

                var block = $"{Environment.NewLine}{Environment.NewLine}===== {name}（{DateTime.Now:yyyy-MM-dd HH:mm}）====={Environment.NewLine}{text}";
                AppendText(block);
                added++;
            }
            catch (Exception ex)
            {
                _context.Logger.Warn($"读取文件失败：{path}", ex);
            }
        }

        if (added == 0)
        {
            return;
        }

        context.Handled = true;
        Flush();
        _context.Shell.Notify($"已并入 {added} 个文件的内容到便签", NotificationKind.Success);
        _context.Shell.OpenPanel(_context.PluginId);
    }

    // ---------------------------------------------------------------- 设置项（宿主设置窗口里显示）

    public IEnumerable<PluginSettingsSection> GetSettingsSections()
    {
        yield return new PluginSettingsSection
        {
            Title = "便签",
            Description = "内容自动保存到插件数据目录，不写在宿主配置里",
            Icon = "emoji:📝",
            Items = new[]
            {
                PluginSettingItem.Number(_context, "SaveDelaySeconds", "自动保存延迟", 1,
                    0.5, 10, 0.5, "秒", "停止输入多久后落盘；改成 0.5 秒几乎感觉不到延迟",
                    () =>
                    {
                        _saveDelaySeconds = Math.Clamp(_context.GetSetting("SaveDelaySeconds", 1d), 0.5, 10);
                        _saveTimer?.Stop();
                        _saveTimer = null;
                    }),

                PluginSettingItem.Button("立即保存", Flush, "把当前内容立刻写入磁盘"),
                PluginSettingItem.Button("打开便签文件", () =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"")
                        {
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        _context.Logger.Warn("打开便签文件失败", ex);
                    }
                }, "在资源管理器中定位 notes.md"),

                PluginSettingItem.Note($"数据文件：{_filePath}"),
                PluginSettingItem.Note("把 .txt / .md 文件拖到任务栏上，内容会自动并入便签。"),
            },
        };
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>文本变化：只重置「防抖计时器」，不立刻写盘。
    /// <para><b>为什么要防抖：</b>用户打字时每秒会产生几十次 Change 事件，每次都写文件既费磁盘
    /// 又可能卡界面；等用户停手一会儿再统一写，体验和性能都好得多。</para>
    /// </summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _lineCount = CountLines(_editor?.Text);
        UpdateStatus();
        _context.InvalidateBarItem();

        // 1 秒防抖：连续输入时不会每敲一个字就写盘
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(_saveDelaySeconds),
            };
            _saveTimer.Tick += OnSaveTick;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>防抖计时器到点：真正落盘（见 Flush）。</summary>
    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        Flush();
    }

    private void UpdateStatus()
    {
        if (_status is null)
        {
            return;
        }

        var chars = _editor?.Text.Length ?? 0;
        _status.Text = $"自动保存 · {_lineCount} 行 · {chars} 字 · {_filePath}";

        // 文字被省略号截断时，悬停能看到完整内容（包含完整路径）
        _status.ToolTip = $"自动保存 · {_lineCount} 行 · {chars} 字\n{_filePath}";
    }

    private void AppendText(string text)
    {
        var current = _editor is not null ? _editor.Text : ReadFromDisk();

        if (_editor is not null)
        {
            _editor.AppendText(text);
            _editor.CaretIndex = _editor.Text.Length;
        }
        else
        {
            WriteToDisk(current + text);
            _lineCount = CountLines(current + text);
            _context.InvalidateBarItem();
        }
    }

    /// <summary>把当前内容写进磁盘，并刷新状态行。
    /// <para>写文件用 UTF-8（不带 BOM）—— 这样别的编辑器打开中文不会出现乱码。</para>
    /// </summary>
    private void Flush()
    {
        if (_editor is null)
        {
            return;
        }

        WriteToDisk(_editor.Text);
        _lineCount = CountLines(_editor.Text);
    }

    /// <summary>读便签文件；文件不存在或读取失败时返回空串（首次运行时就走这个分支）。</summary>
    private string ReadFromDisk()
    {
        try
        {
            return File.Exists(_filePath) ? File.ReadAllText(_filePath) : string.Empty;
        }
        catch (Exception ex)
        {
            _context?.Logger.Warn("读取便签失败", ex);
            return string.Empty;
        }
    }

    /// <summary>把内容写入数据文件。
    /// <para><b>Directory.CreateDirectory：</b>先确保目录存在（已存在也不会报错）；<b>File.WriteAllText</b> 一次性覆盖写入。
    /// 全程 try 包住，写盘失败只记日志，不中断用户编辑。</para></summary>
    /// <summary>写便签文件。任何异常都只记日志、不向外抛 —— 插件绝不能让宿主崩掉。</summary>
    private void WriteToDisk(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, text);
        }
        catch (Exception ex)
        {
            _context.Logger.Warn("保存便签失败", ex);
        }
    }

    /// <summary>数行数（空文本算 0 行），用于角标与状态显示。</summary>
    private static int CountLines(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

    /// <summary>做一个小按钮。颜色统一从 IPluginContext.Theme 取，跟随主题变化。</summary>
    private Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
            Foreground = BrushFrom(_context.Theme.Foreground),
            BorderBrush = BrushFrom(_context.Theme.Border),
            BorderThickness = new Thickness(1),
        };

        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>把 "#AARRGGBB" 串变成 WPF 纯色画刷（同 SystemMonitor 的实现）。
    /// <para><b>SolidColorBrush：</b>纯色画刷。<b>ColorConverter：</b>颜色串转 Color 结构。<b>Brush.Freeze()：</b>冻结后不可变、可跨线程复用、渲染更快。</para></summary>
    private static Brush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
