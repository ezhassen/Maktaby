using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Win32.NativeMethods;
using HWND = Windows.Win32.Foundation.HWND;
using RECT = Windows.Win32.Foundation.RECT;
using HMONITOR = Windows.Win32.Graphics.Gdi.HMONITOR;
using MONITORINFO = Windows.Win32.Graphics.Gdi.MONITORINFO;
using MONITOR_FROM_FLAGS = Windows.Win32.Graphics.Gdi.MONITOR_FROM_FLAGS;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IDesktopWindowService"/>. Reports the desktop/shell
/// window handles and the desktop bounds. More accurate Progman/WorkerW discovery will be
/// layered on later without changing this contract.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopWindowService : IDesktopWindowService
{
    public nint GetDesktopWindowHandle()
        => Win32Apis.GetDesktopWindow();

    public nint GetShellWindowHandle()
        => Win32Apis.GetShellWindow();

    public RectD GetDesktopBounds()
    {
        HMONITOR hmon = Win32Apis.MonitorFromWindow((HWND)(IntPtr)0, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO info = new();
        info.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();

        if (Win32Apis.GetMonitorInfo(hmon, ref info))
        {
            RECT r = info.rcMonitor;
            return RectD.FromXYWH(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }

        return RectD.FromXYWH(0, 0, 1920, 1080);
    }
}
