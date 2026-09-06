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
/// Win32 implementation of <see cref="IMonitorService"/> using Monitor APIs.
/// Returns Core geometry types so the rest of the app stays monitor-API agnostic.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
[SupportedOSPlatform("windows10.0.14393")]
public sealed class MonitorService : IMonitorService
{
    public RectD GetPrimaryWorkArea()
        => GetWorkArea(Win32Apis.MonitorFromWindow((HWND)(IntPtr)0, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY));

    public IReadOnlyList<RectD> GetMonitorWorkAreas()
        => GetAllMonitors().Select(m => m.WorkArea).ToList();

    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            Win32Apis.EnumDisplayMonitors((IntPtr hMonitor, IntPtr hdc, ref Windows.Win32.Foundation.RECT rect, IntPtr data) =>
            {
                try
                {
                    var info = new ManualApis.MONITORINFOEX
                    {
                        cbSize = (uint)Marshal.SizeOf<ManualApis.MONITORINFOEX>()
                    };
                    if (!Win32Apis.GetMonitorInfoEx(hMonitor, ref info))
                    {
                        return true;
                    }

                    Win32Apis.TryGetDpiForMonitor(hMonitor, out uint dpiX, out uint dpiY);
                    list.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice ?? string.Empty,
                        Bounds = RectD.FromXYWH(info.rcMonitor.left, info.rcMonitor.top,
                            info.rcMonitor.right - info.rcMonitor.left, info.rcMonitor.bottom - info.rcMonitor.top),
                        WorkArea = RectD.FromXYWH(info.rcWork.left, info.rcWork.top,
                            info.rcWork.right - info.rcWork.left, info.rcWork.bottom - info.rcWork.top),
                        IsPrimary = (info.dwFlags & ManualApis.MONITORINFOF_PRIMARY) != 0,
                        DpiX = dpiX,
                        DpiY = dpiY,
                    });
                }
                catch { }
                return true;
            });
        }
        catch { }
        if (list.Count == 0)
        {
            var wa = GetPrimaryWorkArea();
            list.Add(new MonitorInfo
            {
                DeviceName = "PRIMARY",
                Bounds = wa,
                WorkArea = wa,
                IsPrimary = true,
            });
        }
        return list;
    }

    public RectD GetWorkAreaContaining(PointD point)
    {
        RECT rect = new()
        {
            left = (int)point.X,
            top = (int)point.Y,
            right = (int)point.X,
            bottom = (int)point.Y,
        };

        HMONITOR hmon = Win32Apis.MonitorFromRect(rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        return GetWorkArea(hmon);
    }

    private static RectD GetWorkArea(HMONITOR hmon)
    {
        MONITORINFO info = new();
        info.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();

        if (Win32Apis.GetMonitorInfo(hmon, ref info))
        {
            RECT r = info.rcWork;
            return RectD.FromXYWH(r.left, r.top, r.right - r.left, r.bottom - r.top);
        }

        return RectD.FromXYWH(0, 0, 1920, 1080);
    }
}
