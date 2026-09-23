using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.Sdk;

namespace MiniBar.App.Services;

/// <summary>
/// 宿主自己的设置，也实现成 <see cref="PluginSettingsSection"/> 列表 ——
/// 于是设置窗口只需要一套渲染逻辑，"宿主设置"和"插件设置"长得一模一样。
/// </summary>
public sealed class HostSettingsProvider
{
    private readonly SettingsService _settings;

    public HostSettingsProvider(SettingsService settings) => _settings = settings;

    private AppSettings S => _settings.Settings;

    /// <summary>写盘 + 立即应用（能力开关类的改动走 applyLayout=false，避免反复重排窗口）。</summary>
    private void Commit(bool applyLayout = true)
    {
        _settings.NotifyChanged();

        if (applyLayout)
        {
            AppServices.Shell?.ApplySettings();
        }
    }

    private static readonly string[] EdgeNames = { "底部", "顶部", "左侧", "右侧" };

    private static readonly string[] AnchorNames =
    {
        "左上", "顶部居中", "右上", "左下", "底部居中", "右下",
    };

    public IEnumerable<PluginSettingsSection> Build()
    {
        yield return new PluginSettingsSection
        {
            Title = "外观",
            Description = "任务栏自身的显示方式",
            Icon = "glyph:E790",
            Items = new[]
            {
                Item.Choice(new[] { "浅色", "深色", "跟随系统" }, ThemeLabel(S.Theme), value =>
                {
                    S.Theme = value switch
                    {
                        "深色" => "Dark",
                        "跟随系统" => "System",
                        _ => "Light",
                    };
                    Commit(false);
                    AppServices.Theme.Apply(S.Theme);
                    AppServices.Shell?.OnThemeChanged();
                }, "主题", "宿主与所有插件界面会一起切换"),

                Item.Number(S.BarOpacity, 0.3, 1.0, 0.01, value =>
                {
                    S.BarOpacity = value;
                    Commit(false);

                    // 拖动滑块时直接改窗口透明度，避免每一帧都去问系统重排 AppBar
                    if (AppServices.Shell is { IsMiniMode: false, Bar: { } bar })
                    {
                        bar.Opacity = value;
                    }
                }, "不透明度", suffix: null, description: "0.3 全透 ~ 1.0 完全不透明"),

                Item.Toggle(S.ShowItemLabels, value =>
                {
                    S.ShowItemLabels = value;
                    Commit();
                }, "显示插件文字标签", "在图标旁显示插件名称"),
            },
        };

        yield return new PluginSettingsSection
        {
            Title = "位置与占用空间",
            Description = "AppBar 是 Shell 提供的机制：注册后系统会把屏幕工作区让出一条，其它窗口最大化时会自动避开",
            Icon = "glyph:E7C4",
            Items = new[]
            {
                Item.Toggle(S.UseAppBar, value =>
                {
                    S.UseAppBar = value;
                    Commit();
                }, "像任务栏一样占用屏幕空间（AppBar）",
                    "关闭后变回悬浮胶囊：仍然置顶，但不占工作区"),

                Item.Number(S.AppBarThickness, 30, 160, 2, value =>
                {
                    S.AppBarThickness = value;
                    Commit();
                }, "任务栏厚度", "px", "仅在占用屏幕空间时生效"),

                Item.Choice(EdgeNames, EdgeNames[(int)S.Edge], value =>
                {
                    if (Enum.TryParse<DockEdge>(NameOfEdge(Array.IndexOf(EdgeNames, value)), out var edge))
                    {
                        S.Edge = edge;
                        Commit();
                    }
                }, "停靠边缘"),

                Item.Number(S.Margin, 0, 40, 1, value =>
                {
                    S.Margin = value;
                    Commit();
                }, "距屏幕边缘留白", "px", "悬浮模式下的浮动间距"),

                Item.Choice(
                    MonitorNames(),
                    MonitorNames()[MonitorPosition()],
                    value =>
                    {
                        var index = Array.IndexOf(MonitorNames(), value);
                        if (index >= 0)
                        {
                            S.MonitorIndex = index;
                            Commit();
                        }
                    },
                    "目标显示器"),
            },
        };

        yield return new PluginSettingsSection
        {
            Title = "全屏与迷你模式",
            Description = "有程序全屏（游戏/视频/演示）时让出屏幕，只留一个小浮层显示指定插件的内容",
            Icon = "glyph:E7F4",
            Items = new[]
            {
                Item.Toggle(S.AutoEnterMiniMode, value =>
                {
                    S.AutoEnterMiniMode = value;
                    Commit();
                }, "检测到全屏时自动进入迷你模式"),

                Item.Toggle(S.HideBarWhenFullscreen, value =>
                {
                    S.HideBarWhenFullscreen = value;
                    Commit();
                }, "全屏时彻底隐藏任务栏", "关闭后全屏时也会保留任务栏（会盖在全屏程序上）"),

                Item.Choice(MiniModeChoices(), CurrentMiniChoice(), value =>
                {
                    S.MiniModePluginId = value == AutoMiniChoice ? null : MiniChoiceToId(value);
                    Commit();
                }, "迷你模式显示内容"),

                Item.Choice(AnchorNames, AnchorNames[(int)S.MiniWindowAnchor], value =>
                {
                    if (Enum.TryParse<AnchorCorner>(AnchorNameOf(value), out var corner))
                    {
                        S.MiniWindowAnchor = corner;
                        Commit();
                    }
                }, "迷你窗口位置"),

                Item.Toggle(S.MiniWindowAcceptsFocus, value =>
                {
                    S.MiniWindowAcceptsFocus = value;
                    Commit();
                }, "迷你窗口接受焦点", "默认不接受：点它不会把全屏程序切出去"),

                Item.Number(S.FullscreenPollMs, 200, 5000, 100, value =>
                {
                    S.FullscreenPollMs = (int)value;
                    Commit(false);
                }, "全屏检测间隔", "毫秒", "AppBar 收到系统通知时不用等这个间隔"),

                Item.Toggle(S.TreatQuietHoursAsFullscreen, value =>
                {
                    S.TreatQuietHoursAsFullscreen = value;
                    Commit(false);
                    if (AppServices.Fullscreen is { } watcher)
                    {
                        watcher.TreatQuietTimeAsFullscreen = value;
                    }
                }, "把「专注助手 / 演示模式」也视为全屏"),
            },
        };

        yield return new PluginSettingsSection
        {
            Title = "交互",
            Icon = "glyph:E8B8",
            Items = new[]
            {
                Item.Toggle(S.CloseFlyoutOnDeactivate, value =>
                {
                    S.CloseFlyoutOnDeactivate = value;
                    Commit(false);
                }, "插件界面失去焦点时自动关闭"),

                Item.Toggle(S.AutoLoadPluginOnDllDrop, value =>
                {
                    S.AutoLoadPluginOnDllDrop = value;
                    Commit(false);
                }, "把 DLL 拖进来即加载"),

                Item.Toggle(S.WatchPluginFolders, value =>
                {
                    S.WatchPluginFolders = value;
                    Commit(false);
                }, "监视插件目录", "新增/删除/覆盖 DLL 后自动热加载或卸载（需要重启生效）"),

                Item.Toggle(S.ConfirmBeforeUninstall, value =>
                {
                    S.ConfirmBeforeUninstall = value;
                    Commit(false);
                }, "卸载并删除插件前确认"),
            },
        };

        yield return new PluginSettingsSection
        {
            Title = "内存与诊断",
            Icon = "glyph:E9D2",
            Items = new[]
            {
                Item.Toggle(S.TrimWorkingSetOnIdle, value =>
                {
                    S.TrimWorkingSetOnIdle = value;
                    Commit(false);
                }, "空闲时把内存还给系统", "关闭面板/卸载插件后也会立刻归还"),

                Item.Number(S.IdleTrimSeconds, 5, 600, 5, value =>
                {
                    S.IdleTrimSeconds = (int)value;
                    Commit(false);
                }, "空闲判定阈值", "秒"),

                Item.Toggle(S.ReassertTopmost, value =>
                {
                    S.ReassertTopmost = value;
                    Commit();
                }, "周期性重申置顶"),

                Item.Number(S.TopmostGuardIntervalMs, 1000, 30000, 500, value =>
                {
                    S.TopmostGuardIntervalMs = (int)value;
                    Commit(false);
                }, "重申间隔", "毫秒"),

                Item.Toggle(S.VerboseLogging, value =>
                {
                    S.VerboseLogging = value;
                    AppLog.Verbose = value;
                    Commit(false);
                }, "详细日志（Debug 级）"),

                Item.Button("立即归还内存", () =>
                {
                    AppServices.TrimMemory(force: true);
                    AppServices.Shell?.Notify("已把未使用的内存页还给系统", NotificationKind.Success);
                }, "把本进程的工作集清空，任务管理器里的读数会立刻下降"),

                Item.Button("打开日志文件", () => OpenPath(AppPaths.LogFile)),
                Item.Button("打开配置目录", () => OpenPath(AppPaths.ConfigDirectory)),
                Item.Button("打开用户插件目录", () => OpenPath(AppPaths.UserPluginDirectory)),
            },
        };

        var plugins = AppServices.Plugins;

        yield return new PluginSettingsSection
        {
            Title = "关于",
            Icon = "glyph:E946",
            Items = new[]
            {
                Item.Info("MiniBar", "可扩展任务栏空壳 · .NET 8 + WPF"),
                Item.Info($"当前插件：{plugins?.Plugins.Count ?? 0} 个 " +
                          $"（已加载 {plugins?.Plugins.Count(p => p.IsLoaded) ?? 0} 个，" +
                          $"固定 {plugins?.PinnedPlugins.Count() ?? 0} 个）"),
                Item.Info($"程序目录：{AppPaths.BaseDirectory}"),
                Item.Button("查看插件管理器", () => AppServices.Shell?.ShowPluginManager(),
                    "加载 / 禁用 / 重新加载 / 卸载并删除插件", "打开插件管理器"),
            },
        };
    }

