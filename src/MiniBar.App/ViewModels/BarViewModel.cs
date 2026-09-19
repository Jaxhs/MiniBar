using System.Collections.ObjectModel;
using System.Windows.Controls;
using MiniBar.App.Hosting;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.ViewModels;

/// <summary>任务栏显示区的视图模型。插件列表由宿主同步进来，顺序即固定顺序。</summary>
public sealed class BarViewModel : ObservableObject
{
    private Orientation _itemsOrientation = Orientation.Horizontal;
    private bool _showLabels;
    private bool _hasOverflow;
    private bool _isEmpty = true;
    private bool _miniIndicatorVisible;
    private DockEdge _edge = DockEdge.Bottom;
    private string _gripGlyph = "\uE700";

    public ObservableCollection<PluginDescriptor> PinnedItems { get; } = new();

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

    /// <summary>最小变更同步：只做必要的增删移动，避免重建容器（插件内嵌内容才不会被反复重建）。</summary>
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
