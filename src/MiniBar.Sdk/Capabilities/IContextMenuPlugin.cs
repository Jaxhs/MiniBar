/// <summary>
/// 本文件定义“右键菜单”能力，以及描述一条菜单项的 <see cref="PluginMenuEntry"/> 与菜单上下文 <see cref="PluginMenuContext"/>。
///
/// <para>宿主会在四个位置（任务栏空白处、图标上、迷你窗口、插件管理器）询问所有菜单插件，
/// 插件把自己的功能挂到任意位置即可。菜单项用静态工厂（Command/Toggle/Submenu/Separator）构造，
/// 不用手写一大坨初始化代码。</para>
///
/// <para><b>Action 是什么：</b>一种“可以稍后执行的逻辑”（无返回值、无参数的回调 / 委托）。
/// <see cref="PluginMenuEntry.Invoke"/> 就是“用户点了这个菜单项时要跑哪段代码”，插件把方法或 Lambda 塞进去即可。</para>
/// </summary>
namespace MiniBar.Sdk;

public enum PluginMenuTarget
{
    /// <summary>任务栏空白处右键。</summary>
    BarBackground,

    /// <summary>某个任务栏图标上右键。</summary>
    BarItem,

    /// <summary>迷你窗口上右键。</summary>
    MiniWindow,

    /// <summary>插件管理器窗口里右键。</summary>
    PluginManager,
}

public sealed class PluginMenuContext
{
    /// <summary>由宿主创建。</summary>
    public PluginMenuContext(IPluginContext plugin, PluginMenuTarget target, string? targetPluginId,
        IReadOnlyList<string>? dropPaths = null)
    {
        Plugin = plugin;
        Target = target;
        TargetPluginId = targetPluginId;
        DropPaths = dropPaths ?? Array.Empty<string>();
    }

    public IPluginContext Plugin { get; }

    public PluginMenuTarget Target { get; }

    /// <summary>当 <see cref="Target"/> 为 <see cref="PluginMenuTarget.BarItem"/> 时，被右键的插件 ID。</summary>
    public string? TargetPluginId { get; }

    /// <summary>拖放到图标上时携带的路径（用于“用 XX 打开”之类的菜单项）。</summary>
    public IReadOnlyList<string> DropPaths { get; }

    public bool IsForSelf => TargetPluginId is null || TargetPluginId == Plugin.PluginId;
}

/// <summary>一条菜单项。用静态工厂构造，避免插件写一大坨初始化代码。</summary>
public sealed class PluginMenuEntry
{
    private PluginMenuEntry(string header)
    {
        Header = header;
    }

    public string Header { get; }

    /// <summary>菜单项左侧图标（可为 null 不显示）。</summary>
    public PluginIcon? Icon { get; init; }

    /// <summary>是否为分隔线（为 true 时其它字段基本忽略）。</summary>
    public bool IsSeparator { get; init; }

    /// <summary>是否可点击（默认 true）；false 时灰显。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>是否为“勾选”状态（用于开关类菜单项，配合 <see cref="Submenu"/> 之外的 Toggle 工厂）。</summary>
    public bool IsChecked { get; init; }

    /// <summary>用户点击该菜单项时要执行的逻辑（无参回调 / 委托）。</summary>
    public Action? Invoke { get; init; }

    /// <summary>子菜单项（仅当用 <see cref="Submenu"/> 构造时非空）。</summary>
    public IReadOnlyList<PluginMenuEntry>? Children { get; init; }

    /// <summary>鼠标悬停提示（可为 null）。</summary>
    public string? ToolTip { get; init; }

    /// <summary>一条分隔线（用于把菜单项分组）。</summary>
    public static PluginMenuEntry Separator() => new("-") { IsSeparator = true };

    /// <summary>一个普通命令项：显示文字、点击执行 <paramref name="invoke"/>。</summary>
    public static PluginMenuEntry Command(string header, Action invoke, PluginIcon? icon = null,
        bool isEnabled = true, string? toolTip = null) =>
        new(header) { Invoke = invoke, Icon = icon, IsEnabled = isEnabled, ToolTip = toolTip };

    /// <summary>一个可勾选项：<paramref name="isChecked"/> 控制前面的勾，点击执行 <paramref name="invoke"/>。</summary>
    public static PluginMenuEntry Toggle(string header, bool isChecked, Action invoke, PluginIcon? icon = null) =>
        new(header) { Invoke = invoke, Icon = icon, IsChecked = isChecked };

    /// <summary>一个子菜单：鼠标悬停/点击展开 <paramref name="children"/>。</summary>
    public static PluginMenuEntry Submenu(string header, IEnumerable<PluginMenuEntry> children, PluginIcon? icon = null) =>
        new(header) { Children = children.ToArray(), Icon = icon };
}

/// <summary>
/// 能力：往宿主已有的右键菜单里追加菜单项。
/// 宿主会在“任务栏空白处”、“图标上”、“迷你窗口”、“插件管理器”四个位置询问所有菜单插件，
/// 因此插件可以把自己的功能挂到任意位置 —— 这就是“插件可出现在任意位置”。
/// </summary>
public interface IContextMenuPlugin
{
    /// <summary>宿主在相应位置弹出菜单时调用，返回插件要插入的菜单项集合（可为空）。</summary>
    IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context);
}
