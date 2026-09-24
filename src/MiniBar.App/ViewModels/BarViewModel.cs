using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.ViewModels;

/// <summary>
/// 任务栏显示区的视图模型（MVVM 里的 ViewModel）。手写实现，不依赖任何 MVVM 框架：
///   · 集合用 ObservableCollection&lt;PluginDescriptor&gt;——它内部已实现“集合变化自动通知界面”，增删元素时绑定它的 ItemsControl 会自动刷新；
///   · 单个属性变化通知靠继承自 ObservableObject 的 SetProperty（改了属性就自动触发 PropertyChanged，界面绑定的控件重绘）；
///   · 由边缘（Edge）推导出的属性（如 IsVertical）留在 VM 里，界面 XAML 直接绑定，避免在代码-behind 里写一堆 if 判断；
///   · 插件列表由宿主（PluginHost）通过 Sync() 增量同步进来，VM 自身不负责加载/卸载，只是“展示态”的镜子。
/// </summary>
public sealed class BarViewModel : ObservableObject
{
    private Orientation _itemsOrientation = Orientation.Horizontal;
    private Thickness _itemAreaMargin = new(9, 0, 9, 0);
    private Thickness _itemMargin = new(2, 0, 2, 0);
    private Thickness _itemLabelMargin = new(6, 0, 2, 0);
    private double _itemLabelMaxWidth = double.PositiveInfinity;
    private double _itemWidth = double.NaN;
    private double _itemHeight = 38;
    private bool _showLabels;
    private bool _hasOverflow;
    private bool _isEmpty = true;
    private bool _miniIndicatorVisible;
    private DockEdge _edge = DockEdge.Bottom;
    private string _gripGlyph = "\uE700";

    /// <summary>固定到任务栏的插件集合（顺序即固定顺序），直接绑定任务栏主显示区。ObservableCollection 保证增删自动刷新界面。</summary>
    public ObservableCollection<PluginDescriptor> PinnedItems { get; } = new();

    /// <summary>未固定、收入“更多”里的插件集合，绑定溢出菜单。同样由宿主经 Sync() 增量维护。</summary>
    public ObservableCollection<PluginDescriptor> OverflowItems { get; } = new();

    public Orientation ItemsOrientation
    {
        get => _itemsOrientation;
        set
        {
            if (SetProperty(ref _itemsOrientation, value))
            {
                OnPropertyChanged(nameof(IsVertical));
            }
        }
    }

    /// <summary>由 ItemsOrientation 推导：竖排时为 true。把这个“派生状态”放在 VM 里，界面直接绑定，无需在 XAML/后台写方向判断。</summary>
    public bool IsVertical => ItemsOrientation == Orientation.Vertical;

    public bool ShowLabels
    {
        get => _showLabels;
        set => SetProperty(ref _showLabels, value);
    }

    /// <summary>
    /// 每个图标区的外边距。横排时左右留白、竖排时上下留白 ——
    /// 侧边（左/右）模式下任务栏很窄，若还按横排那样左右留 9，图标就会被挤掉。
    /// </summary>
    public Thickness ItemAreaMargin
    {
        get => _itemAreaMargin;
        set => SetProperty(ref _itemAreaMargin, value);
    }

    /// <summary>
    /// 整块任务项的外边距，跟随方向。
    ///
    /// <para>横排 (2,0)：左右各留 2 把相邻图标分开，上下留 0 让图标撑满任务栏高度。</para>
    /// <para>竖排 (2,6)：<b>上下必须留白</b> —— 竖排时任务项是一个个上下摞起来的，
    /// 上下边距为 0 的话四项会糊成一团（用户反馈的"内容太过紧凑"）。
    /// 左右仍留 2，配合 ItemWidth 保证内容不会贴到边。</para>
    /// </summary>
    public Thickness ItemMargin
    {
        get => _itemMargin;
        set => SetProperty(ref _itemMargin, value);
    }

