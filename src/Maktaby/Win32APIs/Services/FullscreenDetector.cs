using Maktaby.Native;
using Maktaby.Win32.NativeMethods;
using System;
using System.Runtime.Versioning;

namespace Maktaby.Win32.Services;

/// <summary>
/// One-shot check for "is a real full-screen app in front of the user right now", used to
/// avoid stealing focus while they are watching something.
/// </summary>
/// <remarks>
/// A MAXIMIZED window deliberately does not count: it covers the work area but leaves the
/// taskbar, so the user can still see and dismiss a prompt. Only a window covering the whole
/// monitor counts.
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class FullscreenDetector
{
    /// <summary>
    /// Pixels of slack allowed on each edge. Borderless "full screen" apps are usually exact,
    /// but a few sit a pixel or two inside the monitor; being strict would miss them, and
    /// being loose by a taskbar's height would wrongly catch maximized windows.
    /// </summary>
    private const int Tolerance = 2;

    /// <summary>
    /// True when the foreground window belongs to another process and covers its entire
    /// monitor. Fails OPEN (returns false) on any API error: a missed suppression only costs
    /// one prompt appearing, whereas a wrong suppression would hide an update forever.
    /// </summary>
    public static bool IsForegroundFullScreen()
    {
        try
        {
            var hwnd = Win32Apis.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd)) { return false; }

            // Our own windows never count. Without this the prompt we are deciding whether to
            // show (or the Settings window behind it) would suppress the prompt itself.
            Win32Apis.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) { return false; }

            if (!Win32Apis.GetWindowRect(hwnd, out var rect)) { return false; }

            var monitor = Win32Apis.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) { return false; }

            var info = new MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!Win32Apis.GetMonitorInfo(monitor, ref info)) { return false; }

            var m = info.Monitor;
            return rect.Left <= m.Left + Tolerance
                && rect.Top <= m.Top + Tolerance
                && rect.Right >= m.Right - Tolerance
                && rect.Bottom >= m.Bottom - Tolerance;
        }
        catch
        {
            return false;
        }
    }
}
