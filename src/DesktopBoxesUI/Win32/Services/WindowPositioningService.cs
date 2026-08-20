using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Win32.NativeMethods;
using HWND = Windows.Win32.Foundation.HWND;
using RECT = Windows.Win32.Foundation.RECT;
using SET_WINDOW_POS_FLAGS = Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IWindowPositioningService"/> using SetWindowPos/GetWindowRect.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class WindowPositioningService : IWindowPositioningService
{
    // SWP_NOMOVE=0x0002, SWP_NOZORDER=0x0004, SWP_NOACTIVATE=0x0010
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    public void SetBounds(nint hwnd, RectD bounds)
    {
        var flags = (SET_WINDOW_POS_FLAGS)(SwpNoZOrder | SwpNoActivate);
        Win32Apis.SetWindowPos(
            (HWND)hwnd,
            (HWND)(IntPtr)0,
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            flags);
    }

    public RectD GetBounds(nint hwnd)
    {
        RECT rect = default;
        Win32Apis.GetWindowRect((HWND)hwnd, out rect);
        return RectD.FromXYWH(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }
}
