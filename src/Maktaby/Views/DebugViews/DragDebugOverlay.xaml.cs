using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Maktaby.Views;

/// <summary>
/// Full-screen, click-through, top-most overlay used only for debugging tab drag. It draws in absolute
/// screen (DIP) coordinates so the computed drop-target rectangles and the cursor point can be compared
/// against the real mouse position. Not shown unless <see cref="BoxContainerWindow.DragDebugEnabled"/>.
/// </summary>
public sealed partial class DragDebugOverlay : Window
{
    public DragDebugOverlay()
    {
        InitializeComponent();

        // SystemParameters reports the virtual screen in DIPs — the same coordinate space PointToScreen
        // uses, so the overlay exactly matches the desktop bounds.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public void Clear() => Host.Children.Clear();

    public void DrawRect(double screenDipX, double screenDipY, double w, double h, Brush stroke, string? label = null)
    {
        if (!Finite(screenDipX) || !Finite(screenDipY) || !Finite(w) || !Finite(h))
        {
            return;
        }

        // A degenerate (zero/negative) rectangle is not drawable and would throw; skip it.
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var local = PointFromScreen(new Point(screenDipX, screenDipY));
        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Stroke = stroke,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(rect, local.X);
        Canvas.SetTop(rect, local.Y);
        Host.Children.Add(rect);

        if (label != null)
        {
            var tb = new TextBlock
            {
                Text = label,
                Foreground = stroke,
                FontSize = 11,
                Background = Brushes.Black,
            };
            Canvas.SetLeft(tb, local.X);
            Canvas.SetTop(tb, local.Y);
            Host.Children.Add(tb);
        }
    }

    public void DrawPoint(double screenDipX, double screenDipY, Brush fill, string? label = null)
    {
        if (!Finite(screenDipX) || !Finite(screenDipY))
        {
            return;
        }

        var local = PointFromScreen(new Point(screenDipX, screenDipY));
        var dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = fill,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(dot, local.X - 6);
        Canvas.SetTop(dot, local.Y - 6);
        Host.Children.Add(dot);

        if (label != null)
        {
            var tb = new TextBlock
            {
                Text = label,
                Foreground = fill,
                FontSize = 11,
                Background = Brushes.Black,
            };
            Canvas.SetLeft(tb, local.X + 8);
            Canvas.SetTop(tb, local.Y - 6);
            Host.Children.Add(tb);
        }
    }

    public void DrawText(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.Lime,
            FontSize = 12,
            Background = Brushes.Black,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        };
        Canvas.SetLeft(tb, 12);
        Canvas.SetTop(tb, 12);
        Host.Children.Add(tb);
    }

    public void DrawTextAt(double screenDipX, double screenDipY, string text, Brush fg)
    {
        if (!Finite(screenDipX) || !Finite(screenDipY))
        {
            return;
        }

        var local = PointFromScreen(new Point(screenDipX, screenDipY));
        var tb = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = 11,
            Background = Brushes.Black,
        };
        Canvas.SetLeft(tb, local.X + 10);
        Canvas.SetTop(tb, local.Y + 10);
        Host.Children.Add(tb);
    }

    /// <summary>Full-length crosshair (vertical + horizontal lines) through the given screen-DIP point,
    /// so the exact cursor position can be read against the drawn window/tab rectangles.</summary>
    public void DrawCrosshair(double screenDipX, double screenDipY, Brush stroke)
    {
        if (!Finite(screenDipX) || !Finite(screenDipY))
        {
            return;
        }

        var local = PointFromScreen(new Point(screenDipX, screenDipY));
        double w = Host.ActualWidth;
        double h = Host.ActualHeight;

        Host.Children.Add(new Line
        {
            X1 = local.X,
            Y1 = 0,
            X2 = local.X,
            Y2 = h,
            Stroke = stroke,
            StrokeThickness = 1,
        });
        Host.Children.Add(new Line
        {
            X1 = 0,
            Y1 = local.Y,
            X2 = w,
            Y2 = local.Y,
            Stroke = stroke,
            StrokeThickness = 1,
        });
    }

    private static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}
