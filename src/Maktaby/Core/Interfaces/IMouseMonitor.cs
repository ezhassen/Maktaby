namespace Maktaby.Core.Interfaces;

/// <summary>
/// Watches system mouse-button presses and reports the window that was hit. Used to collapse a
/// container's chrome when the user clicks outside the application (empty desktop, another app),
/// which a desktop-glued window may not register as a normal focus/activation loss.
/// </summary>
public interface IMouseMonitor
{
    /// <summary>Raised (on the UI thread) for every mouse-button press, with the hit window's handle.</summary>
    event EventHandler<IntPtr> MouseButtonDown;

    /// <summary>
    /// Raised (on the UI thread) when the user double-clicks empty desktop area (not a desktop icon
    /// and not another window). Used to toggle the "hide all boxes" action.
    /// </summary>
    event EventHandler? DesktopDoubleClick;

    void Start();

    void Stop();
}
