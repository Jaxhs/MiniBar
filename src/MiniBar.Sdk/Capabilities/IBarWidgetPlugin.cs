using System.Windows;

namespace MiniBar.Sdk;

/// <summary>
/// 能力：直接在任务栏“显示区域”里渲染自定义内容（而不只是一个图标）。
/// 例如：内嵌时钟、CPU 曲线、搜索框、播放控制条、便签……
///
/// 这是“在显示区域显示自定义内容”的落点：宿主会把返回的元素放进任务栏面板里，
/// 插件自行负责它的渲染与刷新（建议控件空闲时才刷新，保持宿主低内存低 CPU）。
/// </summary>
public interface IBarWidgetPlugin
{
    /// <summary>创建内嵌内容。宿主在任务栏重建时会调用一次，销毁时调用 <see cref="ReleaseBarWidget"/>。</summary>
    FrameworkElement CreateBarWidget();

    /// <summary>期望宽度（DIP）；&lt;= 0 表示由内容自行决定（Auto）。</summary>
    double WidgetWidth => 0;

    /// <summary>期望高度（DIP）；&lt;= 0 表示由任务栏高度决定。</summary>
    double WidgetHeight => 0;

    /// <summary>任务栏元素被移除时调用，用于停止计时器、解绑事件。</summary>
    void ReleaseBarWidget() { }
}
