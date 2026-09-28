using Serilog;
using System.Collections.Concurrent;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Native.Desktop;

public enum DesktopTopology
{
    /// <summary>Win10 / Win11 ≤23H2 and early 24H2: wallpaper WorkerW is a top-level
    /// sibling; layers SetParent into that WorkerW.</summary>
    ClassicWorkerW,
    /// <summary>2025+ "raised desktop" (HDR-capable shell): SHELLDLL_DefView is a layered
    /// child of Progman; layers become children of Progman around DefView.</summary>
    RaisedDesktop,
}

public sealed record DesktopLayerInfo(DesktopTopology Topology, IntPtr Progman, IntPtr WorkerW, IntPtr DefView);

/// <summary>Shared core for desktop-layer hosts: shell topology probing, layer state, WinEvent
/// destroy-watch and loss notification. The engine's wallpaper host (BELOW the icons) and the
/// UI's widget host (ABOVE the icons) both derive from this; only attach semantics and the
/// watched-window set differ. Hand-rolled interop only; logging via Serilog.</summary>
public abstract class DesktopLayerHostBase : IDisposable
{
    protected const uint WM_SPAWN_WORKER = 0x052C;

    public DesktopLayerInfo Layer { get; protected set; } = new(DesktopTopology.ClassicWorkerW, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Raised when the shell layer died and must be re-attached. Handlers must return fast.</summary>
    public event Action? LayerLost;

    private WinEventProc? _winEventProc; // rooted while hook lives
    private IntPtr _winEventHook;
    private bool _disposed;

    protected bool Disposed => _disposed;

    /// <summary>Every installed hook procedure, rooted for the process lifetime.
    /// UnhookWinEvent stops new callbacks but cannot recall one already dispatched —
    /// a late landing on a collected delegate fail-fasts the process. One entry per
    /// host is ~100 bytes; never removed, by design.</summary>
    private static readonly ConcurrentDictionary<WinEventProc, byte> HookRoots = new();

    public static DesktopLayerInfo Probe()
    {
        var progman = User32.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
            throw new InvalidOperationException("Progman not found — is explorer.exe running?");

        // Ask Progman to spawn the wallpaper WorkerW. No-op if it already exists.
        User32.SendMessageTimeoutW(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);

        bool raised = ((long)User32.GetWindowLongPtrW(progman, GWL_EXSTYLE) & WS_EX_NOREDIRECTIONBITMAP) != 0;
        if (raised)
        {
            var defView = User32.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            var workerW = User32.FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
            return new DesktopLayerInfo(DesktopTopology.RaisedDesktop, progman, workerW, defView);
        }

        // Classic: find the top-level window hosting SHELLDLL_DefView, then take the next
        // top-level WorkerW sibling after it (covers both the Win10 WorkerW-hosted DefView
        // and the Win11 Progman-hosted DefView variants).
        IntPtr host = IntPtr.Zero, worker = IntPtr.Zero, shellDefView = IntPtr.Zero;
        EnumWindowsProc enumProc = (hwnd, _) =>
        {
            var dv = User32.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero)
            {
                host = hwnd;
                shellDefView = dv;
                worker = User32.FindWindowExW(IntPtr.Zero, hwnd, "WorkerW", null);
                return false;
            }
            return true;
        };
        User32.EnumWindows(enumProc, IntPtr.Zero);
        GC.KeepAlive(enumProc);
        return new DesktopLayerInfo(DesktopTopology.ClassicWorkerW, progman, worker, shellDefView);
    }

    /// <summary>Installs the destroy-watch scoped to an Explorer process. Derived classes pass the
    /// pid they resolved and keep their own preconditions; pid 0 never hooks (system-wide).</summary>
    protected void InstallLayerWatch(uint pid)
    {
        if (_disposed || _winEventHook != IntPtr.Zero || pid == 0) return;
        _winEventProc = OnWinEvent;
        _winEventHook = User32.SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        if (_winEventHook != IntPtr.Zero) HookRoots.TryAdd(_winEventProc, 0);
    }

    /// <summary>Which shell windows count as "the layer" for destroy notifications.
    /// Default covers Progman/WorkerW (wallpaper host); the widget host adds DefView.</summary>
    protected virtual bool IsLayerWindow(IntPtr hwnd) =>
        hwnd == Layer.WorkerW || hwnd == Layer.Progman;

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || idObject != OBJID_WINDOW) return;
        if (!IsLayerWindow(hwnd)) return;
        Log.Warning($"Desktop layer window 0x{hwnd:X} destroyed — scheduling re-attach");
        NotifyLayerLost();
    }

    /// <summary>Explorer restarted or the layer was destroyed: drop the watch and tell the
    /// owner to re-probe and re-attach everything.</summary>
    public virtual void NotifyLayerLost()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            try { User32.UnhookWinEvent(_winEventHook); } catch { }
            _winEventHook = IntPtr.Zero;
        }
        try { LayerLost?.Invoke(); } catch (Exception ex) { Log.Error("LayerLost handler failed", ex); }
    }

    /// <summary>Final cleanup: repaint the desktop so the original static wallpaper shows.
    /// Only called on exit — on 24H2 raised desktops this refresh destroys the live WorkerW.</summary>
    public static void RestoreDesktop()
    {
        User32.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, IntPtr.Zero, SPIF_UPDATEINIFILE);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_winEventHook != IntPtr.Zero)
        {
            try { User32.UnhookWinEvent(_winEventHook); } catch { }
            _winEventHook = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }
}
