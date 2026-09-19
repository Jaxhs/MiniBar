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

    public PluginIcon? Icon { get; init; }

    public bool IsSeparator { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsChecked { get; init; }

    public Action? Invoke { get; init; }

    public IReadOnlyList<PluginMenuEntry>? Children { get; init; }

    public string? ToolTip { get; init; }

    public static PluginMenuEntry Separator() => new("-") { IsSeparator = true };

    public static PluginMenuEntry Command(string header, Action invoke, PluginIcon? icon = null,
        bool isEnabled = true, string? toolTip = null) =>
        new(header) { Invoke = invoke, Icon = icon, IsEnabled = isEnabled, ToolTip = toolTip };

    public static PluginMenuEntry Toggle(string header, bool isChecked, Action invoke, PluginIcon? icon = null) =>
        new(header) { Invoke = invoke, Icon = icon, IsChecked = isChecked };

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
    IEnumerable<PluginMenuEntry> GetMenuEntries(PluginMenuContext context);
}
