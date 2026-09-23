using System.Windows;

namespace MiniBar.Sdk;

/// <summary>
/// 能力：直接在任务栏“显示区域”里渲染自定义内容（而不只是一个图标）。
/// 例如：内嵌时钟、CPU 曲线、搜索框、播放控制条、便签……
///
/// <para>
/// 这是“在显示区域显示自定义内容”的落点：宿主会把返回的元素放进任务栏面板里，
/// 插件自行负责它的渲染与刷新（建议控件空闲时才刷新，保持宿主低内存低 CPU）。
/// </para>
///
/// <para><b>关于点击</b></para>
/// <list type="bullet">
///   <item>默认行为：点击整块内嵌内容 = 点击该插件的图标（也就是"开关它的面板"）——
///         所以时钟、久坐提醒这类只显示读数的插件，点读数就能打开面板；</item>
///   <item>如果你在内容里放了真正的控件（<c>Button</c>、<c>TextBox</c>…），
///         它们会自己吃掉点击，不会误触发开关面板；</item>
///   <item>如果你用的是非控件元素（例如 <c>Border</c>）却希望它"吞掉"点击，
///         在那个元素上设置 <c>Tag = "Interactive"</c> 即可。</item>
/// </list>
///
/// <para><b>宽度</b>：<see cref="WidgetWidth"/> 返回 &lt;= 0 表示由内容自己撑开。
/// 若内容宽度会随数据变化（比如时间字符串变长），建议返回一个按最宽内容估算的固定值，
/// 否则任务栏会随着每次刷新左右抖动。</para>
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
