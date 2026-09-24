using System.Windows;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

/// <summary>
/// 把插件提供的 <see cref="PluginMenuEntry"/> 转成 WPF 菜单项。
/// 宿主自己的菜单项也用同一套构造器，保证外观一致。
/// </summary>
public static class MenuBuilder
{
    private static DataTemplate? _iconTemplate;

    // DataTemplate：描述“某类数据该怎么画成 UI”的模板。这里 PluginIconTemplate 告诉 WPF 怎么把 PluginIcon 画成图标。
    // 用延迟加载(??=)：第一次访问才去资源里找，找不到就缓存 null（之后不再找）。
    private static DataTemplate? IconTemplate => _iconTemplate ??=
        Application.Current?.TryFindResource("PluginIconTemplate") as DataTemplate;

    /// <summary>
    /// 宿主自己用的菜单项构造器：文字 + 可选字形图标(glyph) + 可选快捷键提示(hint，存在 Tag 上)。
    /// Action 是“点击要执行的事”（.NET 里的无返回值委托）；传 null 表示只是个展示项/由别处处理。
    /// isCheckable/isChecked 控制是否显示勾选态。统一套 MiniBarMenuItemStyle 保证外观一致。
    /// </summary>
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

    /// <summary>生成一条分隔线(Separator)。菜单项之间用来分组。</summary>
    public static Separator Sep() => new();

    /// <summary>单独造一个菜单图标（给子菜单标题用），避免"造一个假菜单项再偷它图标"的写法。</summary>
    public static FrameworkElement Icon(string glyph) => BuildIcon(PluginIcon.Parse(glyph));

    /// <summary>
    /// 把菜单弹到<b>鼠标当前位置</b>。
    ///
    /// <para><b>为什么不用 PlacementMode.Bottom + PlacementTarget：</b>那会把菜单钉在窗口的某条边
    /// （例如全宽 AppBar 窗口的正下方、或贴住它的下边），而用户右键是在"某个位置"点的，
    /// 菜单却跑到固定位置 —— 体验很奇怪。这里改用"绝对屏幕坐标"直接落在光标处。</para>
    ///
    /// <para>GetCursorPositionPixels 返回物理像素，而 WPF 的偏移量单位是 DIP，
    /// 所以要除以缩放比（125% 缩放下不改会越偏越远）。</para>
    /// </summary>
    public static void ShowAtCursor(ContextMenu menu, double dpiScale)
    {
        // 坐标单位（做过标定实验：偏移传 (100,100) 时菜单左上角落在物理像素 (135,145)）：
        // WPF Popup 在 AbsolutePoint 模式下的偏移以【DIP】为单位，WPF 自己会乘以 DPI，
        // 所以要把 GetCursorPositionPixels 的物理像素【除以缩放比】传进去。
        var cursor = DisplayService.GetCursorPositionPixels();
        var scale = dpiScale > 0 ? dpiScale : 1.0;

        // 量一下菜单自身宽度（还没打开也能量：Measure 会先算一遍理想尺寸）
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var menuWidthDip = menu.DesiredSize.Width;

        var x = cursor.X / scale;
        var y = cursor.Y / scale;

        // 系统「菜单右对齐」补偿（就是这一条让菜单"右键没反应/跑到屏幕外"）：
        // Windows 的 SystemParameters.MenuDropAlignment 为 true 时（平板/左手模式的"菜单右对齐"设置），
        // WPF 会把菜单【右缘】贴到我们给的点上 —— 鼠标在屏幕左半边时，菜单整体被甩到屏幕左侧外面。
        // 补偿：x 再加一个菜单宽度，等价于让【左缘】落在鼠标处。鼠标靠右边缘时 WPF 会自己翻转，不会出屏。
        if (SystemParameters.MenuDropAlignment)
        {
            x += menuWidthDip;
        }

        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = x;
        menu.VerticalOffset = y;

        ElevateToTopmost(menu);

        menu.IsOpen = true;
    }

    /// <summary>
    /// 让菜单的浮层窗口也"置顶"。
    ///
    /// <para><b>为什么需要：</b>任务栏窗口自己是 <c>TOPMOST</c>（常驻最前），而 WPF 为菜单创建的
    /// Popup 窗口默认<b>不是</b> TOPMOST。窗口层级里 TOPMOST 的一方永远压住非 TOPMOST 的一方，
    /// 于是菜单紧贴任务栏弹出时，任务栏会盖在菜单上（实测：任务栏停右侧时，右键菜单的右半截
    /// 直接被任务栏吃掉）。菜单的 Popup 是独立顶层窗口，用 <c>SetWindowPos</c> 把它也设成
    /// TOPMOST 就解决了 —— 这就是"明明用了 ContextMenu 还是被遮住"的原因。</para>
    ///
    /// <para>必须在 <c>Opened</c> 之后才拿得到 <see cref="HwndSource"/>（打开前菜单还不在视觉树里），
    /// 所以挂事件而不是打开前就设。</para>
    /// </summary>
    private static void ElevateToTopmost(ContextMenu menu)
    {
        menu.Opened += (_, _) =>
        {
            try
            {
                if (PresentationSource.FromVisual(menu) is HwndSource source && source.Handle != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(
                        source.Handle,
                        NativeMethods.HWND_TOPMOST,
                        0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

                    AppLog.Debug("菜单浮层已置顶");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("把菜单浮层置顶失败", ex);
            }
        };
    }

    /// <summary>
    /// 把插件返回的一整组 PluginMenuEntry 转成 WPF 菜单对象(IEnumerable&lt;object&gt;，元素是 MenuItem/Separator)。
    /// 处理细节：
    ///   - 跳过 null 项；
    ///   - 自动合并连续的分隔线（中间多个 Sep 只留一个），并丢弃开头/结尾多余的分隔线；
    ///   - invoke 是“点击某菜单项时回调通知宿主去执行插件逻辑”的委托（因为插件逻辑在别的 DLL 里，宿主代为触发）。
    /// 返回 IEnumerable 而不是 List，是因为调用方直接 foreach 加进菜单，延迟转换即可，不必先全建出来。
    /// </summary>
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

    /// <summary>
    /// 把单个 PluginMenuEntry 转成一个 MenuItem。
    /// 有子项(Children)就递归 Convert 挂成子菜单；没有子项但有 Invoke 就绑点击回调。勾选态/图标/提示都照搬到 MenuItem。
    /// </summary>
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

    /// <summary>
    /// 把图标(PluginIcon，可能是字形字符串或图片)包成一个 ContentControl 交给数据模板渲染。
    /// 为什么套 ContentControl：WPF 的 DataTemplate(这里是 PluginIconTemplate) 需要一个“内容载体”来套用，
    /// 把 icon 设为 Content、ContentTemplate 指向 IconTemplate，模板就会按 icon 的形态画出字形或图片，
    /// 调用方（宿主菜单/插件菜单）不用各自写一遍图标绘制逻辑。
    /// </summary>
    private static FrameworkElement BuildIcon(PluginIcon icon) => new ContentControl
    {
        Content = icon,
        ContentTemplate = IconTemplate,
        Focusable = false,
    };

    /// <summary>
    /// 从应用资源里取菜单项样式(MiniBarMenuItemStyle)。
    /// 用 TryFindResource 而不是 FindResource：找不到时返回 null 而不是抛异常，样式缺失时菜单项退化为默认外观也不崩。
    /// </summary>
    private static Style? ResolveMenuStyle() =>
        Application.Current?.TryFindResource("MiniBarMenuItemStyle") as Style;
}
