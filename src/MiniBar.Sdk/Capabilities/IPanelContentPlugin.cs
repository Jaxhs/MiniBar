using System.Windows;

namespace MiniBar.Sdk;

/// <summary>
/// 能力：提供一个可展开的面板（点任务栏图标后从任务栏旁弹出的浮层）。
/// 面板内容在每次打开时创建、关闭时销毁，宿主不会长期持有它 —— 这是低内存占用的关键约定。
/// </summary>
public interface IPanelContentPlugin
{
    /// <summary>面板标题。</summary>
    string PanelTitle { get; }

    /// <summary>期望宽度（DIP），宿主会做屏幕边界约束。</summary>
    double PreferredWidth => 380;

    /// <summary>期望高度（DIP）。</summary>
    double PreferredHeight => 300;

    /// <summary>
    /// 创建面板内容。每次打开都会调用，请勿缓存 <paramref name="host"/> 之外的宿主对象。
    /// 返回的元素会被放进宿主的浮层窗口，可以正常使用绑定的插件私有 ViewModel。
    /// </summary>
    FrameworkElement CreateContent(IPanelHost host);

    /// <summary>面板关闭后调用，用于停止计时器、断开事件，释放内存。</summary>
    void ReleaseContent() { }
}

/// <summary>面板与宿主的交互接口。</summary>
public interface IPanelHost
{
    IPluginContext Plugin { get; }

    /// <summary>请求关闭面板。</summary>
    void Close();

    /// <summary>请求调整浮层尺寸。</summary>
    void Resize(double width, double height);

    /// <summary>更新浮层标题。</summary>
    void SetTitle(string title);

    /// <summary>把浮层钉住，使其在失焦时不自动关闭。</summary>
    void SetPinned(bool pinned);

    bool IsPinned { get; }
}
