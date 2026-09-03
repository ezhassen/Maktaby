using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Transparent, click-through, topmost overlay used to draw snapping guide lines while a Box is
/// being moved or resized. Coordinates fed in are physical device pixels; <see cref="SetGuides"/>
/// converts them to DIPs using the scale supplied by the caller (96 / overlay DPI).
/// </summary>
public sealed partial class SnapOverlay : Window
{
    private const double LineThickness = 2;

    public SnapOverlay()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    /// <summary>
    /// Draws the supplied guide lines (absolute physical pixels) at the given DIP scale, relative to
    /// the overlay's own origin (the monitor's top-left in physical pixels) so they land on screen.
    /// </summary>
    public void SetGuides(IEnumerable<GuideLine> guides, double dipScale, double originX, double originY)
    {
        GuideCanvas.Children.Clear();
        foreach (var g in guides)
        {
            var line = new Line
            {
                Stroke = Brushes.Cyan,
                StrokeThickness = LineThickness,
            };

            if (g.IsVertical)
            {
                double x = (g.Position - originX) * dipScale;
                line.X1 = x;
                line.X2 = x;
                line.Y1 = (g.SpanStart - originY) * dipScale;
                line.Y2 = (g.SpanEnd - originY) * dipScale;
            }
            else
            {
                double y = (g.Position - originY) * dipScale;
                line.Y1 = y;
                line.Y2 = y;
                line.X1 = (g.SpanStart - originX) * dipScale;
                line.X2 = (g.SpanEnd - originX) * dipScale;
            }

            GuideCanvas.Children.Add(line);
        }

        Visibility = Visibility.Visible;
    }

    /// <summary>Clears the guide lines and hides the overlay.</summary>
    public void HideGuides()
    {
        GuideCanvas.Children.Clear();
        Visibility = Visibility.Collapsed;
    }
}