    // ---------------------------------------------------------------- 迷你模式选项

    private const string AutoMiniChoice = "自动选择（第一个提供紧凑内容的插件）";

    private string[] MiniModeChoices()
    {
        var list = new List<string> { AutoMiniChoice };
        if (AppServices.Plugins is { } host)
        {
            list.AddRange(host.Plugins
                .Where(p => p.HasCompact && !p.IsDuplicate)
                .Select(DisplayOf));
        }

        return list.ToArray();
    }

    private string CurrentMiniChoice()
    {
        var configured = S.MiniModePluginId;
        if (string.IsNullOrEmpty(configured) || AppServices.Plugins?.Find(configured) is not { } descriptor)
        {
            return AutoMiniChoice;
        }

        return DisplayOf(descriptor);
    }

    private static string DisplayOf(PluginDescriptor descriptor) => $"{descriptor.DisplayName}（{descriptor.Id}）";

    private static string? MiniChoiceToId(string choice)
    {
        var start = choice.LastIndexOf('（');
        var end = choice.LastIndexOf('）');
        return start >= 0 && end > start ? choice[(start + 1)..end] : null;
    }

    // ---------------------------------------------------------------- 小工具

    private string[] MonitorNames()
    {
        var displays = DisplayService.GetDisplays();
        return displays
            .Select((d, i) => $"显示器 {i + 1}{(d.IsPrimary ? "（主）" : string.Empty)}  {d.Bounds.Width:0}×{d.Bounds.Height:0}")
            .ToArray();
    }

