using DesktopLiveWallPaperEngine.Desktop;
using DesktopLiveWallPaperEngine.Interop;
using System.Text;
using static DesktopLiveWallPaperEngine.Interop.Win32Constants;
using Timer = System.Threading.Timer;

namespace DesktopLiveWallPaperEngine.Playback;

/// <summary>Polls (500 ms) the foreground/system state and reports per-monitor pause
/// transitions. Polling beats WinEvent location hooks here (too chatty/unreliable).
/// Polling can be <see cref="Suspend"/>ed while the user has paused playback manually —
/// that also fixes spurious auto-resumes (fullscreen closes while user-paused used to
/// resume playback behind the user's back) — and <see cref="Resume"/>d on PlayStart
/// with an immediate re-evaluation.</summary>
public sealed class PlaybackMonitor : IDisposable
{
    private const int PeriodMs = 500;

    private readonly Timer _timer;
    private readonly Func<Config.PauseConfig> _config;
    private readonly Dictionary<string, PauseReason> _state = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _suspended;
    private bool _disposed;

    /// <summary>Set from session-change notifications on the message window.</summary>
    public volatile bool SessionLocked;

    /// <summary>Set from GUID_CONSOLE_DISPLAY_STATE, pushed to the message window. Nothing here
    /// polls for it — the poll only reads the flag someone else was told about.</summary>
    public volatile bool DisplayOff;

    private volatile bool _invalidated;

    /// <summary>Forget cached per-monitor state so the next poll re-fires transitions —
    /// call after creating a renderer while a pause condition may already hold.</summary>
    public void Invalidate() => _invalidated = true;

    /// <summary>(monitorDevice, reason) — reason None means resume. Fires on the timer thread.</summary>
    public event Action<string, PauseReason>? PauseStateChanged;

    public PlaybackMonitor(Func<Config.PauseConfig> config)
    {
        _config = config;
        _timer = new Timer(_ => Poll(), null, 1000, PeriodMs);
    }

    /// <summary>Stops polling (idempotent, thread-safe). In-flight polls finish harmlessly.</summary>
    public void Suspend()
    {
        _suspended = true;
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Restarts polling with an immediate evaluation (idempotent, thread-safe).</summary>
    public void Resume()
    {
        if (_disposed) return;
        _suspended = false;
        try { _timer.Change(0, PeriodMs); }
        catch (ObjectDisposedException) { }
    }

    private void Poll()
    {
        try
        {
            if (_suspended) return;
            if (_invalidated)
            {
                _invalidated = false;
                _state.Clear();
            }
            var flags = new SystemFlags(
                SessionLocked,
                User32.GetSystemMetrics(SM_REMOTESESSION) != 0,
                IsBatterySaverOn(),
                IsD3DFullscreen(),
                DisplayOff);

            var foreground = CaptureForeground();
            var config = _config();

            foreach (var monitor in MonitorTracker.Enumerate())
            {
                var handle = MonitorHandle(monitor.Bounds);
                var reason = PauseDecision.Evaluate(foreground, monitor.Bounds, monitor.WorkArea, handle, flags, config);
                if (!_state.TryGetValue(monitor.Device, out var previous) || previous != reason)
                {
                    _state[monitor.Device] = reason;
                    PauseStateChanged?.Invoke(monitor.Device, reason);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Playback poll failed", ex);
        }
    }

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    private static ForegroundInfo? CaptureForeground()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !User32.IsWindowVisible(hwnd)) return null;
        // Never pause for our own windows (boxes, settings, dialogs): interacting with
        // the app itself must not count as covering the wallpaper.
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnPid) return null;
        var sb = new StringBuilder(256);
        User32.GetClassNameW(hwnd, sb, sb.Capacity);
        User32.GetWindowRect(hwnd, out var rect);
        return new ForegroundInfo(sb.ToString(), rect, User32.IsZoomed(hwnd), User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
    }

    private static IntPtr MonitorHandle(in RECT bounds)
    {
        // MonitorFromWindow needs a window; identify the monitor by a representative point instead.
        var point = new POINT { X = bounds.Left + 1, Y = bounds.Top + 1 };
        return MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    private static bool IsBatterySaverOn() =>
        Kernel32.GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;

    private static bool IsD3DFullscreen() =>
        Shell32.SHQueryUserNotificationState(out int state) == 0 && state == Shell32.QUNS_RUNNING_D3D_FULL_SCREEN;

    public void Dispose()
    {
        _disposed = true;
        _suspended = true;
        _timer.Dispose();
    }
}
