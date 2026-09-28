using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;

namespace Maktaby.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IDpiService"/> using DPI APIs.
/// DPI-change notifications will be wired through WPF's per-monitor v2 support later.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DpiService : IDpiService
{
    public double GetSystemDpi()
        => Win32Apis.GetDpiForSystem();

    public double GetDpiForWindow(nint hwnd)
    {
        // Prefer the monitor's live DPI over the window's associated DPI: GetDpiForWindow follows
        // the window's last processed DPI change and goes stale across a scale switch (e.g. still
        // reporting 120 after the display moved to 96), while the monitor itself always reports
        // current values. Falls back to the window value when the monitor cannot be resolved.
        try
        {
            var hmon = Win32Apis.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero && Win32Apis.TryGetDpiForMonitor(hmon, out uint dpiX, out _))
                return dpiX;
        }
        catch { }
        return Win32Apis.GetDpiForWindow(hwnd);
    }

    public event EventHandler<DpiChangedEventArgs>? DpiChanged;

    private void OnDpiChanged(DpiChangedEventArgs e)
        => DpiChanged?.Invoke(this, e);
}
