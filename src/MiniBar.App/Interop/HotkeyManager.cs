using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MiniBar.App.Infrastructure;
using MiniBar.Sdk;

namespace MiniBar.App.Interop;

public sealed record HotkeyRegistration(int HandleId, string PluginId, string HotkeyId, string DisplayName, bool Success, string? Reason);

/// <summary>
/// 全局快捷键注册表。全部热键都挂在同一个隐藏消息窗口上（任务栏窗口本身），
/// 用自增 HandleId 与 (插件ID, 热键ID) 建立映射，WM_HOTKEY 到达时分发回插件。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<int, (string PluginId, string HotkeyId)> _map = new();
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HotkeyRegistration> _lastResult = new();

    private IntPtr _hwnd;
    private HwndSource? _source;
    private int _nextId = 0x4000;

    public event EventHandler<HotkeyRegistration>? Triggered;

    public IReadOnlyList<HotkeyRegistration> LastResult => _lastResult;

    public void Attach(Window window)
    {
        // WPF 的 Window 是托管对象，没有原生句柄。WindowInteropHelper 是"桥"：
        // 它给这个托管窗口要/建一个真正的 Win32 句柄（HWND）。在窗口真正显示前句柄可能还是 Zero，
        // 所以这里检查；若为零说明调用太早，需要先 EnsureHandle()。
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("窗口句柄尚未创建，请先调用 EnsureHandle()。");
        }

        _hwnd = handle;
        // HwndSource 是把"托管 Dispatcher"和"原生窗口消息循环"连起来的对象。
        // AddHook 让我们把一个函数（WndProc）挂进这个窗口的消息处理链：
        // 之后凡是发到本窗口的 Windows 消息（包括系统发来的 WM_HOTKEY）都会先经过 WndProc，
        // 我们就能在里面把热键事件分发给对应插件。
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    /// <summary>把已注册的热键全部注销（用于重新扫描插件后再注册）。</summary>
    public void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _map.Keys)
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
            }
        }

        _map.Clear();
        _registered.Clear();
        _lastResult.Clear();
    }

    /// <summary>
    /// 注册一个全局快捷键。流程：
    /// ① 用 "<c>插件ID::热键ID</c>" 去重，同一个组合只注册一次（避免重复占系统资源）；
    /// ② 把 WPF 的 <c>Key</c> 转成 Windows 虚拟键码 <c>VK</c>，再拼上修饰符（Alt/Ctrl/Shift/Win）+ <c>MOD_NOREPEAT</c>
    ///    （按住不连发）；
    /// ③ 调 <c>RegisterHotKey</c> 向系统"认领"这个组合键。系统会把"按键→我们的窗口"绑定好，
    ///    以后用户一按，系统就发一条 <c>WM_HOTKEY</c> 到我们的窗口过程（见 <see cref="WndProc"/>）；
    /// ④ 成功就把 (自增 id → 插件/热键) 记进 <c>_map</c>，方便按键时反查是谁的；
    ///    失败（多半是该组合已被别的程序占用）不抛异常，而是记进 <c>_lastResult</c> 回传给插件/写日志。
    /// </summary>
    public bool Register(string pluginId, string hotkeyId, HotkeyModifiers modifiers, Key key, string displayName)
    {
        var keyId = $"{pluginId}::{hotkeyId}";
        if (!_registered.Add(keyId))
        {
            return true;
        }

        if (_hwnd == IntPtr.Zero)
        {
            _registered.Remove(keyId);
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            _registered.Remove(keyId);
            _lastResult.Add(new HotkeyRegistration(0, pluginId, hotkeyId, displayName, false, "无法识别的按键"));
            return false;
        }

        var id = _nextId++;
        var nativeModifiers = ToNative(modifiers) | NativeMethods.MOD_NOREPEAT;

        if (!NativeMethods.RegisterHotKey(_hwnd, id, nativeModifiers, vk))
        {
            _registered.Remove(keyId);
            var reason = "该组合键已被其它程序占用";
            AppLog.Warn($"快捷键注册失败：{displayName}（{pluginId}/{hotkeyId}）{reason}");
            _lastResult.Add(new HotkeyRegistration(id, pluginId, hotkeyId, displayName, false, reason));
            return false;
        }

        _map[id] = (pluginId, hotkeyId);
        _lastResult.Add(new HotkeyRegistration(id, pluginId, hotkeyId, displayName, true, null));
        AppLog.Info($"快捷键已注册：{displayName} -> {pluginId}/{hotkeyId}（id={id}）");
        return true;
    }

    /// <summary>注销某个插件的全部热键（插件卸载/禁用时调用）。</summary>
    public void UnregisterPlugin(string pluginId)
    {
        var ids = _map.Where(kv => string.Equals(kv.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var id in ids)
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
            }

            if (_map.Remove(id, out var entry))
            {
                _registered.Remove($"{entry.PluginId}::{entry.HotkeyId}");
            }
        }
    }

    /// <summary>
    /// 窗口消息钩子（由 <see cref="Attach"/> 里的 <c>AddHook</c> 挂上）。
    /// 当系统把 <c>WM_HOTKEY</c> 发到本窗口时，<c>wParam</c> 就是当初注册时拿到的那个 id；
    /// 我们用它在 <c>_map</c> 里反查出"哪个插件的哪个热键被按了"，触发 <see cref="Triggered"/> 事件把结果分发出去，
    /// 并把 <c>handled</c> 置 true，告诉系统"这条消息我已处理，别再往下传"。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_map.TryGetValue(id, out var target))
            {
                handled = true;
                Triggered?.Invoke(this, new HotkeyRegistration(id, target.PluginId, target.HotkeyId, string.Empty, true, null));
            }
        }

        return IntPtr.Zero;
    }

    private static uint ToNative(HotkeyModifiers modifiers)
    {
        uint result = 0;

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= NativeMethods.MOD_ALT;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= NativeMethods.MOD_CONTROL;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= NativeMethods.MOD_SHIFT;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= NativeMethods.MOD_WIN;
        }

        return result;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
