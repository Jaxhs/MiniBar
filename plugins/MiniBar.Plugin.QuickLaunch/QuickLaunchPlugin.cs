using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniBar.Sdk;

namespace MiniBar.Plugin.QuickLaunch;

/// <summary>
/// 示例插件：快捷启动。
///
/// 这是一个“没有任务栏图标也能工作”的插件示范 —— 它同时提供：
///   · IDropHandlerPlugin —— 把任意文件 / 文件夹拖到任务栏上就登记为快捷项；
///   · IContextMenuPlugin —— 往任务栏空白处、图标上、迷你窗口、插件管理器四处加菜单；
///   · IHotkeyPlugin      —— Ctrl+Alt+1..5 一键启动第 N 项；
///   · IPanelContentPlugin / ITaskButtonPlugin —— 面板里管理这些快捷项。
/// 说明插件能力是可以任意组合的，出现在什么位置完全由插件自己声明。
/// </summary>
[PluginManifest(
    "minibar.launch",
    "快捷启动",
    Description = "把文件/文件夹拖到任务栏即可登记；Ctrl+Alt+1~5 一键打开",
    Author = "MiniBar Samples",
    Version = "1.0.0",
    Icon = "emoji:🚀",
    Order = 30,
    DefaultPinned = false)]
public sealed class QuickLaunchPlugin : IMinibarPlugin, ITaskButtonPlugin, IPanelContentPlugin,
    IContextMenuPlugin, IDropHandlerPlugin, IHotkeyPlugin
{
    private const string SettingsKey = "Shortcuts";
    private const int MaxHotkeySlots = 5;

    private readonly List<string> _shortcuts = new();
    private IPluginContext _context = null!;
    private StackPanel? _listHost;

    public void Initialize(IPluginContext context)
    {
        _context = context;

        var stored = context.GetSetting(SettingsKey, new List<string>());
        if (stored is not null)
        {
            _shortcuts.AddRange(stored.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        context.Logger.Info($"快捷启动已就绪，共 {_shortcuts.Count} 项");
    }

    public void Dispose()
    {
        _listHost = null;
    }

    // ---------------------------------------------------------------- 任务栏

    public PluginIcon Icon => "emoji:🚀";

    public string DisplayName => "快捷启动";

    public string? Tooltip => _shortcuts.Count == 0
        ? "把文件或文件夹拖到任务栏上，就会登记到这里"
        : $"{_shortcuts.Count} 个快捷项";

    public string? Badge => _shortcuts.Count > 0 ? _shortcuts.Count.ToString() : null;

    public void OnClick(BarItemClickContext context)
    {
        // 左键单击 → 插件自己开关面板；其它键（中键/双击）由宿主按任务栏习惯处理。
        // 想启动快捷项，用面板里的按钮或 Ctrl+Alt+1~5 全局快捷键。
        if (context.Kind == BarItemActivationKind.Primary)
        {
            context.TogglePanel();
        }
    }

    // ---------------------------------------------------------------- 面板

    public string PanelTitle => "快捷启动";

    public double PreferredWidth => 420;

    public double PreferredHeight => 320;

    public FrameworkElement CreateContent(IPanelHost host)
    {
        _listHost = new StackPanel { Margin = new Thickness(2) };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _listHost,
            MinHeight = 160,
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        toolbar.Children.Add(MakeButton("添加文件…", () => PickPath(pickFolder: false)));
        toolbar.Children.Add(MakeButton("添加文件夹…", () => PickPath(pickFolder: true)));
        toolbar.Children.Add(MakeButton("清空全部", () =>
        {
            if (_shortcuts.Count == 0)
            {
                return;
            }

            if (MessageBox.Show($"确定清空全部 {_shortcuts.Count} 个快捷项吗？", "快捷启动",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }

            _shortcuts.Clear();
            Persist();
        }));

        var hint = new TextBlock
        {
            Text = "也可以直接把文件 / 文件夹拖到任务栏上（拖到某个图标上则只给那个插件处理）。",
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushFrom(_context.Theme.Muted),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(scroll, 0);
        Grid.SetRow(toolbar, 1);
        Grid.SetRow(hint, 2);

        grid.Children.Add(scroll);
        grid.Children.Add(toolbar);
        grid.Children.Add(hint);

        RebuildList();
        return grid;
    }

    public void ReleaseContent()
    {
        _listHost = null;

        // 面板关掉后主动收回一下临时控件，配合宿主的低内存取向
        GC.Collect(0, GCCollectionMode.Optimized);
    }

    private void RebuildList()
    {
        if (_listHost is null)
        {
            return;
        }

        _listHost.Children.Clear();

        if (_shortcuts.Count == 0)
        {
            _listHost.Children.Add(new TextBlock
            {
                Text = "还没有快捷项。把文件夹或文件拖到任务栏上试试。",
                FontSize = 12,
                Margin = new Thickness(6, 10, 6, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushFrom(_context.Theme.Muted),
            });
            return;
        }

        for (var i = 0; i < _shortcuts.Count; i++)
        {
            _listHost.Children.Add(BuildRow(i, _shortcuts[i]));
        }
    }

    private FrameworkElement BuildRow(int index, string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        var isFolder = Directory.Exists(path);

        var name = new TextBlock
        {
            Text = (isFolder ? "📁 " : "📄 ") + Path.GetFileName(path.TrimEnd('\\')) +
                   (index < MaxHotkeySlots ? $"   （Ctrl+Alt+{index + 1}）" : string.Empty),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = exists ? BrushFrom(_context.Theme.Foreground) : BrushFrom(_context.Theme.Danger),
        };

        var sub = new TextBlock
        {
            Text = path,
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BrushFrom(_context.Theme.Muted),
        };

        var open = new Button
        {
            Content = "打开",
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 4, 0),
            Background = Brushes.Transparent,
            Foreground = BrushFrom(_context.Theme.Accent),
            BorderBrush = BrushFrom(_context.Theme.Border),
            BorderThickness = new Thickness(1),
        };
        open.Click += (_, _) => Launch(path);

        var remove = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Thickness(7, 3, 7, 3),
            Background = Brushes.Transparent,
            Foreground = BrushFrom(_context.Theme.Muted),
            BorderBrush = Brushes.Transparent,
            ToolTip = "移除",
        };
        remove.Click += (_, _) =>
        {
            _shortcuts.Remove(path);
            Persist();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(open);
        buttons.Children.Add(remove);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(sub);

        var row = new Grid { Margin = new Thickness(4, 3, 4, 3), Tag = path };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(texts, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(texts);
        row.Children.Add(buttons);

        row.MouseLeftButtonUp += (_, _) => Launch(path);
        row.ToolTip = "单击立即打开";

        return row;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) { }

    // ---------------------------------------------------------------- 拖放

    public bool CanHandle(PluginDropContext context)
    {
        if (context.Paths.Count == 0)
        {
            return false;
        }

        // 单个 DLL 交给宿主内置的“拖入即加载插件”逻辑，别抢
        if (context.Paths.Count == 1 &&
            string.Equals(Path.GetExtension(context.Paths[0]), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return context.Paths.All(p => File.Exists(p) || Directory.Exists(p));
    }

    public void OnDrop(PluginDropContext context)
    {
        var added = 0;

        foreach (var path in context.Paths)
        {
            if (_shortcuts.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _shortcuts.Add(path);
            added++;
        }

        Persist();
        context.Handled = true;

        _context.Shell.Notify(
            added > 0 ? $"已登记 {added} 个快捷项，共 {_shortcuts.Count} 个" : "这些项目已经在列表里了",
            added > 0 ? NotificationKind.Success : NotificationKind.Info);
    }

    // ---------------------------------------------------------------- 菜单

    public IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context)
    {
        // 拖到图标上时，插件菜单里可以看到“用这个打开”（这里示范按目标类型区分）
        if (context.Target == PluginMenuTarget.BarItem && context.TargetPluginId == _context.PluginId)
        {
            yield return PluginMenuEntry.Command("打开快捷启动面板",
                () => _context.Shell.TogglePanel(_context.PluginId), "glyph:E8A7");
        }

        var recent = _shortcuts.Take(6).ToList();
        if (recent.Count > 0)
        {
            yield return PluginMenuEntry.Submenu("快捷启动（最近 6 项）",
                recent.Select((path, index) => PluginMenuEntry.Command(
                    $"{(index < MaxHotkeySlots ? $"{index + 1}. " : string.Empty)}{Path.GetFileName(path.TrimEnd('\\'))}",
                    () => Launch(path),
                    icon: (PluginIcon)(Directory.Exists(path) ? "emoji:📁" : "emoji:📄")))
                    .ToList(),
                "emoji:🚀");
        }

        if (context.DropPaths.Count > 0)
        {
            var paths = context.DropPaths.ToArray();
            yield return PluginMenuEntry.Command($"把 {paths.Length} 个项目登记为快捷启动",
                () =>
                {
                    foreach (var path in paths)
                    {
                        if (!_shortcuts.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                        {
                            _shortcuts.Add(path);
                        }
                    }

                    Persist();
                },
                "glyph:E710");
        }

        yield return PluginMenuEntry.Command("清空快捷启动列表", () =>
        {
            _shortcuts.Clear();
            Persist();
        }, "glyph:E74D", isEnabled: _shortcuts.Count > 0);
    }

    // ---------------------------------------------------------------- 快捷键

    public IEnumerable<PluginHotkey> GetHotkeys()
    {
        // 固定保留 5 个槽位；没有对应项时按下不会有任何事情发生
        for (var slot = 1; slot <= MaxHotkeySlots; slot++)
        {
            yield return new PluginHotkey(
                $"slot{slot}",
                $"Ctrl+Alt+{slot} 启动快捷启动第 {slot} 项",
                HotkeyModifiers.Control | HotkeyModifiers.Alt,
                Key.D0 + slot);
        }
    }

    public void OnHotkey(string hotkeyId)
    {
        if (!hotkeyId.StartsWith("slot", StringComparison.Ordinal) ||
            !int.TryParse(hotkeyId.AsSpan(4), out var slot))
        {
            return;
        }

        var index = slot - 1;
        if (index < 0 || index >= _shortcuts.Count)
        {
            _context.Shell.Notify($"第 {slot} 个快捷项还不存在", NotificationKind.Warning);
            return;
        }

        Launch(_shortcuts[index]);
    }

    public void OnHotkeyRegistrationFailed(string hotkeyId, string reason)
    {
        // 快捷键被占用是常态（很多人会用 Ctrl+Alt+数字），只提示一次级别的信息
        _context.Logger.Warn($"快捷键 {hotkeyId} 注册失败：{reason}");
    }

    // ---------------------------------------------------------------- 内部

    private void Persist()
    {
        _context.SetSetting(SettingsKey, _shortcuts);
        _context.InvalidateBarItem();
        RebuildList();
    }

    private void PickPath(bool pickFolder)
    {
        // 用 .NET 自带的 OpenFileDialog / 文件夹选择走 shell，这里简单起见只做文件选择，
        // 文件夹选择提示用户直接拖拽 —— 保持插件零依赖。
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = pickFolder ? "选择一个文件夹里的文件（或用拖拽添加文件夹）" : "选择要登记的文件",
            CheckFileExists = !pickFolder,
            Multiselect = true,
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            if (!_shortcuts.Any(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)))
            {
                _shortcuts.Add(file);
            }
        }

        Persist();
    }

    private void Launch(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                _context.Shell.Notify($"找不到：{path}", NotificationKind.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _context.Logger.Info($"已启动：{path}");
        }
        catch (Exception ex)
        {
            _context.Logger.Error($"启动失败：{path}", ex);
            _context.Shell.Notify($"启动失败：{ex.Message}", NotificationKind.Error);
        }
    }

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
