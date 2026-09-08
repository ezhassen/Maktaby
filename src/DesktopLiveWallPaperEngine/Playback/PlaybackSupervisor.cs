using DesktopLiveWallPaperEngine.Desktop;
using DesktopLiveWallPaperEngine.Interop;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static DesktopLiveWallPaperEngine.Interop.Win32Constants;

namespace DesktopLiveWallPaperEngine.Playback;

/// <summary>Event-driven pause supervisor: pauses wallpapers hidden under fullscreen apps (and
/// resumes them) with no polling loop and no timers.
///
/// Every input the policy (<see cref="PauseDecision"/>) reads arrives as an event:
/// <list type="bullet">
/// <item>foreground switches, minimizes and move/size completions via WinEvent hooks on a
/// dedicated thread with its own message pump (a hook thread that never pumps would never
/// fire);</item>
/// <item>geometry drifts of any top-level window via <c>EVENT_OBJECT_LOCATIONCHANGE</c>,
/// coalesced per-window on actual rect changes, so the hook storm of a window drag costs one
/// cheap compare per event;</item>
/// <item>session lock/unlock, display on/off and battery-saver transitions via the engine's
/// existing pushed notifications (<c>WM_WTSSESSION_CHANGE</c>, power-setting messages),
/// which land in <see cref="SessionLocked"/> / <see cref="DisplayOff"/> / <see cref="Invalidate"/>;</item>
/// <item>remote-session and exclusive-D3D-fullscreen state, which have no event of their own,
/// are read at evaluation time — i.e. only when something else already fired.</item>
/// </list>
///
/// The decision considers every visible top-level window, not just the foreground one: a
/// monitor pauses when ANY qualifying window covers it, so an unfocused fullscreen app keeps
/// its monitor paused (foreground-only checks wrongly resumed it the moment focus moved to
/// the other screen). Minimized, cloaked, shell and own-process windows are excluded from
/// the capture. One transient-noise hold remains (still no timers): a lone exclusive-D3D
/// flag flip with the window list otherwise identical is held as transition noise.
/// Evaluation is skipped entirely while <see cref="Suspend"/>ed (user-paused), and
/// <see cref="Resume"/> re-evaluates immediately, preserving the old no-spurious-resume contract.
/// Transitions fire on the hook/notification thread; the engine marshals them as before.</summary>
public sealed class PlaybackSupervisor : IDisposable
{
    private readonly Func<Config.PauseConfig> _config;
    private readonly Dictionary<string, PauseReason> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private volatile bool _suspended;
    private volatile bool _sessionLocked;
    private volatile bool _displayOff;
    private bool _disposed;

    private readonly Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly List<IntPtr> _hooks = [];
    private readonly User32.WinEventProc _winEventProc;

    /// <summary>Rooted for the process lifetime: unhooking cannot recall an in-flight
    /// out-of-context callback, and a collected delegate there fail-fasts the process.</summary>
    private static readonly ConcurrentDictionary<User32.WinEventProc, byte> HookRoots = new();

    /// <summary>Last known rect per top-level window, for coalescing LOCATIONCHANGE storms.
    /// Rebuilt from every full capture; any window's geometry can now flip some monitor's
    /// coverage, not just the foreground window's.</summary>
    private readonly Dictionary<IntPtr, RECT> _lastRects = [];

    /// <summary>Inputs of the last applied evaluation. QUNS (exclusive-D3D-fullscreen) has no
    /// owning event and flaps during mode transitions; a flip unaccompanied by any other input
    /// change is held, not applied — genuine entries always move/focus windows too.</summary>
    private sealed record LastInputs(IReadOnlyList<TopWindowInfo> Windows,
        bool Locked, bool Remote, bool Battery, bool DisplayOff, bool Quns)
    {
        public bool SameExceptQuns(LastInputs other) =>
            Locked == other.Locked && Remote == other.Remote && Battery == other.Battery &&
            DisplayOff == other.DisplayOff && Windows.SequenceEqual(other.Windows);
    }
    private LastInputs? _lastInputs;

