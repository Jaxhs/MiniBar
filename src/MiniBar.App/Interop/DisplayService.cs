using System.Collections.Generic;
using System.Windows;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Interop;

/// <summary>一块显示器的物理像素信息。</summary>
public sealed record DisplayInfo(IntPtr Handle, int Index, bool IsPrimary, Rect Bounds, Rect WorkArea)
{
    public string DeviceName { get; init; } = string.Empty;
}

/// <summary>
/// 显示器与坐标系工具。刻意不使用 System.Windows.Forms.Screen，
/// 只用 EnumDisplayMonitors，省掉整个 WinForms 程序集（约数 MB 内存）。
/// 所有坐标均为物理像素，与 WPF 的 DIP 换算由调用方按窗口 DPI 处理。
/// </summary>
public static class DisplayService
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        var index = 0;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            if (NativeMethods.TryGetMonitorInfo(hMonitor, out var mi))
            {
                list.Add(new DisplayInfo(
                    hMonitor,
                    index++,
                    (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height),
                    new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height))
                {
                    DeviceName = mi.szDevice ?? string.Empty,
                });
            }

            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            var fallback = new Rect(0, 0,
                (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
            list.Add(new DisplayInfo(IntPtr.Zero, 0, true, fallback, fallback));
        }

        return list;
    }

    public static DisplayInfo GetPrimary()
    {
        foreach (var display in GetDisplays())
        {
            if (display.IsPrimary)
            {
                return display;
            }
        }

        return GetDisplays()[0];
    }

    /// <summary>按设置里的序号取显示器（-1 或越界则返回主显示器）。</summary>
    public static DisplayInfo GetByIndex(int index)
    {
        if (index < 0)
        {
            return GetPrimary();
        }

        var displays = GetDisplays();
        return index < displays.Count ? displays[index] : GetPrimary();
    }

    public static DisplayInfo? FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(monitor);
    }

    public static DisplayInfo? FromHandle(IntPtr hMonitor)
    {
        if (!NativeMethods.TryGetMonitorInfo(hMonitor, out var mi))
        {
            return null;
        }

        var index = 0;
        foreach (var display in GetDisplays())
        {
            if (display.Handle == hMonitor)
            {
                index = display.Index;
                break;
            }
        }

        return new DisplayInfo(hMonitor, index, (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height),
            new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height))
        {
            DeviceName = mi.szDevice ?? string.Empty,
        };
    }

    public static DisplayInfo? FromPixel(int x, int y)
    {
        var pt = new NativeMethods.POINT { X = x, Y = y };
        var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(monitor);
    }

    public static Point GetCursorPositionPixels() =>
        NativeMethods.GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : new Point(0, 0);

    /// <summary>窗口所在显示器的缩放系数（1.0 = 96 DPI）。</summary>
    public static double GetScale(IntPtr hwnd)
    {
        var dpi = hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(hwnd) : 96;
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>以像素为单位给窗口定位（绕过 WPF 的 DIP 换算，PerMonitorV2 下最稳）。</summary>
    public static void PlaceWindow(IntPtr hwnd, Rect pixelRect, bool topmost = true)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero,
            (int)Math.Round(pixelRect.Left),
            (int)Math.Round(pixelRect.Top),
            (int)Math.Round(pixelRect.Width),
            (int)Math.Round(pixelRect.Height),
            NativeMethods.SWP_NOACTIVATE | (topmost ? 0u : NativeMethods.SWP_NOOWNERZORDER));

        AppLog.Debug($"PlaceWindow -> {pixelRect}");
    }

    /// <summary>只移动位置，不改尺寸（像素）。WPF 自己算尺寸，避免与 SizeToContent 打架。</summary>
    public static void MoveWindow(IntPtr hwnd, double leftPx, double topPx, bool topmost = true)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            hwnd,
            topmost ? NativeMethods.HWND_TOPMOST : IntPtr.Zero,
            (int)Math.Round(leftPx),
            (int)Math.Round(topPx),
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
            (topmost ? 0u : NativeMethods.SWP_NOOWNERZORDER));
    }

    /// <summary>当前窗口在屏幕上的像素矩形。</summary>
    public static Rect GetWindowRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return Rect.Empty;
        }

        return new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>把窗口贴到工作区指定边缘（像素）。</summary>
    public static Rect ComputeEdgeRect(DisplayInfo display, DockEdge edge, Size sizePixels, double marginPixels)
    {
        var work = display.WorkArea;
        double left;
        double top;

        switch (edge)
        {
            case DockEdge.Top:
                left = work.Left + (work.Width - sizePixels.Width) / 2;
                top = work.Top + marginPixels;
                break;
            case DockEdge.Left:
                left = work.Left + marginPixels;
                top = work.Top + (work.Height - sizePixels.Height) / 2;
                break;
            case DockEdge.Right:
                left = work.Right - sizePixels.Width - marginPixels;
                top = work.Top + (work.Height - sizePixels.Height) / 2;
                break;
            default:
                left = work.Left + (work.Width - sizePixels.Width) / 2;
                top = work.Bottom - sizePixels.Height - marginPixels;
                break;
        }

        left = Math.Max(work.Left, Math.Min(left, work.Right - sizePixels.Width));
        top = Math.Max(work.Top, Math.Min(top, work.Bottom - sizePixels.Height));
        return new Rect(left, top, sizePixels.Width, sizePixels.Height);
    }

    /// <summary>计算锚定到某个角的矩形（像素）。</summary>
    public static Rect ComputeAnchorRect(DisplayInfo display, AnchorCorner corner, Size sizePixels, double marginPixels)
    {
        var work = display.WorkArea;
        double left = corner switch
        {
            AnchorCorner.TopLeft or AnchorCorner.BottomLeft => work.Left + marginPixels,
            AnchorCorner.TopRight or AnchorCorner.BottomRight => work.Right - sizePixels.Width - marginPixels,
            _ => work.Left + (work.Width - sizePixels.Width) / 2,
        };

        double top = corner switch
        {
            AnchorCorner.TopLeft or AnchorCorner.TopCenter or AnchorCorner.TopRight => work.Top + marginPixels,
            _ => work.Bottom - sizePixels.Height - marginPixels,
        };

        return new Rect(left, top, sizePixels.Width, sizePixels.Height);
    }
}
