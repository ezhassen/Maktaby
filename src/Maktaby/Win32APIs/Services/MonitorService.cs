using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IMonitorService"/> using Monitor APIs.
/// Returns Core geometry types so the rest of the app stays monitor-API agnostic.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
[SupportedOSPlatform("windows10.0.14393")]
public sealed class MonitorService : IMonitorService
{
    public RectD GetPrimaryWorkArea()
        => GetWorkArea(Win32Apis.MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY));

    public IReadOnlyList<RectD> GetMonitorWorkAreas()
        => GetAllMonitors().Select(m => m.WorkArea).ToList();

    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            Win32Apis.EnumDisplayMonitors((IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                try
                {
                    var info = new MONITORINFOEX
                    {
                        Size = (uint)Marshal.SizeOf<MONITORINFOEX>()
                    };
                    if (!Win32Apis.GetMonitorInfoEx(hMonitor, ref info))
                    {
                        return true;
                    }

                    Win32Apis.TryGetDpiForMonitor(hMonitor, out uint dpiX, out uint dpiY);
                    list.Add(new MonitorInfo
                    {
                        DeviceName = info.Device ?? string.Empty,
                        Bounds = RectD.FromXYWH(info.Monitor.Left, info.Monitor.Top,
                            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top),
                        WorkArea = RectD.FromXYWH(info.Work.Left, info.Work.Top,
                            info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
                        IsPrimary = (info.Flags & MONITORINFOF_PRIMARY) != 0,
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
        RECT rect = new((int)point.X, (int)point.Y, (int)point.X, (int)point.Y);

        IntPtr hmon = Win32Apis.MonitorFromRect(rect, MONITOR_DEFAULTTOPRIMARY);
        return GetWorkArea(hmon);
    }

    private static RectD GetWorkArea(IntPtr hmon)
    {
        MONITORINFO info = new();
        info.Size = (uint)Marshal.SizeOf<MONITORINFO>();

        if (Win32Apis.GetMonitorInfo(hmon, ref info))
        {
            RECT r = info.Work;
            return RectD.FromXYWH(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        return RectD.FromXYWH(0, 0, 1920, 1080);
    }
}
