/// <summary>
/// 本文件定义“迷你模式紧凑内容”能力 <see cref="ICompactContentPlugin"/>。
///
/// <para>当别的程序全屏时，宿主隐藏整个任务栏、只显示一个小窗口（不吃焦点），
/// 里面就是被指定插件（默认取第一个实现本接口的插件）的紧凑界面。
/// 内容应当极简、只读、不抢焦点——典型如一个极简时钟或状态灯。</para>
///
/// <para>与面板一样，宿主在退出迷你模式时会释放内容，插件应在 <see cref="ICompactContentPlugin.ReleaseCompactContent"/>
/// 里停掉计时器、断开事件，避免 ALC 回收不掉。</para>
/// </summary>
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
