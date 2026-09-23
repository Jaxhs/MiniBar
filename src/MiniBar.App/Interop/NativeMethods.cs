using System.Runtime.InteropServices;
using System.Text;

namespace MiniBar.App.Interop;

/// <summary>
/// 本文件是 Win32 API 的"中转站"。新手先弄懂下面几个概念，再看下面的声明就不会懵：
///
/// <b>1. 什么是 P/Invoke？</b>
/// P/Invoke（Platform Invocation）就是"让 C# 直接调用 Windows 系统 DLL 里的 C 函数"。
/// 写法是给一个静态方法贴 <c>[DllImport("user32.dll")]</c>，方法名对应 DLL 里的函数名。
/// 这些函数本来是给 C/C++ 用的，C# 通过它跨过"托管世界"去借操作系统的能力
/// （拿窗口、设坐标、注册热键、读空闲时间……）。本项目几乎所有 Win32 调用都集中在这里。
///
/// <b>2. 为什么必须手动声明返回类型和参数类型？</b>
/// 64 位进程里，窗口句柄（HWND）、指针都是 64 位（8 字节）。如果在 C# 里把返回值
/// 声明成 <c>int</c>（只有 32 位），系统返回的 64 位句柄会被截断成低 32 位而丢失高半部分，
/// 得到的值就错了，后续用这个句柄去操作窗口会失败或操作到别的窗口。所以涉及句柄的函数
/// 一律用 <see cref="IntPtr"/>（它的大小随平台自动是 4 或 8 字节）来接返回值/参数。
///
/// <b>3. <see cref="IntPtr"/> 是什么？</b>
/// 一个"平台相关长度的整数"，专门用来装指针/句柄。32 位程序里是 4 字节，64 位里是 8 字节。
/// 把它当成"Windows 给窗口/进程发的身份证号"即可，不要当普通 int 做算术。
///
/// <b>4. <c>StructLayout</c> 与结构体（RECT / POINT / MONITORINFOEX）</b>
/// C# 的结构体内存布局会被运行时自动优化（字段可能重排），但 C 函数要求"按声明顺序 tightly packed"。
/// <c>[StructLayout(LayoutKind.Sequential)]</c> 就是强制 C# 字段顺序与 C 结构体一一对应，
/// 这样 <c>DllImport</c> 才能把同一块内存既当 C 结构体用、又当 C# 结构体用。
/// <see cref="MarshalAsAttribute"/>（如 <c>ByValTStr</c>、<c>UnmanagedType.Bool</c>）则进一步描述
/// 某个字段在"非托管内存"里该怎么解释（定长字符串、4 字节 BOOL 等）。
///
/// <b>5. <see cref="Marshal.SizeOf{T}()"/></b>
/// 算出"这个结构体在非托管内存里占多少字节"。C 结构体头一个字段通常是 <c>cbSize</c>，
/// 系统要靠它知道自己收到了多大的结构，所以每次传结构体前都要填好 <c>cbSize = Marshal.SizeOf(...)</c>，
/// 漏填或填错，系统调用会直接失败。
/// </summary>
internal static class NativeMethods
{
    // ---- 窗口样式 ----
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_LAYERED = 0x00080000;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    // ---- 窗口消息 ----
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_DISPLAYCHANGE = 0x007E;
    internal const int WM_DPICHANGED = 0x02E0;
    internal const int WM_SETTINGCHANGE = 0x001A;

    // ---- 热键修饰符 ----
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    // ---- 显示器 ----
    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    internal const uint MONITORINFOF_PRIMARY = 0x00000001;

