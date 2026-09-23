using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 主菜单的"拼装车间"。
///
/// <para><b>为什么单独抽一个类</b>：这个菜单有三个地方要弹 ——
/// 任务栏右键、任务栏把手（左键）、<b>系统托盘图标右键</b>。如果各处各写一份，
/// 改一个入口就要改三处（很容易漏）。所以统一在这里 Build 一次，谁需要谁调用。</para>
///
/// <para><b>菜单的组织原则（用户反馈"菜单太乱"之后重排过）</b>：</para>
/// <list type="number">
///   <item>最上面只放两个最常用的入口：打开设置、插件管理；</item>
///   <item>中间按主题分组成「子菜单」（迷你模式 / 界面 / 插件 / 打开…），
///         避免一次性铺开二三十项；</item>
///   <item><b>每个插件的菜单项收进「插件」子菜单里、按插件再分一层</b> ——
///         以前是把所有插件的项平铺在主菜单里，插件一多就完全没法看；</item>
///   <item>「退出 MiniBar」固定在最后一项，并且因为菜单整体变短了，
///         它不会再被挤出屏幕外（用户之前反馈"找不到退出入口"就是这个原因）。</item>
/// </list>
/// </summary>
internal static class MainMenuBuilder
{
    /// <summary>
    /// 构建主菜单。
    /// </summary>
    /// <param name="plugins">插件宿主（要读插件列表与状态）。</param>
    /// <param name="settings">配置服务（菜单里的勾选项要写回配置）。</param>
    /// <param name="shell">壳服务（开面板、迷你模式、退出等能力）。</param>
    /// <param name="bar">任务栏窗口；为 null 时（例如早期启动阶段）不显示"显示/隐藏任务栏"这类项。</param>
    public static ContextMenu Build(PluginHost plugins, SettingsService settings, ShellService shell, BarWindow? bar)
    {
        var app = settings.Settings;
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("MiniBarContextMenuStyle") };

        // ---------------------------------------------------------------- 1. 最常用
        menu.Items.Add(MenuBuilder.Item("打开设置…", () => shell.ShowSettings(), "glyph:E713", hint: "Ctrl+Alt+,"));
        menu.Items.Add(MenuBuilder.Item("插件管理…", () => shell.ShowPluginManager(), "glyph:E8FD", hint: "Ctrl+Alt+P"));

        // ---------------------------------------------------------------- 2. 迷你模式
        menu.Items.Add(MenuBuilder.Sep());
        menu.Items.Add(BuildMiniModeMenu(plugins, settings, shell));

        // ---------------------------------------------------------------- 3. 界面与行为
        menu.Items.Add(BuildAppearanceMenu(settings, shell, bar));

        // ---------------------------------------------------------------- 4. 插件（按插件分组）
        var pluginMenu = BuildPluginMenu(plugins, shell);
        if (pluginMenu is not null)
        {
            menu.Items.Add(MenuBuilder.Sep());
            menu.Items.Add(pluginMenu);
        }

        // ---------------------------------------------------------------- 5. 打开目录 / 退出
        menu.Items.Add(MenuBuilder.Sep());

        var openMenu = new MenuItem
        {
            Header = "打开…",
            Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
            Icon = MenuBuilder.Icon("glyph:E740"),
        };
        openMenu.Items.Add(MenuBuilder.Item("用户插件目录", () => OpenPath(AppPaths.UserPluginDirectory), "glyph:E838"));
        openMenu.Items.Add(MenuBuilder.Item("配置目录", () => OpenPath(AppPaths.ConfigDirectory), "glyph:E8A5"));
        openMenu.Items.Add(MenuBuilder.Item("日志文件", () => OpenPath(AppPaths.LogFile), "glyph:E9D9"));
        menu.Items.Add(openMenu);

        menu.Items.Add(MenuBuilder.Sep());
        menu.Items.Add(MenuBuilder.Item("退出 MiniBar", () => Application.Current.Shutdown(), "glyph:E7E8",
            hint: "也可以右键右下角的托盘图标"));

