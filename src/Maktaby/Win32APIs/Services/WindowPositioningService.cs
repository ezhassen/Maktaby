using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;

namespace Maktaby.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IWindowPositioningService"/> using SetWindowPos/GetWindowRect.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class WindowPositioningService : IWindowPositioningService
{
    // SWP_NOMOVE=0x0002, SWP_NOZORDER=0x0004, SWP_NOACTIVATE=0x0010
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public void SetBounds(nint hwnd, RectD bounds)
    {
        uint flags = SwpNoZOrder | SwpNoActivate;
        Win32Apis.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            flags);
    }

    public RectD GetBounds(nint hwnd)
    {
        RECT rect = default;
        Win32Apis.GetWindowRect(hwnd, out rect);
        return RectD.FromXYWH(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
}
