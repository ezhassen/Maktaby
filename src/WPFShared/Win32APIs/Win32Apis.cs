using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindowsNative;

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
        var hmon = User32.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero)
        {
            return null;
        }

        MONITORINFO mi = default;
        mi.Size = (uint)Marshal.SizeOf<MONITORINFO>();
        if (!User32.GetMonitorInfo(hmon, ref mi))
        {
            return null;
        }

        return (mi.Work.Left, mi.Work.Top, mi.Work.Right, mi.Work.Bottom);
    }
    public static uint GetDpiForWindow(IntPtr hWnd) => User32.GetDpiForWindow(hWnd);

}