        return menu;
    }

    /// <summary>迷你模式：开关 + 选择"全屏时显示哪个插件的内容" + 两个自动行为。</summary>
    private static MenuItem BuildMiniModeMenu(PluginHost plugins, SettingsService settings, ShellService shell)
    {
        var root = new MenuItem
        {
            Header = "迷你模式",
            Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
            Icon = MenuBuilder.Icon("glyph:E790"),
        };

        var compactPlugins = plugins.Plugins
            .Where(p => p.IsEnabled && p.IsLoaded && p.HasCompact)
            .ToList();

        root.Items.Add(MenuBuilder.Item(
            shell.IsMiniMode ? "退出迷你模式" : "进入迷你模式",
            () =>
            {
                if (shell.IsMiniMode)
                {
                    shell.ExitMiniMode();
                }
                else
                {
                    shell.EnterMiniMode(string.Empty);
                }
            },
            "glyph:E9A7",
            isEnabled: compactPlugins.Count > 0 || shell.IsMiniMode,
            isChecked: shell.IsMiniMode,
            isCheckable: true));

        if (compactPlugins.Count > 0)
        {
            var source = new MenuItem
            {
                Header = "显示内容",
                Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
            };

            var current = shell.ResolveMiniPlugin();
            foreach (var descriptor in compactPlugins)
            {
                var captured = descriptor;
                source.Items.Add(MenuBuilder.Item(
                    descriptor.DisplayName,
                    () => shell.SetMiniModePlugin(captured.Id),
                    isChecked: ReferenceEquals(current, descriptor),
                    isCheckable: true));
            }

            root.Items.Add(source);
        }

        root.Items.Add(MenuBuilder.Sep());
        root.Items.Add(MenuBuilder.Item("检测到全屏时自动进入",
            () =>
            {
                settings.Settings.AutoEnterMiniMode = !settings.Settings.AutoEnterMiniMode;
                settings.NotifyChanged();
                shell.ApplySettings();
            },
            isChecked: settings.Settings.AutoEnterMiniMode,
            isCheckable: true));

        root.Items.Add(MenuBuilder.Item("全屏时彻底隐藏任务栏",
            () =>
            {
                settings.Settings.HideBarWhenFullscreen = !settings.Settings.HideBarWhenFullscreen;
                settings.NotifyChanged();
            },
            isChecked: settings.Settings.HideBarWhenFullscreen,
            isCheckable: true));

        return root;
    }

    /// <summary>界面与行为：停靠边、主题、文字标签、系统任务栏自动隐藏、系统托盘图标、释放内存。</summary>
    private static MenuItem BuildAppearanceMenu(SettingsService settings, ShellService shell, BarWindow? bar)
    {
        var app = settings.Settings;

        var root = new MenuItem
        {
            Header = "界面与行为",
            Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
            Icon = MenuBuilder.Icon("glyph:E8FD"),
        };

        // 停靠边
        var edgeMenu = new MenuItem
        {
            Header = "任务栏位置",
            Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
        };

        foreach (var (edge, label, glyph) in new[]
                 {
                     (DockEdge.Bottom, "底部", "glyph:E74B"),
                     (DockEdge.Top, "顶部", "glyph:E74A"),
                     (DockEdge.Left, "左侧", "glyph:E76B"),
                     (DockEdge.Right, "右侧", "glyph:E76C"),
                 })
        {
            var captured = edge;
            edgeMenu.Items.Add(MenuBuilder.Item(label,
                () =>
                {
                    app.Edge = captured;
                    settings.NotifyChanged();
                    shell.ApplySettings();
                },
                glyph, isChecked: app.Edge == edge, isCheckable: true));
        }

        root.Items.Add(edgeMenu);

        // 主题
        var themeMenu = new MenuItem
        {
            Header = "主题",
            Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
        };

        foreach (var (name, label) in new[] { ("Light", "浅色"), ("Dark", "深色"), ("System", "跟随系统") })
        {
            var captured = name;
            themeMenu.Items.Add(MenuBuilder.Item(label,
                () =>
                {
                    app.Theme = captured;
                    settings.NotifyChanged();
                    AppServices.Theme.Apply(captured);
                    shell.OnThemeChanged();
                },
                isChecked: string.Equals(app.Theme, name, StringComparison.OrdinalIgnoreCase),
                isCheckable: true));
        }

        root.Items.Add(themeMenu);

        root.Items.Add(MenuBuilder.Item("显示插件文字标签",
            () =>
            {
                app.ShowItemLabels = !app.ShowItemLabels;
                settings.NotifyChanged();
                shell.ApplySettings();
            },
            isChecked: app.ShowItemLabels, isCheckable: true));

        root.Items.Add(MenuBuilder.Sep());
        root.Items.Add(MenuBuilder.Item("显示/隐藏任务栏",
            () => bar?.SetBarVisible(!(bar?.IsVisible ?? false)),
            "glyph:E7B3",
            isEnabled: bar is not null));

        root.Items.Add(MenuBuilder.Item("自动隐藏系统任务栏",
            () =>
            {
                app.AutoHideSystemTaskbar = !app.AutoHideSystemTaskbar;
                settings.NotifyChanged();
                App.ApplySystemTaskbarSetting();
            },
            "glyph:E7B3",
            hint: "等于替你勾上「任务栏设置 → 自动隐藏任务栏」",
            isChecked: app.AutoHideSystemTaskbar, isCheckable: true));

        root.Items.Add(MenuBuilder.Item("显示系统托盘图标",
            () =>
            {
                app.EnableTrayIcon = !app.EnableTrayIcon;
                settings.NotifyChanged();
                shell.ApplyTrayIconSetting();
            },
            "glyph:E8FD",
            hint: "托盘图标：左键显示/隐藏任务栏，右键菜单，双击打开设置",
            isChecked: app.EnableTrayIcon, isCheckable: true));

        root.Items.Add(MenuBuilder.Sep());
        root.Items.Add(MenuBuilder.Item("空闲时释放内存",
            () =>
            {
                app.TrimWorkingSetOnIdle = !app.TrimWorkingSetOnIdle;
                settings.NotifyChanged();
            },
            hint: "把未使用内存还给系统",
            isChecked: app.TrimWorkingSetOnIdle, isCheckable: true));

        return root;
    }

    /// <summary>
    /// 把各插件的菜单项收进「插件」子菜单，每个插件再占一层。
    /// <para>
    /// 每个插件的子菜单 = 插件自己的菜单项 + 一条分隔线 + 宿主提供的通用操作
    /// （打开界面 / 插件设置 / 启用禁用 / 重新加载）。
    /// 这样即使装了十几个插件，主菜单也只有固定几项。
    /// </para>
    /// </summary>
    private static MenuItem? BuildPluginMenu(PluginHost plugins, ShellService shell)
    {
        var root = new MenuItem
        {
            Header = "插件",
            Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
            Icon = MenuBuilder.Icon("glyph:E838"),
        };

        var any = false;

        foreach (var descriptor in plugins.Plugins.Where(p => !p.IsDuplicate).OrderBy(p => p.Order))
        {
            var captured = descriptor;
            var entries = shell.CollectMenuEntries(PluginMenuTarget.BarBackground, descriptor.Id);
            var children = new List<object>();

            if (entries.Count > 0)
            {
                children.AddRange(MenuBuilder.Convert(entries, shell.InvokeMenuEntry));
                children.Add(MenuBuilder.Sep());
            }

            if (descriptor.HasPanel)
            {
                children.Add(MenuBuilder.Item("打开 / 关闭界面",
                    () => shell.TogglePanel(captured.Id), "glyph:E8A7"));
            }

            children.Add(MenuBuilder.Item("插件设置…",
                () => shell.ShowSettings(captured.Id), "glyph:E713"));

            children.Add(MenuBuilder.Item("重新加载",
                () => plugins.Reload(captured), "glyph:E72C", isEnabled: descriptor.IsEnabled));

            children.Add(MenuBuilder.Sep());
            children.Add(MenuBuilder.Item(descriptor.IsEnabled ? "禁用（保留文件与配置）" : "启用",
                () => plugins.SetEnabled(captured, !captured.IsEnabled), "glyph:E769"));

            var submenu = new MenuItem
            {
                Header = descriptor.DisplayName,
                Style = (Style)Application.Current.FindResource("MiniBarMenuItemStyle"),
                Icon = new ContentControl
                {
                    Content = descriptor.Icon,
                    ContentTemplate = (DataTemplate)Application.Current.FindResource("PluginIconTemplate"),
                },
            };

            foreach (var child in children)
            {
                submenu.Items.Add(child);
            }

            root.Items.Add(submenu);
            any = true;
        }

        if (!any)
        {
            root.Items.Add(MenuBuilder.Item("（还没有安装插件）", null, isEnabled: false));
        }
        else
        {
            root.Items.Add(MenuBuilder.Sep());
            root.Items.Add(MenuBuilder.Item("全部重新加载", () =>
            {
                foreach (var descriptor in plugins.Plugins.Where(p => p.IsEnabled && !p.IsDuplicate).ToArray())
                {
                    plugins.Reload(descriptor);
                }
            }, "glyph:E72C"));
        }

        root.Items.Add(MenuBuilder.Item("打开插件管理…", () => shell.ShowPluginManager(), "glyph:E8FD"));
        return root;
    }

    /// <summary>用资源管理器打开目录或定位文件。</summary>
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
}
