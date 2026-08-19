namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// Simple, framework-agnostic geometry types used by the Core layer so that it does
/// not depend on WPF or Win32 coordinate types. Platform layers convert to/from these.
/// </summary>
public readonly record struct PointD(double X, double Y);

public readonly record struct SizeD(double Width, double Height);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static RectD FromXYWH(double x, double y, double width, double height) => new(x, y, width, height);
}

/// <summary>Which edge(s) of a window are being dragged during a resize.</summary>
public enum ResizeEdge
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>A guide line drawn while snapping. Coordinates are in physical device pixels.</summary>
public readonly struct GuideLine
{
    public bool IsVertical { get; }
    public double Position { get; }
    public double SpanStart { get; }
    public double SpanEnd { get; }

    public GuideLine(bool isVertical, double position, double spanStart, double spanEnd)
    {
        IsVertical = isVertical;
        Position = position;
        SpanStart = spanStart;
        SpanEnd = spanEnd;
    }
}

/// <summary>Outcome of a snap computation: the adjusted rect plus the guide lines to render.</summary>
public readonly struct SnapResult
{
    public RectD Rect { get; }
    public IReadOnlyList<GuideLine> Guides { get; }

    public SnapResult(RectD rect, IReadOnlyList<GuideLine> guides)
    {
        Rect = rect;
        Guides = guides;
    }
}
