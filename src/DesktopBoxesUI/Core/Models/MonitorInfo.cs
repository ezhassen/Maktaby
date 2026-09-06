namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// One physical display monitor. All bounds are in physical screen pixels (the EnumDisplayMonitors
/// space); convert to DIPs with <see cref="DpiX"/>/<see cref="DpiY"/> before positioning WPF windows.
/// </summary>
public sealed class MonitorInfo
{
    /// <summary>Stable device name (\\.\DISPLAY1, ...). Used to track monitors across layouts.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Full monitor bounds in physical pixels.</summary>
    public RectD Bounds { get; set; }

    /// <summary>Work area (excluding taskbar) in physical pixels.</summary>
    public RectD WorkArea { get; set; }

    public bool IsPrimary { get; set; }

    public uint DpiX { get; set; } = 96;

    public uint DpiY { get; set; } = 96;
}
