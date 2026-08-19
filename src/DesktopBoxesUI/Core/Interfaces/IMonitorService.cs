using System.Collections.Generic;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Reports monitor layout and work areas. Implemented with Win32 Monitor APIs.
/// Uses Core geometry types so the rest of the app is monitor-API agnostic.
/// </summary>
public interface IMonitorService
{
    RectD GetPrimaryWorkArea();

    IReadOnlyList<RectD> GetMonitorWorkAreas();

    RectD GetWorkAreaContaining(PointD point);
}