    /// <summary>文字标签的外边距，同样跟随方向（竖排时改为上下留白）。</summary>
    public Thickness ItemLabelMargin
    {
        get => _itemLabelMargin;
        set => SetProperty(ref _itemLabelMargin, value);
    }

    /// <summary>
    /// 文字标签的最大宽度。竖排时限制为"任务栏厚度 - 留白"，超出的用省略号收尾；
    /// 横排时不限制（正无穷）。
    /// </summary>
    public double ItemLabelMaxWidth
    {
        get => _itemLabelMaxWidth;
        set => SetProperty(ref _itemLabelMaxWidth, value);
    }

    public bool HasOverflow
    {
        get => _hasOverflow;
        set => SetProperty(ref _hasOverflow, value);
    }

    /// <summary>一个插件都没固定时，任务栏上给个提示，方便用户知道可以往上面拖 DLL。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    public bool MiniIndicatorVisible
    {
        get => _miniIndicatorVisible;
        set => SetProperty(ref _miniIndicatorVisible, value);
    }

    public DockEdge Edge
    {
        get => _edge;
        set
        {
            if (SetProperty(ref _edge, value))
            {
                OnPropertyChanged(nameof(IsVertical));
            }
        }
    }

    public string GripGlyph
    {
        get => _gripGlyph;
        set => SetProperty(ref _gripGlyph, value);
    }

    /// <summary>
    /// 每个任务项在停靠方向上的<b>占用宽度</b>（DIP）。
    ///
    /// <para>横排时给 <see cref="double.NaN"/>：宽度交给内容自己撑开（任务栏够宽，不需要钉死）。</para>
    /// <para>竖排（左/右边缘）时必须给一个具体值：任务栏变窄后，
    /// <c>WrapPanel</c> 默认只给每项"内容宽度"，于是角标会压在内嵌读数上、
    /// 文字居中位置也随内容长度左右跳。钉成整条边的宽度后，每项都占满一行，
    /// 读数居中、角标待在右上角，看起来才正常。</para>
    /// </summary>
    public double ItemWidth
    {
        get => _itemWidth;
        set => SetProperty(ref _itemWidth, value);
    }

    /// <summary>
    /// 每个任务项的高度（DIP）。
    ///
    /// <para>横排给 38：任务栏是一条横带，所有图标必须等高。</para>
    /// <para>竖排给 <see cref="double.NaN"/>（= Auto，由内容决定）：竖排时图标在上、文字在下，
    /// 图标 26 + 文字 21 + 边距 ≈ 62，写死 38 会把文字的下半截切掉
    /// （用户反馈的"带文字的显示不全"就是这个）；而带内嵌读数的项只需要 38 高，
    /// 统一钉死要么切文字、要么给读数项留一大块空白，所以干脆让每项自己撑开。</para>
    /// </summary>
    public double ItemHeight
    {
        get => _itemHeight;
        set => SetProperty(ref _itemHeight, value);
    }


    /// <summary>
    /// 最小变更同步：把 target 集合调整成与 desired 顺序一致，但只做“必要的增/删/移动”。
    /// 为什么要最小变更：任务栏上的插件常内嵌自己的 UI（浮层/计时器/动画），如果每次都 Clear 再 Add，容器会被重建，
    /// 插件刚建好的内容会被反复销毁-重建，既闪又耗资源。这里先反向删除多余项，再正向把每项 Insert/Move 到正确位置。
    /// 在 UI 线程调用（ObservableCollection 有线程亲和性）。
    /// </summary>
    public static void Sync(ObservableCollection<PluginDescriptor> target, IReadOnlyList<PluginDescriptor> desired)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var current = target.IndexOf(item);

            if (current < 0)
            {
                target.Insert(Math.Min(i, target.Count), item);
            }
            else if (current != i)
            {
                target.Move(current, i);
            }
        }
    }
}
