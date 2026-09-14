using System.Windows;

namespace DesktopBoxes.WidgetSdk;

/// <summary>Mouse button for <see cref="INativeWidget.Pressed"/>.</summary>
public enum WidgetPointerButton
{
    Left,
    Middle,
    Right,
    X1,
    X2,
}

/// <summary>Pointer press over a native widget, in DIPs relative to <see cref="INativeWidget.Visual"/>.</summary>
public sealed class WidgetPointerEventArgs(double x, double y, WidgetPointerButton button) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public WidgetPointerButton Button { get; } = button;
}

/// <summary>Contract a native widget plugin implements. The host creates one instance per
/// placed container (parameterless constructor), calls <see cref="Visual"/> once on the UI
/// thread to obtain the hosted root element, then drives the lifecycle:
/// <c>ApplyTheme</c> after load and on theme changes, <c>Suspend</c>/<c>Resume</c> when the
/// widget is hidden, covered, minimized or session-locked, <c>Dispose</c> on close.
/// All members run on the UI thread. Throwing from any member disables that instance
/// (logged) but never the host — keep handlers fast and total.</summary>
public interface INativeWidget : IDisposable
{
    /// <summary>Root visual the host parents into the widget window. Stable for the instance.</summary>
    FrameworkElement Visual { get; }

    /// <summary>True after <see cref="Suspend"/> until <see cref="Resume"/>.</summary>
    bool IsSuspended { get; }

    /// <summary>Stop timers, animation and rendering. Idempotent; safe before <see cref="Visual"/> is shown.</summary>
    void Suspend();

    /// <summary>Restart what <see cref="Suspend"/> stopped. Idempotent.</summary>
    void Resume();

    /// <summary>Apply a theme: <c>"dark"</c>, <c>"light"</c> or <c>null</c> (host default).
    /// Only called when the manifest opts in via <c>supportsTheme</c>.</summary>
    void ApplyTheme(string? theme);

    /// <summary>Pointer entered the widget. Optional: the host also detects WPF routed
    /// mouse events itself and merges both sources.</summary>
    event EventHandler? Entered;
    event EventHandler? Left;
    event EventHandler? Clicked;
    event EventHandler<WidgetPointerEventArgs>? Pressed;
    event EventHandler? Focused;
    event EventHandler? Unfocused;
}
