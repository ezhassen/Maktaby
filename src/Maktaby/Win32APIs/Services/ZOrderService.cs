using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Win32.NativeMethods;

namespace Maktaby.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IZOrderService"/>. Insertion handles are derived from
/// the well-known HWND_TOP/BOTTOM/TOPMOST/NOTOPMOST sentinel values to avoid coupling to the
/// generated constant names.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ZOrderService : IZOrderService
{
    // SWP_NOMOVE=0x0002, SWP_NOSIZE=0x0001, SWP_NOACTIVATE=0x0010
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    public void SetZOrder(nint hwnd, ZOrderTarget target)
    {
        IntPtr insert = target switch
        {
            ZOrderTarget.Top => new IntPtr(0),        // HWND_TOP
            ZOrderTarget.Bottom => new IntPtr(1),     // HWND_BOTTOM
            ZOrderTarget.TopMost => new IntPtr(-1),   // HWND_TOPMOST
            ZOrderTarget.NoTopMost => new IntPtr(-2), // HWND_NOTOPMOST
            ZOrderTarget.BehindDesktopIcons => new IntPtr(1),
            _ => new IntPtr(0),
        };

        uint flags = SwpNoMove | SwpNoSize | SwpNoActivate;
        Win32Apis.SetWindowPos(hwnd, insert, 0, 0, 0, 0, flags);
    }
}
