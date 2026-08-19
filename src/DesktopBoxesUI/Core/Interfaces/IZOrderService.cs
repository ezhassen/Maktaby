namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Controls window Z-order (top, bottom, topmost, or placed behind the desktop icons).
/// Implemented with Win32 <c>SetWindowPos</c>. Kept abstract so Core can describe intent
/// without referencing Win32 insertion handles.
/// </summary>
public interface IZOrderService
{
    void SetZOrder(nint hwnd, ZOrderTarget target);
}
