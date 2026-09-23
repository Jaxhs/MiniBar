using System.Windows;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

/// <summary>
/// 把窗口注册成系统 AppBar（Shell 提供的机制，任务栏本身就是这么实现的）。
///
/// 注册之后 Windows 会把屏幕工作区让出一条给本程序 —— 也就是说：
///   · 其它窗口“最大化”时**不会**盖住它（最大化是贴到工作区，而不是整个屏幕）；
///   · 这是操作系统级的行为，不需要靠 TOPMOST 硬顶，也不会和别的置顶窗口打架。
///
/// 三条必须遵守的规则：
///   1) ABN_POSCHANGED 时必须重新 ABM_SETPOS（分辨率变化、别的 AppBar 出现都要重排）；
///   2) 隐藏任务栏 / 退出程序前必须 ABM_REMOVE，否则工作区会永久留一条空位；
///   3) 回调消息由系统发到窗口过程，必须挂 HWND 钩子接住。
/// </summary>
public sealed class AppBarService : IDisposable
{
    /// <summary>自定义回调消息（WM_APP 区段），系统用它把 ABN_* 通知发回来。</summary>
    public const int CallbackMessage = 0x8000 + 0x0A51;

    private const uint ABM_NEW = 0x00;
    private const uint ABM_REMOVE = 0x01;
    private const uint ABM_QUERYPOS = 0x02;
    private const uint ABM_SETPOS = 0x03;

    private const int ABN_STATECHANGE = 0x00;
    private const int ABN_POSCHANGED = 0x01;
    private const int ABN_FULLSCREENAPP = 0x02;
    private const int ABN_WINDOWARRANGE = 0x03;

    /// <summary>
    /// 本程序窗口的原生句柄（HWND）。<see cref="IntPtr"/> 在 64 位下是 8 字节，专门装这种"窗口身份证号"。
    /// WPF 的 <c>Window</c> 是托管对象，拿不到原生句柄；要先通过
    /// <c>new WindowInteropHelper(window).Handle</c> 把托管窗口"钉"出一个 HWND，再 <see cref="Attach"/> 进来。
    /// 值为 <see cref="IntPtr.Zero"/> 表示还没拿到句柄（窗口还没真正创建）。
    /// </summary>
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>系统要求重新安排位置（分辨率变化、别的 AppBar 加入/退出、任务栏移动等）。</summary>
    public event EventHandler? PositionChanged;

    /// <summary>系统告诉我们有全屏程序开始/结束（true = 开始）。</summary>
    public event EventHandler<bool>? FullscreenAppChanged;

    public bool IsRegistered { get; private set; }

    /// <summary>是否已经拿到窗口句柄（诊断用）。</summary>
    public bool HasHandle => _hwnd != IntPtr.Zero;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>注册为 AppBar。必须已经有窗口句柄。</summary>
    public bool Register()
    {
        if (_disposed || _hwnd == IntPtr.Zero || IsRegistered)
        {
            return IsRegistered;
        }

        var data = new APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = CallbackMessage,
        };

