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

/// <summary>点击任务栏图标时传给插件的上下文。宿主已经处理了面板开关，这里只做插件自定义逻辑。</summary>
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

    public void OpenPanel() => Plugin.Shell.OpenPanel(Plugin.PluginId);

    public void ClosePanel() => Plugin.Shell.ClosePanel(Plugin.PluginId);

    public void TogglePanel() => Plugin.Shell.TogglePanel(Plugin.PluginId);

    public void EnterMiniMode() => Plugin.Shell.EnterMiniMode(Plugin.PluginId);
}
