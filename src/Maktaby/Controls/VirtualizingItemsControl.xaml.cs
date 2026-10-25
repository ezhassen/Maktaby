using System.Windows;
using System.Windows.Controls;

namespace Maktaby.Controls;

/// <summary>
/// A ready-to-use ItemsControl that hosts a <see cref="VirtualizingIconPanel"/> inside its own
/// ScrollViewer. Do NOT wrap this control in an outer ScrollViewer: the panel implements
/// IScrollInfo and must be the ScrollViewer's direct content (via the template above) so the
/// scrollbar reflects the virtualized extent.
/// </summary>
public partial class VirtualizingItemsControl : UserControl
{
    public VirtualizingItemsControl()
    {
        InitializeComponent();
        PART_ItemsControl.ApplyTemplate();
        ScrollViewerHost = PART_ItemsControl.Template?.FindName("PART_ScrollViewer", PART_ItemsControl) as ScrollViewer;
        Loaded += (_, _) =>
        {
            if (ScrollViewerHost is null)
            {
                PART_ItemsControl.ApplyTemplate();
                ScrollViewerHost = PART_ItemsControl.Template?.FindName("PART_ScrollViewer", PART_ItemsControl) as ScrollViewer;
            }
        };
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(System.Collections.IEnumerable), typeof(VirtualizingItemsControl),
            new PropertyMetadata(null, (d, e) => ((VirtualizingItemsControl)d).PART_ItemsControl.ItemsSource = (System.Collections.IEnumerable?)e.NewValue));

    public System.Collections.IEnumerable? ItemsSource
    {
        get => (System.Collections.IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(VirtualizingItemsControl),
            new PropertyMetadata(null, (d, e) => ((VirtualizingItemsControl)d).PART_ItemsControl.ItemTemplate = (DataTemplate?)e.NewValue));

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingItemsControl), new FrameworkPropertyMetadata(96.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingItemsControl), new FrameworkPropertyMetadata(96.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(VirtualizingItemsControl), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(VirtualizingItemsControl), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public static readonly DependencyProperty HorizontalScrollBarVisibilityProperty =
        DependencyProperty.Register(nameof(HorizontalScrollBarVisibility), typeof(ScrollBarVisibility), typeof(VirtualizingItemsControl), new PropertyMetadata(ScrollBarVisibility.Disabled));

    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => (ScrollBarVisibility)GetValue(HorizontalScrollBarVisibilityProperty);
        set => SetValue(HorizontalScrollBarVisibilityProperty, value);
    }

    public static readonly DependencyProperty VerticalScrollBarVisibilityProperty =
        DependencyProperty.Register(nameof(VerticalScrollBarVisibility), typeof(ScrollBarVisibility), typeof(VirtualizingItemsControl), new PropertyMetadata(ScrollBarVisibility.Auto));

    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => (ScrollBarVisibility)GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    /// <summary>The host ScrollViewer (available after the template is applied).</summary>
    public ScrollViewer? ScrollViewerHost { get; private set; }

    /// <summary>The inner ItemsControl that owns item generation and containers.</summary>
    public ItemsControl ItemsControl => PART_ItemsControl;
}