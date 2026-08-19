using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Positions a native window (the WPF Box window) on screen. Implemented with Win32
/// <c>SetWindowPos</c> / <c>GetWindowRect</c>. The WPF layer passes its HWND and Core geometry.
/// </summary>
public interface IWindowPositioningService
{
    void SetBounds(nint hwnd, RectD bounds);

    RectD GetBounds(nint hwnd);
}
