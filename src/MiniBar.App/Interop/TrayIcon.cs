using System.Runtime.InteropServices;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

/// <summary>
/// 系统托盘（通知区域）图标。
///
/// <para><b>原理</b>：Windows 的托盘图标是 <c>Shell_NotifyIcon</c> 这个 Shell API 管理的 ——
/// 它不是一个"窗口"，而是挂在某个窗口名下的一枚图标（所以必须给它一个 HWND，
/// 用来接收鼠标消息）。图标用 <c>ExtractIconEx</c> 从 exe 本身里取（我们给 exe 设了
/// ApplicationIcon），这样托盘和资源管理器里的图标永远一致，也省掉自己画 HICON 的麻烦。</para>
///
/// <para><b>交互约定</b>（由 BarWindow 处理鼠标消息后决定）：</para>
/// <list type="bullet">
///   <item>左键单击：显示 / 隐藏任务栏（相当于按了一下任务栏按钮）；</item>
///   <item>左键双击：打开设置窗口；</item>
///   <item>右键单击：弹出主菜单（含退出）。</item>
/// </list>
///
/// <para><b>生命周期</b>：跟任务栏窗口绑定 —— 窗口创建时 <see cref="Show"/>，
/// 关闭/禁用时 <see cref="Hide"/>。进程被强杀时托盘图标会"残留"到下一次鼠标划过，
/// 所以退出路径上也要尽力 <c>NIM_DELETE</c>（见 App 的退出兜底）。</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>托盘回调消息号：系统把鼠标消息用这个号发到挂靠窗口的过程里。</summary>
    public const int CallbackMessage = 0x8000 + 0x0A52;

    // 鼠标消息号（WM_ 常量，来自 winuser.h）。BarWindow 收到回调消息后要按它区分左键/右键/双击。
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_LBUTTONDBLCLK = 0x0203;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NIF_SHOWTIP = 0x80;

    private IntPtr _hwnd;
    private IntPtr _smallIcon;
    private bool _added;
    private bool _disposed;

    public bool IsVisible { get; private set; }

    /// <summary>把图标挂到某个窗口名下（这个窗口负责收鼠标消息）。</summary>
    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>显示托盘图标（幂等）。</summary>
    public void Show(string tooltip = "MiniBar")
    {
        if (_disposed || _hwnd == IntPtr.Zero || _added)
        {
            return;
        }

        _smallIcon = LoadSmallIcon();

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _smallIcon,
            szTip = tooltip,
        };

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        IsVisible = _added;
        AppLog.Info(_added ? "已显示系统托盘图标" : "系统托盘图标创建失败");
    }

    /// <summary>隐藏托盘图标并释放图标句柄（幂等）。</summary>
    public void Hide()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
            };

            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_smallIcon != IntPtr.Zero)
        {
            DestroyIcon(_smallIcon);
            _smallIcon = IntPtr.Zero;
        }

        IsVisible = false;
    }

    /// <summary>更新悬停提示文字。</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tooltip,
        };

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>弹一条气泡通知（就是托盘图标旁那个小气泡）。</summary>
    public void ShowBalloon(string title, string text, bool warning = false)
    {
        if (!_added)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfo = text,
            szInfoTitle = title,
            dwInfoFlags = warning ? 2u : 1u, // 2 = 警告图标，1 = 信息图标
        };

        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>从 exe 里取小尺寸图标（我们拥有返回的句柄，用 DestroyIcon 释放）。</summary>
    private static IntPtr LoadSmallIcon()
    {
        try
        {
            var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exe))
            {
                return IntPtr.Zero;
            }

            ExtractIconEx(exe, 0, out var large, out var small, 1);

            if (large != IntPtr.Zero)
            {
                DestroyIcon(large); // 大图标用不上，马上还掉
            }

            return small;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
    }

    // ---------------------------------------------------------------- 结构体与 P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
