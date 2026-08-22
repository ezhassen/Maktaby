using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopBoxesUI.Views;

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

    protected override void OnRender(DrawingContext dc)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0, 120, 215));
        brush.Freeze();

        double x = LineX;
        double y = LineY;
        double h = LineHeight;
        if (h < 10)
        {
            h = 10;
        }

        var pen = new Pen(brush, 3);
        pen.Freeze();
        dc.DrawLine(pen, new Point(x, y), new Point(x, y + h));
        dc.DrawEllipse(brush, null, new Point(x, y), 3.5, 3.5);
        dc.DrawEllipse(brush, null, new Point(x, y + h), 3.5, 3.5);
    }

    public void Update(double x, double y, double h)
    {
        LineX = x;
        LineY = y;
        LineHeight = h;
        InvalidateVisual();
    }
}