    private int MonitorPosition()
    {
        var displays = DisplayService.GetDisplays();

        if (S.MonitorIndex >= 0 && S.MonitorIndex < displays.Count)
        {
            return S.MonitorIndex;
        }

        for (var i = 0; i < displays.Count; i++)
        {
            if (displays[i].IsPrimary)
            {
                return i;
            }
        }

        return 0;
    }

    private static string NameOfEdge(int index) => index switch
    {
        1 => "Top",
        2 => "Left",
        3 => "Right",
        _ => "Bottom",
    };

    /// <summary>设置里存的是 Light/Dark/System，界面显示中文 —— 下拉框当前值必须用显示名。</summary>
    private static string ThemeLabel(string theme) => theme switch
    {
        "Dark" => "深色",
        "System" => "跟随系统",
        _ => "浅色",
    };

    private static string AnchorNameOf(string name) => name switch
    {
        "左上" => "TopLeft",
        "顶部居中" => "TopCenter",
        "右上" => "TopRight",
        "左下" => "BottomLeft",
        "底部居中" => "BottomCenter",
        _ => "BottomRight",
    };

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开路径失败：{path}", ex);
        }
    }

    /// <summary>设置项工厂的薄封装，纯粹为了让上面的声明读起来短一点。</summary>
    private static class Item
    {
        public static PluginSettingItem Toggle(bool value, Action<bool> setter, string label, string? description = null) => new()
        {
            Label = label,
            Kind = PluginSettingKind.Bool,
            Description = description,
            GetValue = () => value,
            SetValue = v => setter(v is bool b && b),
        };

        public static PluginSettingItem Number(double value, double min, double max, double step,
            Action<double> setter, string label, string? suffix = null, string? description = null) => new()
        {
            Label = label,
            Kind = PluginSettingKind.Number,
            Description = description,
            Suffix = suffix,
            Minimum = min,
            Maximum = max,
            Step = step,
            GetValue = () => value,
            SetValue = v => setter(v is double d ? d : value),
        };

        public static PluginSettingItem Choice(string[] choices, string current, Action<string> setter,
            string label, string? description = null) => new()
        {
            Label = label,
            Kind = PluginSettingKind.Choice,
            Description = description,
            Choices = choices,
            GetValue = () => current,
            SetValue = v => setter(v as string ?? current),
        };

        public static PluginSettingItem Button(string label, Action invoke, string? description = null,
            string? actionLabel = null) => new()
        {
            Label = label,
            Kind = PluginSettingKind.Action,
            Description = description,
            ActionLabel = actionLabel ?? label,
            Invoke = invoke,
        };

        public static PluginSettingItem Info(string label, string? description = null) => new()
        {
            Label = label,
            Kind = PluginSettingKind.Info,
            Description = description,
        };
    }
}