    // ---- DWM ----
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // ---- 用户通知状态 ----
    internal const int QUNS_BUSY = 2;
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;
    internal const int QUNS_QUIET_TIME = 6;

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    /// <summary>
    /// 一个矩形（左/上/右/下四个像素坐标）。这是 Windows API 里描述"一块屏幕区域"的标准结构体，
    /// 注意它用的是<b>物理像素</b>，不是 WPF 的 DIP。本项目里所有坐标换算最终都要落到这种 RECT 上。
    /// Width/Height 是算出来的，不是字段（避免存两份导致不一致）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    /// <summary>屏幕上的一个坐标点（X/Y，物理像素）。配合 <c>MonitorFromPoint</c> 用来判断某个点落在哪块显示器上。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 一块显示器的完整信息（由 <c>GetMonitorInfo</c> 填出来）。
    /// <list type="bullet">
    ///   <item><description><c>rcMonitor</c>：整块屏幕的物理像素范围（含任务栏占用的那条）。</description></item>
    ///   <item><description><c>rcWork</c>：工作区——屏幕范围减去系统任务栏/AppBar 之后的"可用区域"。最大化窗口贴的就是它。</description></item>
    ///   <item><description><c>dwFlags</c>：位标志，<c>MONITORINFOF_PRIMARY</c> 表示这是主显示器。</description></item>
    ///   <item><description><c>szDevice</c>：设备名（如 <c>\\.\DISPLAY1</c>），固定 32 字符的定长字符串（见上面的 <c>MarshalAs</c>）。</description></item>
    /// </list>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>当前前台窗口是否属于本进程（用于判断“失焦”是不是真的离开了本程序）。</summary>
    internal static bool IsOwnProcessForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>
    /// 取某个窗口所在显示器的 DPI（每英寸点数）。标准值是 96 DPI（= 缩放 100%）。
    /// 本项目是 <b>PerMonitorV2 DPI 感知</b>：每块显示器可以有不同缩放（125%/150%…）。
    /// 把 DPI / 96 就得到"缩放系数"。用它能把 WPF 的 DIP 与系统物理像素互相换算
    /// （物理像素 = DIP × 缩放系数），否则高缩放下窗口定位会偏移。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 查询系统"用户通知状态"。本项目用它在轮询之外更快识别两类特殊全屏：
    /// <c>QUNS_RUNNING_D3D_FULL_SCREEN</c>（D3D 独占全屏，比如老游戏的全屏模式）和
    /// <c>QUNS_PRESENTATION_MODE</c>（演示模式）/ <c>QUNS_QUIET_TIME</c>（专注助手）。
    /// 这类全屏不一定有"盖满屏幕的窗口"，纯靠矩形检测会漏，所以必须靠这个函数补一刀。
    /// </summary>
    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int pquns);

    /// <summary>
    /// 设置窗口的 DWM（桌面窗口管理器）外观属性。本项目用它做两件事：
    /// ① 圆角（<c>DWMWA_WINDOW_CORNER_PREFERENCE</c>）；② <b>深色标题栏</b>（<c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>）。
    /// 后者是 Windows 10 20H1+ / Win11 才支持的"沉浸式深色模式"，把标题栏文字与边框变成深色，
    /// 让本程序在浅色/深色主题下都好看。不支持的旧系统调用会失败，这里一律静默忽略。
    /// </summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 用于 <c>GetLastInputInfo</c> 的输入信息结构体。Windows 用它返回一个时间戳：
    /// <c>dwTime</c> 是"系统启动以来，最后一次键盘/鼠标操作发生的时刻"（单位毫秒，由 <c>Environment.TickCount</c> 同源计时）。
    /// 用当前 <c>TickCount</c> 减去它，就得到"用户已经多久没动键鼠了"——这就是"空闲时长"。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>距离上次键鼠输入过去了多久（用于"空闲时归还内存"）。</summary>
    /// <remarks>
    /// <b>空闲时长</b>的用法：久坐提醒插件、空闲裁剪工作集（<see cref="TrimWorkingSet"/>）都靠它。
    /// 注意 <c>TickCount</c> 大约 49.7 天会回绕到 0，这里用 <c>unchecked</c> 的减法
    /// （无符号相减）能正确处理回绕，不会出现负数。
    /// </remarks>
    internal static TimeSpan GetIdleTime()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    internal static bool IsPrimaryMonitor(IntPtr hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
        {
            return false;
        }

        return (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
    }

    internal static bool TryGetMonitorInfo(IntPtr hMonitor, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref info);
    }

    internal static string GetWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    internal static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var len = GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    /// <summary>给窗口加上 / 去掉扩展样式位。</summary>
    internal static void SetExStyle(IntPtr hwnd, int bits, bool enable)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLong(hwnd, GWL_EXSTYLE);
        var updated = enable ? current | bits : current & ~bits;
        if (updated != current)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, updated);
        }
    }

    /// <summary>把内存工作集还给系统（任务管理器里的“内存”读数会明显下降）。</summary>
    internal static void TrimWorkingSet()
    {
        try
        {
            var process = GetCurrentProcess();
            EmptyWorkingSet(process);
            SetProcessWorkingSetSize(process, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 忽略
        }
    }
}
