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
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("窗口句柄尚未创建，请先调用 EnsureHandle()。");
        }

        _hwnd = handle;
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
    /// 注册快捷键。同一个 (插件, 热键) 组合只会注册一次；
    /// 失败（通常是被别的程序占用）不会抛异常，而是记录在 <see cref="LastResult"/> 里回传给插件。
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
