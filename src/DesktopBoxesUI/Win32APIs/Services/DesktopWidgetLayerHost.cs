using DesktopBoxesUI.Win32.NativeMethods;
using System.Runtime.Versioning;
using WindowsNative;
using WindowsNative.Desktop;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI.Win32APIs.Services;

/// <summary>
/// Owns the widget layer: the desktop shell topology (via the shared probe, so both layers
/// share one understanding of Progman/WorkerW/DefView) and the placement of the
/// <see cref="Views.DesktopSurface"/> window ABOVE the desktop icons. The surface owns the
/// widgets/containers (they are owned/glued to it), so keeping the surface correctly layered
/// keeps everything above the icons and below application windows.
///
/// Counterpart to the engine's <c>DesktopWallpaperLayerHost</c>, which parents wallpaper windows
/// BELOW the icons — both derive from <see cref="DesktopLayerHostBase"/>. The actual glue math
/// stays in <see cref="Win32Apis"/> (battle-tested there); this class owns when it runs.
/// DesktopManager hosts exactly one instance and only on the custom-surface feature path.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopWidgetLayerHost : DesktopLayerHostBase
{
    /// <summary>Probes the shell topology once (fast, UI-thread safe) and installs the layer watch.
    /// Returns false when the shell is unreachable — callers keep current placement and retry later.
    /// Unlike the engine's blocking EnsureLayer, this never sleeps: the UI thread must not stall.</summary>
    public bool TryEnsureLayer()
    {
        if (Disposed) return false;
        DesktopLayerInfo layer;
        try
        {
            layer = Probe();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Widget layer probe failed: {ex.Message}");
            return false;
        }

        // Above-icons placement needs the icon host itself; a probe without DefView cannot anchor us.
        if (layer.DefView == IntPtr.Zero || !User32.IsWindow(layer.DefView))
        {
            Serilog.Log.Warning($"Widget layer probe found no DefView (topology={layer.Topology}) — deferring attach");
            return false;
        }

        Layer = layer;
        User32.GetWindowThreadProcessId(Layer.Progman, out uint pid);
        InstallLayerWatch(pid);
        Serilog.Log.Information($"Widget layer ready: {Layer.Topology} progman=0x{Layer.Progman:X} workerW=0x{Layer.WorkerW:X} defView=0x{Layer.DefView:X}");
        return true;
    }

    /// <summary>Attaches the layer window (the DesktopSurface) above the desktop icons:
    /// re-probes when the cached layer went stale, then glues via the shared wrapper.
    /// No-op for a dead handle; safe to call redundantly.</summary>
    public void AttachAboveIcons(IntPtr hwnd)
    {
        if (Disposed || hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
        if (!IsLayerLive() && !TryEnsureLayer()) return;
        try
        {
            Win32Apis.GlueToDesktopSurface(hwnd);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Widget layer attach failed for 0x{hwnd:X}: {ex.Message}");
        }
    }

    /// <summary>Re-asserts z-order above the icons without moving the window (cheap path for
    /// relayouts). Falls back to a full attach when the anchor changed underneath us.</summary>
    public void AssertAboveIcons(IntPtr hwnd)
    {
        if (Disposed || hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
        try
        {
            var anchor = Win32Apis.GetDesktopAnchorHandle();
            if (anchor == IntPtr.Zero || !User32.IsWindow(anchor))
            {
                AttachAboveIcons(hwnd);
                return;
            }
            // Insert just above the anchor: above Explorer's desktop content, below every app.
            User32.SetWindowPos(hwnd, anchor, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Widget layer z-assert failed for 0x{hwnd:X}: {ex.Message}");
        }
    }

    /// <summary>True when the cached layer handles are still alive.</summary>
    public bool IsLayerLive()
    {
        return Layer.DefView != IntPtr.Zero && User32.IsWindow(Layer.DefView)
            && Layer.Progman != IntPtr.Zero && User32.IsWindow(Layer.Progman);
    }

    /// <summary>The widget layer additionally watches DefView (the icon host itself).</summary>
    protected override bool IsLayerWindow(IntPtr hwnd) =>
        hwnd == Layer.WorkerW || hwnd == Layer.Progman || hwnd == Layer.DefView;

    /// <summary>Re-probes when the cached layer went stale (e.g. after session unlock).
    /// Returns true when the layer is (again) usable.</summary>
    public bool ValidateLayer()
    {
        if (IsLayerLive()) return true;
        Serilog.Log.Warning("Widget layer handle went stale — re-probing");
        NotifyLayerLost();
        return false;
    }
}
