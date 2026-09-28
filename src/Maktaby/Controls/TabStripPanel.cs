using System.Windows;
using System.Windows.Controls;

namespace Maktaby.Controls;

/// <summary>
/// Uniform tab strip that fills available width and reserves a small gap at <see cref="GapIndex"/>.
/// When a tab is dragged its container is <c>Visibility.Collapsed</c> so remaining tabs snap to fill
/// its space (Chrome-like). The gap is a fixed <see cref="GapWidth"/> inserted at <see cref="GapIndex"/>
/// (0..visibleCount, where visibleCount == far-right). Remaining width is distributed uniformly among
/// visible tabs so nothing overflows the window.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly DependencyProperty GapIndexProperty =
        DependencyProperty.Register(nameof(GapIndex), typeof(int), typeof(TabStripPanel),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty GapWidthProperty =
        DependencyProperty.Register(nameof(GapWidth), typeof(double), typeof(TabStripPanel),
            new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Insertion index among visible tabs (excluding collapsed dragged tab). -1 = no gap.</summary>
    public int GapIndex
    {
        get => (int)GetValue(GapIndexProperty);
        set => SetValue(GapIndexProperty, value);
    }

    public double GapWidth
    {
        get => (double)GetValue(GapWidthProperty);
        set => SetValue(GapWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int visible = 0;
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed) visible++;

        if (visible == 0) return new Size(0, 0);

        double availableW = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double gap = GapIndex >= 0 && GapIndex <= visible ? GapWidth : 0;
        double tabW = (availableW - gap) / visible;
        if (tabW < 0) tabW = 0;

        var childSize = new Size(tabW, availableSize.Height);
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(childSize);
        }

        // Desired height is max child desired height; width is availableW (stretch)
        double desiredH = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            desiredH = System.Math.Max(desiredH, child.DesiredSize.Height);
        }
        return new Size(availableW, desiredH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int visible = 0;
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed) visible++;

        if (visible == 0) return finalSize;

        double gap = GapIndex >= 0 && GapIndex <= visible ? GapWidth : 0;
        double tabW = (finalSize.Width - gap) / visible;
        if (tabW < 0) tabW = 0;

        double x = 0;
        int visibleIdx = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            if (GapIndex >= 0 && visibleIdx == GapIndex)
                x += GapWidth;

            child.Arrange(new Rect(x, 0, tabW, finalSize.Height));
            x += tabW;
            visibleIdx++;
        }
        // Gap at far-right (GapIndex == visible) is trailing empty space already accounted for by narrower tabW.
        return finalSize;
    }
}
