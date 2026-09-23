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
/// 演示三件事：
///   1) 面板里放可交互控件（多行文本框），内容按 1 秒防抖自动落盘到插件专属数据目录；
///   2) <see cref="IDropHandlerPlugin"/> —— 把 .txt/.md 文件拖到任务栏上，内容直接并入便签；
///   3) 徽标（Badge）+ <c>InvalidateBarItem</c>：行数实时显示在任务图标右上角。
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

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _filePath = Path.Combine(context.DataDirectory, "notes.md");
        _saveDelaySeconds = Math.Clamp(context.GetSetting("SaveDelaySeconds", 1d), 0.5, 10);
        _lineCount = CountLines(ReadFromDisk());
        context.Logger.Info($"便签数据文件：{_filePath}");
    }

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

    public string? Badge => _lineCount > 0 ? _lineCount.ToString() : null;

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

    public string PanelTitle => "便签";

    public double PreferredWidth => 420;

    public double PreferredHeight => 340;

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

    public IEnumerable<PluginHotkey> GetHotkeys()
    {
        yield return new PluginHotkey("toggle", "Ctrl+Alt+N 打开/关闭便签",
            HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.N);
    }

    public void OnHotkey(string hotkeyId)
    {
        if (hotkeyId == "toggle")
        {
            _context.Shell.TogglePanel(_context.PluginId);
        }
    }

    public void OnHotkeyRegistrationFailed(string hotkeyId, string reason) =>
        _context.Shell.Notify($"便签快捷键注册失败：{reason}", NotificationKind.Warning);

    // ---------------------------------------------------------------- 拖放

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

    private void Flush()
    {
        if (_editor is null)
        {
            return;
        }

        WriteToDisk(_editor.Text);
        _lineCount = CountLines(_editor.Text);
    }

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

    private static int CountLines(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

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

    private static Brush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
