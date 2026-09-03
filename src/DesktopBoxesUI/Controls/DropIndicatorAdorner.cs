using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopBoxesUI.Controls;

/// <summary>
/// Draws a vertical drop indicator (a vertical line with a marker) at a position inside the
/// adorned element's coordinate space, used to show where a dragged item will be inserted.
/// </summary>
internal sealed class DropIndicatorAdorner : Adorner
{
    public double LineX { get; set; }

    public double LineY { get; set; }

    public double LineHeight { get; set; }

    public DropIndicatorAdorner(UIElement adorned)
        : base(adorned)
    {
    }

    public double GapX { get; set; } = double.NaN;
    public double GapY { get; set; }
    public double GapW { get; set; }
    public double GapH { get; set; }
    public bool ShowGap { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0, 120, 215));
        brush.Freeze();

        if (ShowGap && !double.IsNaN(GapX))
        {
            var gapPen = new Pen(brush, 1.5) { DashStyle = DashStyles.Dash };
            gapPen.Freeze();
            var rect = new Rect(GapX, GapY, GapW, GapH);
            dc.DrawRoundedRectangle(null, gapPen, rect, 4, 4);
            var fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 215));
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, rect, 4, 4);
            // Also draw thin insertion line at left edge of gap for precision
            var linePen = new Pen(brush, 2);
            linePen.Freeze();
            dc.DrawLine(linePen, new Point(GapX, GapY), new Point(GapX, GapY + GapH));
        }

        double x = LineX;
        double y = LineY;
        double h = LineHeight;
        if (h < 10)
        {
            h = 10;
        }

        // If gap is shown, line already drawn at gap edge; still draw main line if not overlapping gap
        if (!ShowGap || Math.Abs(x - GapX) > 2)
        {
            var pen = new Pen(brush, 3);
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, y), new Point(x, y + h));
            dc.DrawEllipse(brush, null, new Point(x, y), 3.5, 3.5);
            dc.DrawEllipse(brush, null, new Point(x, y + h), 3.5, 3.5);
        }
    }

    public void Update(double x, double y, double h)
    {
        LineX = x;
        LineY = y;
        LineHeight = h;
        ShowGap = false;
        InvalidateVisual();
    }

    public void UpdateGap(double x, double y, double w, double h)
    {
        GapX = x;
        GapY = y;
        GapW = w;
        GapH = h;
        ShowGap = true;
        InvalidateVisual();
    }

    public void ClearGap()
    {
        ShowGap = false;
        GapX = double.NaN;
        InvalidateVisual();
    }
}
