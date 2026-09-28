namespace Maktaby.Core.Models;

/// <summary>
/// Event payload describing a per-monitor DPI change. Coordinates are already in
/// framework-neutral doubles (DPI-scaled).
/// </summary>
public sealed class DpiChangedEventArgs : System.EventArgs
{
    public DpiChangedEventArgs(double oldDpi, double newDpi)
    {
        OldDpi = oldDpi;
        NewDpi = newDpi;
    }

    public double OldDpi { get; }
    public double NewDpi { get; }
}