        var result = SHAppBarMessage(ABM_NEW, ref data);
        IsRegistered = result != 0;
        AppLog.Info(IsRegistered ? "已注册为系统 AppBar（占用屏幕边缘空间）" : "AppBar 注册失败，退回普通置顶窗口");
        return IsRegistered;
    }

    /// <summary>
    /// 注销 AppBar，把占用的屏幕空间还给系统。
    /// <para>
    /// <b>为什么这个调用一定要发生？</b>只要注册过 AppBar，系统就给本程序让出一条屏幕边。
    /// 如果程序"没打招呼就消失"（隐藏、进迷你模式、正常退出，乃至被 <c>taskkill /F</c> 强杀），
    /// 那条占位不会被自动回收，屏幕边缘会永久留一条空白/错位。所以<b>凡是离开 AppBar 状态都必须走
    /// <see cref="Unregister"/>（ABM_REMOVE）</b>。强杀场景下进程来不及执行清理，因此需要宿主在
    /// <c>ProcessExit</c> / <c>UnhandledException</c> / <c>SessionEnding</c> 等兜底回调里再尝试归还。
    /// </para>
    /// </summary>
    public void Unregister()
    {
        if (!IsRegistered || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
        };

        SHAppBarMessage(ABM_REMOVE, ref data);
        IsRegistered = false;
        AppLog.Info("已注销 AppBar，屏幕空间已归还");
    }

    /// <summary>
    /// 申请沿指定边缘的一条空间。返回系统实际批给我们的矩形（像素）；
    /// 失败时返回 null，调用方应退回自己的浮动定位逻辑。
    /// </summary>
    public Rect? SetPosition(DockEdge edge, Rect monitorBounds, double thicknessPixels, double marginPixels)
    {
        if (!IsRegistered || _hwnd == IntPtr.Zero)
        {
            return null;
        }

        var size = Math.Max(24, thicknessPixels);
        var margin = Math.Max(0, marginPixels);

        // 先给出"想要"的位置：贴在该显示器边缘的一条
        var desired = edge switch
        {
            DockEdge.Top => new Rect(monitorBounds.Left, monitorBounds.Top, monitorBounds.Width, size + margin),
            DockEdge.Left => new Rect(monitorBounds.Left, monitorBounds.Top, size + margin, monitorBounds.Height),
            DockEdge.Right => new Rect(monitorBounds.Right - size - margin, monitorBounds.Top, size + margin, monitorBounds.Height),
            _ => new Rect(monitorBounds.Left, monitorBounds.Bottom - size - margin, monitorBounds.Width, size + margin),
        };

        var data = new APPBARDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = ToNativeEdge(edge),
            rc = RECT.FromRect(desired),
        };

        // QUERYPOS：让 Shell 把矩形裁到可用区域内（避开任务栏与其它 AppBar）
        SHAppBarMessage(ABM_QUERYPOS, ref data);

        // 裁完之后厚度可能被改小，再按我们的目标厚度贴回选定的边缘
        switch (edge)
        {
            case DockEdge.Top:
                data.rc.Bottom = data.rc.Top + (int)Math.Round(size + margin);
                break;
            case DockEdge.Left:
                data.rc.Right = data.rc.Left + (int)Math.Round(size + margin);
                break;
            case DockEdge.Right:
                data.rc.Left = data.rc.Right - (int)Math.Round(size + margin);
                break;
            default:
                data.rc.Top = data.rc.Bottom - (int)Math.Round(size + margin);
                break;
        }

        // SETPOS：登记占用，返回真正生效的矩形
        SHAppBarMessage(ABM_SETPOS, ref data);

        var granted = data.rc.ToRect();
        AppLog.Debug($"AppBar 位置已登记：{edge} -> {granted}");
        return granted;
    }

    /// <summary>
    /// 窗口过程里调用，处理系统发来的 ABN_* 通知。返回 true 表示已处理。
    /// <para>
    /// <b>新手注意（AppBar 的经典坑）：</b>我们自己调用 <c>ABM_SETPOS</c>（见 <see cref="SetPosition"/>）之后，
    /// Shell 会"回敬"一次 <c>ABN_POSCHANGED</c> 通知。如果收到通知就立刻又去 <c>SetPosition</c>，
    /// 又会触发下一次通知，形成"设位置→被通知→再设位置"的死循环，CPU 会飙高。
    /// 标准做法是：忽略"自己刚设完位置后 1 秒内"的 <c>ABN_POSCHANGED</c>（真正由分辨率变化/别的 AppBar
    /// 引起的通知则照常处理）。本类只负责把通知转发出去，1 秒抑噪的节律由调用方/宿主把控。
    /// </para>
    /// </summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != CallbackMessage)
        {
            return false;
        }

        switch (wParam.ToInt32())
        {
            case ABN_POSCHANGED:
                PositionChanged?.Invoke(this, EventArgs.Empty);
                break;

            case ABN_FULLSCREENAPP:
                var started = lParam != IntPtr.Zero;
                AppLog.Debug($"AppBar 收到全屏程序通知：{(started ? "开始" : "结束")}");
                FullscreenAppChanged?.Invoke(this, started);
                break;

            case ABN_STATECHANGE:
            case ABN_WINDOWARRANGE:
                break;
        }

        return true;
    }

    private static uint ToNativeEdge(DockEdge edge) => edge switch
    {
        DockEdge.Left => 0,   // ABE_LEFT
        DockEdge.Top => 1,    // ABE_TOP
        DockEdge.Right => 2,  // ABE_RIGHT
        _ => 3,               // ABE_BOTTOM
    };

    /// <summary>
    /// 释放资源（实现 <see cref="IDisposable"/>）。
    /// <para>
    /// <b>IDisposable / using 是什么？</b> 当一个对象"占用了需要主动归还的资源"（窗口注册、句柄、计时器……），
    /// 光等垃圾回收不够及时，于是实现 <c>IDisposable</c>，把"清理动作"写在 <c>Dispose</c> 里。
    /// 调用方用 <c>using (var x = ...)</c> 时，离开作用域会自动调用 <c>Dispose</c>，相当于"离开即清理"，
    /// 不用担心忘写。这里的 <c>Dispose</c> 负责 <see cref="Unregister"/>，把 AppBar 占用的屏幕空间还回去。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static RECT FromRect(Rect source) => new()
        {
            Left = (int)Math.Round(source.Left),
            Top = (int)Math.Round(source.Top),
            Right = (int)Math.Round(source.Right),
            Bottom = (int)Math.Round(source.Bottom),
        };

        public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.StdCall)]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
