using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopBoxesUI.Controls;

/// <summary>
/// Card with a clickable header (chevron + <see cref="HeaderContent"/>) that collapses
/// the <see cref="HeaderedContentControl.Content"/> body. Backed by an implicit style in
/// <c>AppStyles.xaml</c>; replaces the hand-rolled per-section collapse code
/// (PerformanceMonitorWindow WebView2 section was the reference).
/// </summary>
public class CollapsibleGroupBox : HeaderedContentControl
{
    static CollapsibleGroupBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CollapsibleGroupBox),
            new FrameworkPropertyMetadata(typeof(CollapsibleGroupBox)));
    }

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(CollapsibleGroupBox),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Alias for <see cref="HeaderedContentControl.Header"/> so consumers write
    /// <c>HeaderContent</c> next to <c>Content</c> (the body).</summary>
    public object? HeaderContent
    {
        get => Header;
        set => Header = value;
    }

    public static readonly DependencyProperty BodyPaddingProperty =
        DependencyProperty.Register(nameof(BodyPadding), typeof(Thickness), typeof(CollapsibleGroupBox),
            new FrameworkPropertyMetadata(new Thickness(0)));

    public Thickness BodyPadding
    {
        get => (Thickness)GetValue(BodyPaddingProperty);
        set => SetValue(BodyPaddingProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Re-attached (not stacked) so template re-applies never double-toggle.
        if (GetTemplateChild("PART_HeaderBorder") is System.Windows.Controls.Border header)
        {
            header.MouseLeftButtonUp -= Header_Toggle;
            header.MouseLeftButtonUp += Header_Toggle;
        }
    }

    private void Header_Toggle(object sender, MouseButtonEventArgs e)
    {
        // Button/chevron clicks mark the event handled themselves — only bare header
        // clicks reach here, so there is never a double toggle.
        if (e.Handled) return;
        IsExpanded = !IsExpanded;
    }
}
