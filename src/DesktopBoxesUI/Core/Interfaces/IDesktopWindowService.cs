using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Detects the desktop window and reports its bounds so Box windows can be positioned
/// relative to the real Windows desktop. Implemented via Shell/Win32 in the platform layer.
/// </summary>
public interface IDesktopWindowService
{
    /// <summary>Window handle (HWND) of the desktop/Progman window, as an opaque pointer.</summary>
    nint GetDesktopWindowHandle();

    /// <summary>Window handle (HWND) of the shell window (Progman/WorkerW), as an opaque pointer.</summary>
    nint GetShellWindowHandle();

    /// <summary>Bounds of the desktop surface where Boxes should live.</summary>
    RectD GetDesktopBounds();
}
