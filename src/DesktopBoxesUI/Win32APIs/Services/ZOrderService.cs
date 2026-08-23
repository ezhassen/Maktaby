using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using HWND = Windows.Win32.Foundation.HWND;
using SET_WINDOW_POS_FLAGS = Windows.Win32.UI.WindowsAndMessaging.SET_WINDOW_POS_FLAGS;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IZOrderService"/>. Insertion handles are derived from
/// the well-known HWND_TOP/BOTTOM/TOPMOST/NOTOPMOST sentinel values to avoid coupling to the
/// generated constant names.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ZOrderService : IZOrderService
{
    // SWP_NOMOVE=0x0002, SWP_NOSIZE=0x0001, SWP_NOACTIVATE=0x0010
    private const int SwpNoMove = 0x0002;
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;

    public void SetZOrder(nint hwnd, ZOrderTarget target)
    {
        HWND insert = target switch
        {
            ZOrderTarget.Top => (HWND)(IntPtr)0,        // HWND_TOP
            ZOrderTarget.Bottom => (HWND)(IntPtr)1,     // HWND_BOTTOM
            ZOrderTarget.TopMost => (HWND)(IntPtr)(-1), // HWND_TOPMOST
            ZOrderTarget.NoTopMost => (HWND)(IntPtr)(-2), // HWND_NOTOPMOST
            ZOrderTarget.BehindDesktopIcons => (HWND)(IntPtr)1,
            _ => (HWND)(IntPtr)0,
        };

        var flags = (SET_WINDOW_POS_FLAGS)(SwpNoMove | SwpNoSize | SwpNoActivate);
        Win32Apis.SetWindowPos((HWND)hwnd, insert, 0, 0, 0, 0, flags);
    }
}
