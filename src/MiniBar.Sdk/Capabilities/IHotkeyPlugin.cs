using System.Windows.Input;

namespace MiniBar.Sdk;

[Flags]
public enum HotkeyModifiers
{
    None = 0,

    /// <summary>对应 Win32 MOD_ALT。</summary>
    Alt = 1,

    /// <summary>对应 Win32 MOD_CONTROL。</summary>
    Control = 2,

    /// <summary>对应 Win32 MOD_SHIFT。</summary>
    Shift = 4,

    /// <summary>对应 Win32 MOD_WIN。</summary>
    Windows = 8,
}

public sealed class PluginHotkey
{
    public PluginHotkey(string id, string displayName, HotkeyModifiers modifiers, Key key)
    {
        Id = id;
        DisplayName = displayName;
        Modifiers = modifiers;
        Key = key;
    }

    /// <summary>插件内唯一标识，回调时原样返回。</summary>
    public string Id { get; }

    /// <summary>用于菜单显示，例如 "Ctrl+Alt+N"。</summary>
    public string DisplayName { get; }

    public HotkeyModifiers Modifiers { get; }

    public Key Key { get; }
}

/// <summary>
/// 能力：注册全局快捷键。宿主统一负责 RegisterHotKey 的注册、注销与冲突提示，
/// 插件只需声明键位并在 <see cref="OnHotkey"/> 里执行命令。
/// </summary>
public interface IHotkeyPlugin
{
    IEnumerable<PluginHotkey> GetHotkeys();

    /// <summary>快捷键被按下（回调在 UI 线程）。<paramref name="hotkeyId"/> 即 <see cref="PluginHotkey.Id"/>。</summary>
    void OnHotkey(string hotkeyId);

    /// <summary>
    /// 某条快捷键注册失败（被别的程序占用）时的通知，用于在插件里显示提示。
    /// </summary>
    void OnHotkeyRegistrationFailed(string hotkeyId, string reason) { }
}
