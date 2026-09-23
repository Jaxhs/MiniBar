using System.Collections.Generic;

namespace MiniBar.App.Infrastructure;

public enum DockEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

public enum AnchorCorner
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>宿主全局配置，持久化到 %APPDATA%\MiniBar\settings.json。</summary>
public sealed class AppSettings
{
    // ---- 任务栏外观与位置 ----
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>显示器序号（0 起），-1 表示主显示器。</summary>
    public int MonitorIndex { get; set; } = -1;

    /// <summary>距屏幕工作区边缘的边距（DIP）。</summary>
    public double Margin { get; set; } = 8;

    /// <summary>
    /// 是否注册为系统 AppBar：像任务栏那样真正占住一条屏幕边缘，
    /// 其它窗口最大化时会自动避开（这是 Shell 提供的能力，不是靠置顶硬顶）。
    /// </summary>
    public bool UseAppBar { get; set; } = true;

    /// <summary>AppBar 模式的厚度（DIP）。只有在 <see cref="UseAppBar"/> 为真时生效。</summary>
    public double AppBarThickness { get; set; } = 52;

    public double BarOpacity { get; set; } = 0.94;

    public string Theme { get; set; } = "Light";

    public bool ShowOverflowButton { get; set; } = true;

    public bool ShowItemLabels { get; set; }

    // ---- 常驻置顶 ----
    /// <summary>周期性重申 HWND_TOPMOST，防止被其他置顶窗口挤下去。</summary>
    public bool ReassertTopmost { get; set; } = true;

    public int TopmostGuardIntervalMs { get; set; } = 3000;

    /// <summary>加入 WS_EX_TOOLWINDOW：不出现在任务栏和 Alt+Tab。</summary>
    public bool HideFromAltTab { get; set; } = true;

    // ---- 全屏 / 迷你模式 ----
    public bool AutoEnterMiniMode { get; set; } = true;

    /// <summary>检测到全屏时彻底隐藏任务栏（否则仅降级为不吃焦点的浮层）。</summary>
    public bool HideBarWhenFullscreen { get; set; } = true;

    public int FullscreenPollMs { get; set; } = 700;

    /// <summary>把“专注助手/演示模式”也算作需要进入迷你模式。</summary>
    public bool TreatQuietHoursAsFullscreen { get; set; }

    public string? MiniModePluginId { get; set; }

    public AnchorCorner MiniWindowAnchor { get; set; } = AnchorCorner.TopCenter;

    /// <summary>迷你窗口是否接受焦点。默认不接受：全屏游戏/演示时点击不抢焦点。</summary>
    public bool MiniWindowAcceptsFocus { get; set; }

    // ---- 交互 ----
    public bool CloseFlyoutOnDeactivate { get; set; } = true;

    public bool ConfirmBeforeUninstall { get; set; } = true;

    public bool AutoLoadPluginOnDllDrop { get; set; } = true;

    public bool WatchPluginFolders { get; set; } = true;

    /// <summary>空闲时裁剪工作集，把未被访问的内存页还给系统（任务管理器里的内存读数会明显下降）。</summary>
    public bool TrimWorkingSetOnIdle { get; set; } = true;

    public int IdleTrimSeconds { get; set; } = 20;

    public bool VerboseLogging { get; set; }

    /// <summary>插件私有配置：插件 ID → (键 → JSON 文本)。由 IPluginContext.GetSetting/SetSetting 读写。</summary>
    public Dictionary<string, Dictionary<string, string>> PluginSettings { get; set; } = new();

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }
}
