using WindowsNative;
using WindowsNative.Desktop;
using System.Runtime.InteropServices;
using static WindowsNative.Win32Constants;

namespace DesktopLiveWallPaperEngine.Desktop;

/// <summary>Owns the fragile part: spawning/finding the wallpaper layer, attaching our
/// windows behind the desktop icons on both known shell topologies, re-attaching when
/// explorer restarts or the layer is destroyed, and restoring the desktop on exit.</summary>
public sealed class DesktopWallpaperLayerHost : DesktopLayerHostBase
{
    private readonly List<IntPtr> _attached = [];
    private readonly object _attachSync = new();

    /// <summary>Probes with retries — on 24H2+ the WorkerW is created lazily and may not
    /// exist for a while after logon.</summary>
    public void EnsureLayer()
    {
        for (int attempt = 0; ; attempt++)
        {
            Layer = Probe();
            bool ok = Layer.Topology == DesktopTopology.RaisedDesktop
                ? Layer.WorkerW != IntPtr.Zero || Layer.DefView != IntPtr.Zero
                : Layer.WorkerW != IntPtr.Zero;
            if (ok)
            {
                Serilog.Log.Information($"Desktop layer ready: {Layer.Topology} progman=0x{Layer.Progman:X} workerW=0x{Layer.WorkerW:X} defView=0x{Layer.DefView:X}");
                if (Layer.WorkerW != IntPtr.Zero)
                {
                    User32.GetWindowThreadProcessId(Layer.WorkerW, out uint pid);
                    InstallLayerWatch(pid);
                }
                return;
            }
            if (attempt >= 20)
                throw new InvalidOperationException("Wallpaper layer (WorkerW) did not appear after 20 attempts. Ensure desktop icons are enabled.");
            Thread.Sleep(300);
        }
    }

