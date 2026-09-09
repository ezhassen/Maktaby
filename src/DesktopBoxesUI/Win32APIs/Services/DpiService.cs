using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IDpiService"/> using DPI APIs.
/// DPI-change notifications will be wired through WPF's per-monitor v2 support later.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DpiService : IDpiService
{
    public double GetSystemDpi()
        => Win32Apis.GetDpiForSystem();

    public double GetDpiForWindow(nint hwnd)
        => Win32Apis.GetDpiForWindow(hwnd);

    public event EventHandler<DpiChangedEventArgs>? DpiChanged;

    private void OnDpiChanged(DpiChangedEventArgs e)
        => DpiChanged?.Invoke(this, e);
}
