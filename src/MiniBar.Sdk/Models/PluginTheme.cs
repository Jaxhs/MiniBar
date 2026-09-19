namespace MiniBar.Sdk;

/// <summary>
/// 主题快照。为了让插件不依赖宿主的资源字典，这里只传递语义化颜色（#AARRGGBB）。
/// 插件界面应始终使用这些颜色，从而与宿主主题保持一致。
/// </summary>
public sealed class PluginTheme
{
    public PluginTheme(bool isDark, string background, string surface, string foreground, string muted,
        string border, string accent, string accentForeground, string danger)
    {
        IsDark = isDark;
        Background = background;
        Surface = surface;
        Foreground = foreground;
        Muted = muted;
        Border = border;
        Accent = accent;
        AccentForeground = accentForeground;
        Danger = danger;
    }

    public bool IsDark { get; }

    /// <summary>主背景（面板底色）。</summary>
    public string Background { get; }

    /// <summary>卡片/控件底色。</summary>
    public string Surface { get; }

    /// <summary>主文本色。</summary>
    public string Foreground { get; }

    /// <summary>次要文本色。</summary>
    public string Muted { get; }

    public string Border { get; }

    public string Accent { get; }

    public string AccentForeground { get; }

    public string Danger { get; }

    public static PluginTheme Light { get; } = new(
        isDark: false,
        background: "#F2FFFFFF",
        surface: "#66FFFFFF",
        foreground: "#FF1B1B1F",
        muted: "#FF6B6B75",
        border: "#22000000",
        accent: "#FF0A84FF",
        accentForeground: "#FFFFFFFF",
        danger: "#FFD93025");

    public static PluginTheme Dark { get; } = new(
        isDark: true,
        background: "#F21C1C1E",
        surface: "#1AFFFFFF",
        foreground: "#FFF2F2F5",
        muted: "#FF9A9AA5",
        border: "#26FFFFFF",
        accent: "#FF64A8FF",
        accentForeground: "#FF0B0B0F",
        danger: "#FFFF6B60");
}
