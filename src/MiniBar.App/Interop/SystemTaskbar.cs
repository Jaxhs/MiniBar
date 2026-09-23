using System.Runtime.InteropServices;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

/// <summary>
/// 控制系统任务栏（Windows 自带的那条）的"自动隐藏"。
///
/// <para><b>为什么不用 ShowWindow 直接把任务栏藏起来？</b></para>
/// <para>
/// 直接 <c>ShowWindow(SW_HIDE)</c> 看起来更直接，但它有两个问题：
/// ① 任务栏的窗口被藏了，可它向系统申请的 AppBar 占位还在，屏幕底部会留一条空白；
/// ② 用户按住 Win 键、或者系统重新布局时，任务栏可能又冒出来，状态不受控。
/// </para>
/// <para>
/// 正确做法是用 Shell 自己的开关：<c>SHAppBarMessage(ABM_SETSTATE)</c> + <c>ABS_AUTOHIDE</c>，
/// 这正是"任务栏设置 → 自动隐藏任务栏"复选框背后调用的东西。它的好处是：
/// 状态由系统维护、会把占位一起收掉、而且我们能随时读回来并恢复原状。
/// </para>
/// </summary>
public static class SystemTaskbar
{
    // ---- SHAppBarMessage 的消息号 ----
    private const uint ABM_GETSTATE = 0x04;
    private const uint ABM_SETSTATE = 0x0A;

    // ---- 任务栏状态位（ABM_GETSTATE 返回值 / ABM_SETSTATE 的 lParam）----
    private const int ABS_AUTOHIDE = 0x0001;
    private const int ABS_ALWAYSONTOP = 0x0002;

    /// <summary>程序启动时任务栏的原始状态（用于退出时恢复，不覆盖用户自己的设置）。</summary>
    private static int? _originalState;

    /// <summary>当前是否已被本程序打开了自动隐藏。</summary>
    public static bool IsAutoHideEnabled => (GetState() & ABS_AUTOHIDE) != 0;

    /// <summary>
    /// 打开/关闭系统任务栏的自动隐藏。
    /// 第一次调用时会把原始状态记下来，<see cref="Restore"/> 用它恢复。
    /// </summary>
    public static bool SetAutoHide(bool enabled)
    {
        var tray = FindTaskbar();
        if (tray == IntPtr.Zero)
        {
            AppLog.Warn("找不到系统任务栏窗口（Shell_TrayWnd），无法切换自动隐藏");
            return false;
        }

        _originalState ??= GetState();

        if (enabled)
        {
            WriteState(tray, ABS_AUTOHIDE);
            AppLog.Info("已开启系统任务栏自动隐藏（鼠标移到屏幕边缘才会出现）");
        }
        else
        {
            // 关掉 = 回到本程序改动之前的样子
            Restore();
        }

        return enabled == IsAutoHideEnabled;
    }

    /// <summary>
    /// 把任务栏恢复到本程序启动前的状态。退出路径上必须调用。
    /// <para>
    /// 这里**原值原样写回**，不做"猜一个合理值"的转换 ——
    /// 实测任务栏状态可能是 0x0（既非自动隐藏也非常显示），
    /// 如果我们自作聪明地写 ABS_ALWAYSONTOP，反而会改变用户原有的设置。
    /// </para>
    /// </summary>
    public static void Restore()
    {
        if (_originalState is not { } original)
        {
            return; // 从没改过，什么都不用做
        }

        var tray = FindTaskbar();
        if (tray == IntPtr.Zero)
        {
            return;
        }

        WriteState(tray, original);
        _originalState = null;
        AppLog.Info("系统任务栏状态已恢复");
    }

    private static void WriteState(IntPtr tray, int state)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = tray,
            lParam = (IntPtr)state,
        };

        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    private static int GetState()
    {
        try
        {
            var tray = FindTaskbar();
            if (tray == IntPtr.Zero)
            {
                return 0;
            }

            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = tray,
            };

            return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>主屏任务栏（Shell_TrayWnd）；副屏任务栏是 Shell_SecondaryTrayWnd。</summary>
    private static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
}
