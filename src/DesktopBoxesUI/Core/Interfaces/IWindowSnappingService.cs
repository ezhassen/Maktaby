using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Computes window-snap adjustments and guide lines for moving/resizing a Box. Pure geometry:
/// callers pass everything in the same coordinate space (physical device pixels) and get back an
/// adjusted rect plus the guide lines to draw. Screen targets snap exactly; Box targets are padded
/// so the moving Box keeps a gap from the other Box. No Win32 or WPF dependencies.
/// </summary>
public interface IWindowSnappingService
{
    /// <summary>Snap a moving window (keeps its size) against the supplied targets.</summary>
    SnapResult SnapMove(RectD moving, IReadOnlyList<RectD> screenTargets, IReadOnlyList<RectD> boxTargets, double threshold, double padding);

    /// <summary>Snap a resizing window (only the dragged edges move) against the supplied targets.</summary>
    SnapResult SnapResize(RectD moving, ResizeEdge edge, SizeD minSize, IReadOnlyList<RectD> screenTargets, IReadOnlyList<RectD> boxTargets, double threshold, double padding);
}
