using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace WPFShared.Win32APIs;

[SupportedOSPlatform("windows10.0.14393")]
internal static class Win32Apis
{
    /// <summary>
    /// Returns the work-area rectangle (physical screen pixels) of the monitor that contains
    /// <paramref name="hwnd"/>, or <see langword="null"/> if it cannot be determined. Used to place
    /// dialog/surface windows on the same monitor as the window that triggered them.
    /// </summary>
    public static (int left, int top, int right, int bottom)? GetMonitorWorkArea(IntPtr hwnd)
    {
        var hmon = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (hmon == HWND.Null)
        {
            return null;
        }

        MONITORINFO mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!PInvoke.GetMonitorInfo(hmon, ref mi))
        {
            return null;
        }

        return (mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
    }
    public static uint GetDpiForWindow(HWND hWnd) => PInvoke.GetDpiForWindow(hWnd);

}
