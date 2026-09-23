/// <summary>
/// 本文件定义点击任务栏图标时传给插件的上下文 <see cref="BarItemClickContext"/> 与点击类型枚举 <see cref="BarItemActivationKind"/>。
///
/// <para>这里是“点击语义”的核心约定（踩过坑）：宿主先调 <c>OnClick</c>，再决定要不要自动开关面板。
/// 详见本类 <see cref="BarItemClickContext"/> 的类型说明——核心记住一句话：<b>接管界面时只在
/// <see cref="BarItemActivationKind.Primary"/>（左键）时动手</b>，中键/双击交给宿主默认处理，否则两边各 toggle 一次，
/// 面板会“开一下马上关”。</para>
///
/// <para><b>MouseButton / ModifierKeys 是什么：</b><see cref="System.Windows.Input.MouseButton"/> 表示鼠标哪个键
/// （左/中/右）；<see cref="System.Windows.Input.ModifierKeys"/> 表示按住的组合修饰键（Ctrl/Shift/Alt/Win）。
/// 插件可用它们判断“用户是按着 Ctrl 点的吗”之类的场景。</para>
/// </summary>
using System.Windows.Input;

namespace MiniBar.Sdk;

public enum BarItemActivationKind
{
    /// <summary>左键单击。</summary>
    Primary,

    /// <summary>中键单击（宿主默认行为：关闭该插件面板）。</summary>
    Auxiliary,

    /// <summary>双击。</summary>
    DoubleClick,
}

/// <summary>
/// 点击任务栏图标时传给插件的上下文。
///
/// <para>
/// <b>关于面板开关的约定（很重要，踩过坑）：</b>
/// 点击图标后宿主会先调用插件的 <c>OnClick</c>，然后：
/// </para>
/// <list type="bullet">
///   <item>如果插件在这次 OnClick 里自己调用了 <see cref="OpenPanel"/> /
///         <see cref="ClosePanel"/> / <see cref="TogglePanel"/> / <see cref="EnterMiniMode"/>
///         （即 <see cref="ClickHandled"/> 为 true），宿主<b>不再</b>做任何默认动作；</item>
///   <item>否则，只要插件提供了面板内容（实现了 <c>IPanelContentPlugin</c>），
///         宿主按"任务栏图标"的默认语义自动切换面板。</item>
/// </list>
/// <para>
/// 所以插件有两条路：<b>要么完全不管界面</b>（宿主自动开关面板），
/// <b>要么自己接管</b>（例如想改成"右键才开面板"）。**不要两边都做** ——
/// 宿主 toggle 一次 + 插件再 toggle 一次 = 面板开了马上又关，看起来就是"闪一下"。
/// </para>
///
/// <para><b>补充（踩坑要点）：</b>若选择自己接管，请<b>只在
/// <see cref="BarItemActivationKind.Primary"/>（左键单击）时</b>去动面板/迷你模式；
/// 中键、双击的默认语义（如中键关闭面板）交给宿主处理，否则同样会造成“闪一下”或行为异常。</para>
/// </summary>
public sealed class BarItemClickContext
{
    /// <summary>由宿主创建，插件不应自行 new。</summary>
    public BarItemClickContext(IPluginContext plugin, BarItemActivationKind kind, bool isPinned, bool isPanelOpen,
        bool isMiniMode, MouseButton button, ModifierKeys modifiers)
    {
        Plugin = plugin;
        Kind = kind;
        IsPinned = isPinned;
        IsPanelOpen = isPanelOpen;
        IsMiniMode = isMiniMode;
        Button = button;
        Modifiers = modifiers;
    }

    public IPluginContext Plugin { get; }

    public BarItemActivationKind Kind { get; }

    public bool IsPinned { get; }

    public bool IsPanelOpen { get; }

    public bool IsMiniMode { get; }

    public MouseButton Button { get; }

    public ModifierKeys Modifiers { get; }

    /// <summary>
    /// 插件是否已经自己处理了这次点击（动过面板或迷你模式）。
    /// 由宿主读取后决定要不要套用默认行为；插件只需要调用下面那几个方法，不用自己设它。
    /// </summary>
    public bool ClickHandled { get; private set; }

    public void OpenPanel()
    {
        ClickHandled = true;
        Plugin.Shell.OpenPanel(Plugin.PluginId);
    }

    public void ClosePanel()
    {
        ClickHandled = true;
        Plugin.Shell.ClosePanel(Plugin.PluginId);
    }

    public void TogglePanel()
    {
        ClickHandled = true;
        Plugin.Shell.TogglePanel(Plugin.PluginId);
    }

    public void EnterMiniMode()
    {
        ClickHandled = true;
        Plugin.Shell.EnterMiniMode(Plugin.PluginId);
    }
}
