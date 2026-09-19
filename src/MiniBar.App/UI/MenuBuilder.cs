using System.Collections.Generic;
using System.Windows.Controls;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 把插件提供的 <see cref="PluginMenuEntry"/> 转成 WPF 菜单项。
/// 宿主自己的菜单项也用同一套构造器，保证外观一致。
/// </summary>
public static class MenuBuilder
{
    private static DataTemplate? _iconTemplate;

    private static DataTemplate? IconTemplate => _iconTemplate ??=
        Application.Current?.TryFindResource("PluginIconTemplate") as DataTemplate;

    /// <summary>宿主自建菜单项：文字 + 可选字形图标 + 可选快捷键提示。</summary>
    public static MenuItem Item(string header, Action? action, string? glyph = null, string? hint = null,
        bool isEnabled = true, bool isChecked = false, bool isCheckable = false)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled,
            Tag = hint,
            Style = ResolveMenuStyle(),
        };

        if (!string.IsNullOrEmpty(glyph))
        {
            item.Icon = BuildIcon(PluginIcon.Parse(glyph));
        }

        if (isCheckable)
        {
            item.IsCheckable = true;
            item.IsChecked = isChecked;
        }
        else if (isChecked)
        {
            item.IsChecked = true;
        }

        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }

    public static Separator Sep() => new();

    /// <summary>转换插件菜单项（包含子菜单与分隔线），自动合并连续分隔线。</summary>
    public static IEnumerable<object> Convert(IEnumerable<PluginMenuEntry> entries, Action<PluginMenuEntry> invoke)
    {
        var result = new List<object>();
        var lastWasSeparator = true; // 开头的分隔线丢弃

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (entry.IsSeparator)
            {
                if (!lastWasSeparator)
                {
                    result.Add(Sep());
                    lastWasSeparator = true;
                }

                continue;
            }

            result.Add(ConvertOne(entry, invoke));
            lastWasSeparator = false;
        }

        if (lastWasSeparator && result.Count > 0)
        {
            result.RemoveAt(result.Count - 1); // 结尾的分隔线丢弃
        }

        return result;
    }

    private static MenuItem ConvertOne(PluginMenuEntry entry, Action<PluginMenuEntry> invoke)
    {
        var item = new MenuItem
        {
            Header = entry.Header,
            IsEnabled = entry.IsEnabled,
            ToolTip = entry.ToolTip,
            Style = ResolveMenuStyle(),
        };

        if (entry.Icon is not null)
        {
            item.Icon = BuildIcon(entry.Icon);
        }

        if (entry.IsChecked)
        {
            item.IsChecked = true;
            item.IsCheckable = true;
        }

        if (entry.Children is { Count: > 0 })
        {
            foreach (var child in Convert(entry.Children, invoke))
            {
                item.Items.Add(child);
            }
        }
        else if (entry.Invoke is not null)
        {
            item.Click += (_, _) => invoke(entry);
        }

        return item;
    }

    /// <summary>图标统一交给 PluginIconTemplate 渲染，字符串/图片两种形态都不用额外分支。</summary>
    private static FrameworkElement BuildIcon(PluginIcon icon) => new ContentControl
    {
        Content = icon,
        ContentTemplate = IconTemplate,
        Focusable = false,
    };

    private static Style? ResolveMenuStyle() =>
        Application.Current?.TryFindResource("MiniBarMenuItemStyle") as Style;
}
