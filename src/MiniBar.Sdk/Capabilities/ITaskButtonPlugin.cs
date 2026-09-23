/// <summary>
/// 本文件定义“任务栏图标”能力 <see cref="ITaskButtonPlugin"/>：插件在任务栏显示区出现一个图标，
/// 以及图标状态快照 <see cref="BarItemState"/>。
///
/// <para>固定/取消固定、排序、拖拽重排由宿主统一负责并持久化，插件只管“显示什么、点击后做什么”。
/// 点击语义见 <see cref="BarItemClickContext"/>：默认宿主自动切换面板，插件也可通过
/// <see cref="ITaskButtonPlugin.ManagesOwnActivation"/> 完全接管。</para>
/// </summary>
namespace MiniBar.Sdk;

/// <summary>
/// 能力：在任务栏显示区出现一个图标（类似 Windows 任务栏的任务按钮）。
/// 固定、取消固定、排序、拖拽重排由宿主统一负责并持久化，插件无需关心。
/// </summary>
public interface ITaskButtonPlugin
{
    /// <summary>图标。建议写成字符串：<c>=> "emoji:🕒"</c> 或 <c>=> "glyph:E823"</c>。</summary>
    PluginIcon Icon { get; }

    /// <summary>显示名称（悬停提示、插件管理器、溢出菜单）。</summary>
    string DisplayName { get; }

    /// <summary>悬停提示的补充行。</summary>
    string? Tooltip => null;

    /// <summary>右下角角标文本（未读数、状态等），null 表示不显示。</summary>
    string? Badge => null;

    /// <summary>角标是否为强调色（默认灰底）。</summary>
    bool BadgeIsAccent => false;

    /// <summary>首次被宿主发现时是否默认固定到显示区。</summary>
    bool DefaultPinned => false;

    /// <summary>
    /// 是否由插件自己决定“点击后干什么”。
    /// 默认 false = 宿主按标准任务栏语义自动切换该插件的面板（有面板能力时），
    /// 插件仍然会收到 <see cref="OnClick"/> 做附加动作。
    /// 设为 true 后宿主不再自动开关面板，一切交给 <see cref="OnClick"/>。
    /// </summary>
    bool ManagesOwnActivation => false;

    /// <summary>
    /// 图标被左键/中键/双击时的回调。宿主已自动处理“切换面板”这一默认行为，
    /// 若插件想接管（例如弹出自己的窗口而不是面板），在这里实现即可。
    /// </summary>
    void OnClick(BarItemClickContext context);

    /// <summary>固定状态、面板开关状态或迷你模式变化时通知插件（用于更新图标表现）。</summary>
    void OnStateChanged(BarItemState state) { }
}

/// <summary>任务栏图标的当前状态快照。</summary>
public readonly record struct BarItemState(bool IsPinned, bool IsPanelOpen, bool IsMiniMode, bool IsMiniModeHost);
