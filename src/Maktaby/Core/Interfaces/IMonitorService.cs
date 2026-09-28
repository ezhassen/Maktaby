using System.Collections.Generic;
using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Reports monitor layout and work areas. Implemented with Win32 Monitor APIs.
/// Uses Core geometry types so the rest of the app is monitor-API agnostic.
/// </summary>
public interface IMonitorService
{
    RectD GetPrimaryWorkArea();

    IReadOnlyList<RectD> GetMonitorWorkAreas();

    /// <summary>All connected monitors with physical bounds, work areas and per-monitor DPI.</summary>
    IReadOnlyList<MonitorInfo> GetAllMonitors();

    RectD GetWorkAreaContaining(PointD point);
}
