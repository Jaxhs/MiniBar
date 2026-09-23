using System.Collections.ObjectModel;
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
