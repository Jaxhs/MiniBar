namespace MiniBar.Sdk;

/// <summary>插件在宿主中的当前运行状态（只读快照，用于插件间协作与诊断）。</summary>
public sealed class PluginInfo
{
    public PluginInfo(string id, string name, string? version, string filePath, bool enabled, bool loaded,
        bool pinned, string[] capabilities, string? error)
    {
        Id = id;
        Name = name;
        Version = version;
        FilePath = filePath;
        Enabled = enabled;
        Loaded = loaded;
        Pinned = pinned;
        Capabilities = capabilities;
        Error = error;
    }

    public string Id { get; }

    public string Name { get; }

    public string? Version { get; }

    public string FilePath { get; }

    /// <summary>用户是否启用（持久化）。禁用后文件仍在，但不加载、不占内存。</summary>
    public bool Enabled { get; }

    /// <summary>当前是否已加载进内存并完成 Initialize。</summary>
    public bool Loaded { get; }

    /// <summary>是否固定到任务栏显示区。</summary>
    public bool Pinned { get; }

    public string[] Capabilities { get; }

    /// <summary>加载/初始化失败原因。</summary>
    public string? Error { get; }
}

/// <summary>能力名称常量，避免插件与宿主对字符串的拼写不一致。</summary>
public static class PluginCapabilities
{
    public const string TaskButton = "任务栏图标";
    public const string BarWidget = "显示区内嵌内容";
    public const string Panel = "面板内容";
    public const string Compact = "迷你内容";
    public const string ContextMenu = "右键菜单";
    public const string Hotkey = "全局快捷键";
    public const string DropHandler = "拖放处理";

    public const string TaskButtonInterface = "MiniBar.Sdk.ITaskButtonPlugin";
    public const string BarWidgetInterface = "MiniBar.Sdk.IBarWidgetPlugin";
    public const string PanelInterface = "MiniBar.Sdk.IPanelContentPlugin";
    public const string CompactInterface = "MiniBar.Sdk.ICompactContentPlugin";
    public const string ContextMenuInterface = "MiniBar.Sdk.IContextMenuPlugin";
    public const string HotkeyInterface = "MiniBar.Sdk.IHotkeyPlugin";
    public const string DropInterface = "MiniBar.Sdk.IDropHandlerPlugin";
}
