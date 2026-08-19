using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// DPI awareness helpers. Implemented with Win32 DPI APIs. Event-driven so consumers
/// can react to per-monitor DPI changes without polling.
/// </summary>
public interface IDpiService
{
    double GetSystemDpi();

    double GetDpiForWindow(nint hwnd);

    /// <summary>Raised when the DPI of a monitored window changes (per-monitor v2).</summary>
    event System.EventHandler<DpiChangedEventArgs>? DpiChanged;
}
