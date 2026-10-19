using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Maktaby.Helpers;

/// <summary>
/// Attached property that puts an element's tooltip under the guard's control.
/// </summary>
/// <remarks>
/// <para>
/// A WPF <see cref="ToolTip"/> is a <see cref="Popup"/> in its own HWND, and the service closes it
/// from mouse-move/mouse-leave on the element that owns it. Our containers are desktop-band tool
/// windows that get activated, z-order pinned and re-shown constantly while the pointer often does
/// not move at all, and two things here break the service's assumption that it owns the popup's
/// lifetime:
/// </para>
/// <list type="bullet">
/// <item>Layout moves elements under a stationary pointer (a filter pass, an icon reload, the chrome
/// toggling), so a tooltip opens for an element the user never moved onto, and that popup belongs to
/// an owner nothing is tracking any more.</item>
/// <item>The owner window is deactivated or re-pinned while the popup is up, so the mouse-leave that
/// would have closed it never arrives - it survives until the windows are hidden and shown again.</item>
/// </list>
///
/// <para>
/// Setting <c>Managed="True"</c> fixes both without any per-element code:
/// </para>
/// <list type="number">
/// <item>The element starts tooltip-ineligible and only becomes eligible on a real
/// <see cref="UIElement.MouseMove"/>, so a layout shift under a resting pointer cannot open a popup.
/// Leaving disarms it again, which also drops a popup the pointer walked away from.</item>
/// <item>Opening/closing are tracked so <see cref="Close"/> can tear a stranded popup down from any
/// other event. The opening event is <c>Direct</c>, so it has to be hooked per element - which is
/// what the attached property does, once, at the point it is set in XAML.</item>
/// </list>
///
/// <para>
/// Closing works by disabling the element's tooltip: that is what makes the service tear the popup
/// down, and it is left disabled because the next real pointer move re-arms it. There is no
/// "is it open?" query to lean on either - <c>ToolTipService.IsToolTipOpenProperty</c> is not public.
/// </para>
/// </remarks>
internal static class ToolTipGuard
{
    /// <summary>Element whose tooltip this guard last saw open.</summary>
    private static DependencyObject? _openOwner;

    /// <summary>Whether this element's tooltip is guarded. Default false.</summary>
    public static readonly DependencyProperty ManagedProperty =
        DependencyProperty.RegisterAttached(
            "Managed",
            typeof(bool),
            typeof(ToolTipGuard),
            new FrameworkPropertyMetadata(false, OnManagedChanged));

    public static void SetManaged(DependencyObject element, bool value) =>
        element.SetValue(ManagedProperty, value);

    public static bool GetManaged(DependencyObject element) => (bool)element.GetValue(ManagedProperty);

    private static void OnManagedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
        {
            return;
        }

        ToolTipService.AddToolTipOpeningHandler(fe, OnOpening);
        ToolTipService.AddToolTipClosingHandler(fe, OnClosing);
        fe.MouseMove += OnMouseMove;
        fe.MouseLeave += OnMouseLeave;

        // Only the Managed=True transition arms the gating; the reverse just restores the stock
        // behaviour for an element that is no longer guarded.
        ToolTipService.SetIsEnabled(fe, e.NewValue is not true);
    }

    private static void OnOpening(object sender, ToolTipEventArgs e) =>
        _openOwner = e.Source as DependencyObject;

    private static void OnClosing(object sender, ToolTipEventArgs e)
    {
        if (ReferenceEquals(_openOwner, e.Source))
        {
            _openOwner = null;
        }
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && !ToolTipService.GetIsEnabled(fe))
        {
            ToolTipService.SetIsEnabled(fe, true);
        }
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            // Disarms the tooltip for this element. If one is up, this closes it - which is what the
            // service would have done itself had the leave reached it.
            ToolTipService.SetIsEnabled(fe, false);
        }
    }

    /// <summary>
    /// Closes a tracked tooltip, if there is one. Safe to call from anywhere at any time, which is why
    /// it is wired into chrome passes rather than only into the events where a tooltip is expected.
    /// </summary>
    public static void Close()
    {
        if (_openOwner is FrameworkElement fe)
        {
            ToolTipService.SetIsEnabled(fe, false);
        }

        _openOwner = null;
    }
}