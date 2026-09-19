using System.Windows;

namespace MiniBar.Sdk;

/// <summary>
/// 能力：提供“迷你模式”紧凑内容。有其他程序全屏时，宿主隐藏任务栏、只显示这个小窗口，
/// 里面就是被指定插件（默认取第一个实现本接口的插件）的紧凑界面。
/// 内容应当极简、只读、不抢焦点。
/// </summary>
public interface ICompactContentPlugin
{
    string CompactTitle { get; }

    double PreferredCompactWidth => 200;

    double PreferredCompactHeight => 44;

    /// <summary>创建紧凑内容。与面板一样，宿主会在退出迷你模式时释放它。</summary>
    FrameworkElement CreateCompactContent();

    /// <summary>退出迷你模式时调用。</summary>
    void ReleaseCompactContent() { }
}