    /// <summary>(monitorDevice, reason) — reason None means resume. Fires on the hook or
    /// notification thread that triggered the evaluation.</summary>
    public event Action<string, PauseReason>? PauseStateChanged;

    /// <summary>Set from session-change notifications; assigning re-evaluates immediately
    /// (unless suspended — the pending state is then picked up by <see cref="Resume"/>).</summary>
    public bool SessionLocked
    {
        get => _sessionLocked;
        set { _sessionLocked = value; Reevaluate(); }
    }

    /// <summary>Set from console-display-state notifications; assigning re-evaluates immediately
    /// (unless suspended — the pending state is then picked up by <see cref="Resume"/>).</summary>
    public bool DisplayOff
    {
        get => _displayOff;
        set { _displayOff = value; Reevaluate(); }
    }

    public PlaybackSupervisor(Func<Config.PauseConfig> config)
    {
        _config = config;
        _winEventProc = OnWinEvent;
        HookRoots.TryAdd(_winEventProc, 0);
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "PlaybackEvents" };
        _hookThread.Start();
        // Bounded wait: close the gap where a foreground switch could land before the hooks
        // exist. The initial Reevaluate below covers the state regardless.
        _hookReady.Wait(TimeSpan.FromSeconds(10));
        Reevaluate();
    }

    /// <summary>Last evaluated pause reason for a monitor (None when unknown). Thread-safe:
    /// diagnostics UI reads this off-thread while hook/notification threads write it.</summary>
    public PauseReason GetPauseReason(string monitorDevice)
    {
        lock (_gate)
        {
            return _state.TryGetValue(monitorDevice, out var reason) ? reason : PauseReason.None;
        }
    }

    /// <summary>Forget cached per-monitor state and re-fire transitions — call after creating a
    /// renderer while a pause condition may already hold.</summary>
    public void Invalidate()
    {
        lock (_gate) { _state.Clear(); _lastInputs = null; }
        Reevaluate();
    }

    /// <summary>Stops evaluation (idempotent, thread-safe). Hooks stay installed; their
    /// callbacks become no-ops, which costs nothing measurable at WinEvent rates.</summary>
    public void Suspend() => _suspended = true;

    /// <summary>Clears the suspension and evaluates immediately (idempotent, thread-safe).
    /// Caches are dropped so the post-suspend world is observed fresh rather than held.</summary>
    public void Resume()
    {
        if (_disposed) return;
        lock (_gate) { _state.Clear(); _lastInputs = null; }
        _suspended = false;
        Reevaluate();
    }

    private void HookThreadMain()
    {
        try
        {
            // Dedicated thread, pinned for life: every rect observed here must be physical pixels.
            if (User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == IntPtr.Zero)
                User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
            _hookThreadId = Kernel32.GetCurrentThreadId();
            const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            TryAddHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, flags);
            TryAddHook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZEEND, flags);
            TryAddHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, flags);
            if (_hooks.Count == 0)
            {
                Serilog.Log.Warning("Playback event hooks failed; pause automation falls back to pushed notifications only");
                return;
            }
            Serilog.Log.Information($"Playback event hooks installed ({_hooks.Count})");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Playback hook thread failed: {ex.Message}");
            return;
        }
        finally
        {
            try { _hookReady.Set(); } catch (ObjectDisposedException) { }
        }

        while (User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }

        foreach (var hook in _hooks)
        {
            try { User32.UnhookWinEvent(hook); } catch { }
        }
        _hooks.Clear();
    }

    private void TryAddHook(uint eventMin, uint eventMax, uint flags)
    {
        try
        {
            var hook = User32.SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _winEventProc, 0, 0, flags);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
            else Serilog.Log.Warning($"SetWinEventHook(0x{eventMin:X}, 0x{eventMax:X}) failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"SetWinEventHook(0x{eventMin:X}, 0x{eventMax:X}) threw: {ex.Message}");
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_disposed || _suspended) return;
        switch (eventId)
        {
            case EVENT_SYSTEM_FOREGROUND:
            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
            case EVENT_SYSTEM_MOVESIZEEND:
                if (idObject != OBJID_WINDOW) return;
                Reevaluate();
                break;
            case EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
                if (User32.GetWindowRect(hwnd, out var rect))
                {
                    lock (_gate)
                    {
                        // Coalesce the drag storm: unchanged rects cannot flip any coverage.
                        if (_lastRects.TryGetValue(hwnd, out var prev) && rect.Equals(prev)) return;
                        _lastRects[hwnd] = rect;
                    }
                }
                else
                {
                    // Gone (destroyed/cloaked): only a previously covering window matters, and
                    // those are exactly the cached ones. Unknown windows never affected state.
                    bool tracked;
                    lock (_gate) tracked = _lastRects.Remove(hwnd);
                    if (!tracked) return;
                }
                Reevaluate();
                break;
        }
    }

    private void Reevaluate()
    {
        if (_disposed || _suspended) return;

        // This runs on the hook thread, the engine thread, or a Win32 notification thread —
        // any of which may carry a different DPI context. The foreground rect captured here
        // must be in the same physical pixels as MonitorTracker's bounds.
        var awareness = User32.GetThreadDpiAwarenessContext();
        if (User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == IntPtr.Zero)
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
        List<(string Device, PauseReason Reason, string By)> current;
        string inputDesc = "";
        try
        {
            var windows = CaptureTopWindows();
            var flags = new SystemFlags(
                _sessionLocked,
                User32.GetSystemMetrics(SM_REMOTESESSION) != 0,
                IsBatterySaverOn(),
                IsD3DFullscreen(),
                _displayOff);
            var config = _config();

            var monitors = new List<(string Device, RECT Bounds, RECT WorkArea, IntPtr Handle)>();
            foreach (var monitor in MonitorTracker.Enumerate())
                monitors.Add((monitor.Device, monitor.Bounds, monitor.WorkArea, MonitorHandle(monitor.Bounds)));

            // Foreground identity is log-only now: coverage considers every captured window.
            var fgHwnd = User32.GetForegroundWindow();
            string fgDesc = "none";
            if (fgHwnd != IntPtr.Zero)
            {
                foreach (var w in windows)
                {
                    if (w.Hwnd == fgHwnd) { fgDesc = w.ClassName; break; }
                }
                if (fgDesc == "none")
                {
                    var sb = new StringBuilder(64);
                    fgDesc = User32.GetClassNameW(fgHwnd, sb, sb.Capacity) > 0 ? sb.ToString() + " (excluded)" : "unknown";
                }
            }
            inputDesc = $"fg=0x{fgHwnd:X} {fgDesc} wins={windows.Count} | quns={flags.D3DFullscreen} batt={flags.BatterySaver} rem={flags.RemoteSession} lock={flags.SessionLocked} disp={flags.DisplayOff}";

            var inputs = new LastInputs(windows, flags.SessionLocked, flags.RemoteSession, flags.BatterySaver, flags.DisplayOff, flags.D3DFullscreen);
            lock (_gate)
            {
                // Lone QUNS flip (window-for-window identical): hold, touch nothing.
                if (_lastInputs is not null && _lastInputs.SameExceptQuns(inputs))
                    return;
                _lastInputs = inputs;
                _lastRects.Clear();
                foreach (var w in windows) _lastRects[w.Hwnd] = w.WindowRect;
            }

            current = [];
            foreach (var (device, bounds, workArea, handle) in monitors)
            {
                var reason = PauseDecision.EvaluateForMonitor(windows, bounds, workArea, handle, flags, config);
                string by = "-";
                if (reason == PauseReason.Fullscreen)
                {
                    foreach (var w in windows)
                    {
                        if (PauseDecision.CoversMonitor(w.WindowRect, bounds, workArea, w.IsZoomed, w.MonitorHandle, handle))
                        {
                            by = w.ClassName;
                            break;
                        }
                    }
                }
                current.Add((device, reason, by));
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Playback evaluation failed", ex);
            return;
        }
        finally
        {
            if (awareness != IntPtr.Zero)
                User32.SetThreadDpiAwarenessContext(awareness);
        }

        List<(string Device, PauseReason Reason, string By)> transitions = [];
        lock (_gate)
        {
            // A Suspend landing mid-evaluation must still win: applying transitions now would
            // resume playback behind the user's manual pause.
            if (_disposed || _suspended) return;
            foreach (var (device, reason, by) in current)
            {
                if (!_state.TryGetValue(device, out var previous) || previous != reason)
                {
                    _state[device] = reason;
                    transitions.Add((device, reason, by));
                }
            }
            // Drop unplugged monitors so stale entries cannot misfire on reconnect.
            foreach (var device in _state.Keys.ToArray())
            {
                bool gone = true;
                foreach (var (d, _, _) in current)
                {
                    if (string.Equals(d, device, StringComparison.OrdinalIgnoreCase)) { gone = false; break; }
                }
                if (gone) _state.Remove(device);
            }
        }

        foreach (var (device, reason, by) in transitions)
        {
            Serilog.Log.Information("Pause transition: {device} -> {reason} by={by} ({inputDesc})", device, reason, by, inputDesc);
            try { PauseStateChanged?.Invoke(device, reason); }
            catch (Exception ex) { Serilog.Log.Error("Pause transition handler failed", ex); }
        }
    }

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Every visible, non-minimized, non-cloaked top-level window outside our own
    /// process and the shell. Runs synchronously on the caller's thread (awareness pinned by
    /// Reevaluate, for life by the hook thread).</summary>
    private static List<TopWindowInfo> CaptureTopWindows()
    {
        var list = new List<TopWindowInfo>(64);
        User32.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;
                // Never pause for our own windows (boxes, settings, dialogs): interacting with
                // the app itself must not count as covering the wallpaper.
                User32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == OwnPid) return true;
                if (!User32.GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
                var sb = new StringBuilder(64);
                if (User32.GetClassNameW(hwnd, sb, sb.Capacity) == 0) return true;
                string cls = sb.ToString();
                if (PauseDecision.IsShellOrOwnWindow(cls)) return true;
                // Suspended Store apps keep stale fullscreen rects while invisible.
                if (IsCloaked(hwnd)) return true;
                list.Add(new TopWindowInfo(hwnd, cls, rect, User32.IsZoomed(hwnd), User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)));
            }
            catch { }
            return true;
        };
        try { User32.EnumWindows(callback, IntPtr.Zero); }
        catch { }
        GC.KeepAlive(callback);
        return list;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try { return DwmApi.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, (uint)sizeof(int)) == 0 && cloaked != 0; }
        catch { return false; }
    }

    private static IntPtr MonitorHandle(in RECT bounds)
    {
        // MonitorFromWindow needs a window; identify the monitor by a representative point instead.
        var point = new POINT { X = bounds.Left + 1, Y = bounds.Top + 1 };
        return User32.MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
    }

    private static bool IsBatterySaverOn() =>
        Kernel32.GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;

    private static bool IsD3DFullscreen() =>
        Shell32.SHQueryUserNotificationState(out int state) == 0 && state == Shell32.QUNS_RUNNING_D3D_FULL_SCREEN;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _suspended = true;
        try
        {
            uint threadId = _hookThreadId;
            if (threadId != 0)
                User32.PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(TimeSpan.FromSeconds(5));
        }
        catch { }
        try { _hookReady.Dispose(); } catch { }
    }
}