    /// <summary>Parents <paramref name="hwnd"/> into the wallpaper layer and positions it to
    /// cover <paramref name="screenBounds"/> (virtual-screen coordinates). Content must be
    /// presented via DirectComposition (see CompositionHost) — redirection-surface painting
    /// is not composed on raised desktops.</summary>
    public void Attach(IntPtr hwnd, RECT screenBounds)
    {
        IntPtr parent = Layer.Topology == DesktopTopology.RaisedDesktop ? Layer.Progman : Layer.WorkerW;
        if (parent == IntPtr.Zero) throw new InvalidOperationException("Desktop layer not initialized.");

        if (Layer.Topology == DesktopTopology.RaisedDesktop)
        {
            // Child style BEFORE SetParent, then slot directly below SHELLDLL_DefView.
            // WS_POPUP and WS_CHILD are mutually exclusive — swap, never combine.
            var style = (long)User32.GetWindowLongPtrW(hwnd, GWL_STYLE);
            User32.SetWindowLongPtrW(hwnd, GWL_STYLE, new IntPtr((style & ~(long)WS_POPUP) | WS_CHILD));
        }

        if (User32.SetParent(hwnd, parent) == IntPtr.Zero)
            throw new InvalidOperationException($"SetParent into wallpaper layer failed: {Marshal.GetLastWin32Error()}");

        User32.GetWindowRect(parent, out var parentRect);
        var client = MonitorTracker.ScreenToParentClient(screenBounds, parentRect);
        User32.SetWindowPos(hwnd, IntPtr.Zero, client.Left, client.Top, client.Width, client.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        if (Layer.Topology == DesktopTopology.RaisedDesktop && Layer.DefView != IntPtr.Zero)
        {
            // Below the icons (DefView), above the shell's own WorkerW.
            User32.SetWindowPos(hwnd, Layer.DefView, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            EnsureWorkerWAtBottom();
        }

        lock (_attachSync)
        {
            _attached.Add(hwnd);
            // Re-applies re-attach every window while this ledger clears only on layer loss:
            // drop handles that died with a previous session so it cannot grow without bound
            // across display-change storms.
            _attached.RemoveAll(h => h != hwnd && !User32.IsWindow(h));
        }
        Serilog.Log.Information($"Attached 0x{hwnd:X} at {client} ({Layer.Topology})");
    }

    /// <summary>Places an overlay (widget) window in the layer directly above the wallpaper
    /// windows but still under the desktop icons.</summary>
    public void AttachOverlay(IntPtr hwnd, RECT screenBounds)
    {
        Attach(hwnd, screenBounds);
        AssertOverlayZOrder(hwnd);
    }

    /// <summary>Re-asserts a reused wallpaper window's placement without rebuilding it.
    ///
    /// ApplyToMonitor reuses windows across wallpaper changes (only disable/enable recreates
    /// them), so a window can outlive the geometry it was attached with. A window that lost
    /// its parent is fully re-attached; a window whose rect merely drifted is only LOGGED for
    /// now — repositioning a child from parent-relative math proved unsafe on multi-monitor
    /// setups (it shifted the second monitor's window left), so drift needs a confirmed
    /// mapping before it is auto-corrected.</summary>
    public void ReassertPlacement(IntPtr hwnd, RECT screenBounds)
    {
        IntPtr parent = Layer.Topology == DesktopTopology.RaisedDesktop ? Layer.Progman : Layer.WorkerW;
        if (parent == IntPtr.Zero || !User32.IsWindow(hwnd)) return;

        // Fell out of the layer entirely (explorer recycled the parent) — full re-attach.
        if (User32.GetParent(hwnd) != parent)
        {
            Serilog.Log.Information($"Wallpaper window 0x{hwnd:X} lost its parent — re-attaching at {screenBounds}");
            Attach(hwnd, screenBounds);
            return;
        }

        User32.GetWindowRect(parent, out var parentRect);
        var expected = MonitorTracker.ScreenToParentClient(screenBounds, parentRect);
        // Window rects are screen coords; the expected client rect is parent-relative.
        int expLeft = parentRect.Left + expected.Left;
        int expTop = parentRect.Top + expected.Top;
        User32.GetWindowRect(hwnd, out var current);
        if (current.Left != expLeft || current.Top != expTop ||
            current.Width != expected.Width || current.Height != expected.Height)
        {
            Serilog.Log.Information($"Wallpaper window 0x{hwnd:X} drifted: actual {current}, expected {screenBounds} (parent {parentRect}) — leaving in place, see log");
        }
    }

    /// <summary>Keeps an overlay above the wallpaper surfaces after a new wallpaper attach.</summary>
    public void AssertOverlayZOrder(IntPtr hwnd)
    {
        if (Layer.Topology == DesktopTopology.RaisedDesktop && Layer.DefView != IntPtr.Zero)
            User32.SetWindowPos(hwnd, Layer.DefView, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else
            User32.SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Screen rect of the layer parent — children position relative to this.</summary>
    public RECT ParentScreenRect()
    {
        IntPtr parent = Layer.Topology == DesktopTopology.RaisedDesktop ? Layer.Progman : Layer.WorkerW;
        User32.GetWindowRect(parent, out var rect);
        return rect;
    }

    private void EnsureWorkerWAtBottom()
    {
        if (Layer.WorkerW != IntPtr.Zero && User32.IsWindow(Layer.WorkerW))
            User32.SetWindowPos(Layer.WorkerW, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Clears the attached-window ledger, then runs the shared loss path.</summary>
    public override void NotifyLayerLost()
    {
        lock (_attachSync) _attached.Clear();
        base.NotifyLayerLost();
    }

    public void ValidateLayer()
    {
        if (Layer.WorkerW != IntPtr.Zero && !User32.IsWindow(Layer.WorkerW))
        {
            Serilog.Log.Warning("WorkerW handle went stale (session unlock?) — re-probing");
            NotifyLayerLost();
        }
    }
}
