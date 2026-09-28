using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Win32.Services;

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
        IntPtr hmon = Win32Apis.MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
        MONITORINFO info = new();
        info.Size = (uint)Marshal.SizeOf<MONITORINFO>();

        if (Win32Apis.GetMonitorInfo(hmon, ref info))
        {
            RECT r = info.Monitor;
            return RectD.FromXYWH(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        return RectD.FromXYWH(0, 0, 1920, 1080);
    }
}
