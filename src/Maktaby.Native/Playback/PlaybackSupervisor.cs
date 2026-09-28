using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static Maktaby.Native.Win32Constants;
using Timer = System.Threading.Timer;

namespace Maktaby.Native.Playback;

/// <summary>One monitor as the supervisor sees it. Each host maps its own monitor
/// enumeration to this (the live-wallpaper engine maps its MonitorTracker entries;
/// the desktop app maps its IMonitorService) so the supervisor never depends on either.</summary>
public sealed record PauseMonitor(string Device, RECT Bounds, RECT WorkArea);

/// <summary>Event-driven pause supervisor: pauses per-monitor content (live wallpapers,
/// suspendable widgets) hidden under fullscreen apps (and resumes them) with no polling
/// loop.
///
/// Every input the policy (<see cref="PauseDecision"/>) reads arrives as an event:
/// <list type="bullet">
/// <item>foreground switches, minimizes and move/size completions via WinEvent hooks on a
/// dedicated thread with its own message pump (a hook thread that never pumps would never
/// fire);</item>
/// <item>geometry drifts of any top-level window via <c>EVENT_OBJECT_LOCATIONCHANGE</c>,
/// coalesced per-window on actual rect changes and throttled with every other event
/// source through one leading+trailing gate (<see cref="EvalMinIntervalMs"/>), so a focus
/// fight or drag storm costs one immediate evaluation plus ~5 trailing ones a second
/// instead of dozens. A single-shot trailing timer covers the settled state after motion
/// stops (drag end is additionally covered immediately by MOVESIZEEND);</item>
/// <item>session lock/unlock, display on/off and battery-saver transitions via pushed
/// notifications (<c>WM_WTSSESSION_CHANGE</c>, power-setting messages), which land in
/// <see cref="SessionLocked"/> / <see cref="DisplayOff"/> / <see cref="Invalidate"/>;</item>
/// <item>remote-session and exclusive-D3D-fullscreen state, which have no event of their own,
/// are read at evaluation time — i.e. only when something else already fired.</item>
/// </list>
///
/// The decision considers every visible top-level window, not just the foreground one: a
/// monitor pauses when ANY qualifying window covers it, so an unfocused fullscreen app keeps
/// its monitor paused (foreground-only checks wrongly resumed it the moment focus moved to
/// the other screen). Minimized, cloaked, shell, own-process and non-occluding-overlay
/// windows are excluded from the capture. One transient-noise hold remains (no polling involved): a lone exclusive-D3D
/// flag flip with the window list otherwise identical is held as transition noise.
/// Evaluation is skipped entirely while <see cref="Suspend"/>ed (user-paused), and
/// <see cref="Resume"/> re-evaluates immediately, preserving the old no-spurious-resume contract.
/// Transitions fire on the hook/notification thread; the host marshals them as before.</summary>
public sealed class PlaybackSupervisor : IDisposable
{
    /// <summary>Current subscribers. Replaced (never mutated in place) under
    /// <see cref="_gate"/> by <see cref="AddSubscriber"/>/<see cref="RemoveSubscriber"/>; an
    /// evaluation reads it once into a local and works from that snapshot. Per-subscriber
    /// applied state lives on the <see cref="PauseSubscription"/> itself, so a host that
    /// joins or leaves never disturbs the state of the hosts already attached.</summary>
    private PauseSubscription[] _subscribers;
    /// <summary>Union of every subscriber's <c>extraExcludedWindowClasses</c>, applied once
    /// during the capture. Correct because an exclusion can only ever remove a window from
    /// consideration, and each host's own windows are already excluded by PID — so no host can
    /// lose coverage because another host excluded a class. Written only under
    /// <see cref="_gate"/>; read into a local at the start of an evaluation.</summary>
    private IReadOnlySet<string>? _extraExcluded;
    private readonly Lock _gate = new();
    private volatile bool _suspended;
    private bool _disposed;

    private readonly Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private readonly List<IntPtr> _hooks = [];
    private readonly WinEventProc _winEventProc;

    /// <summary>Rooted for the process lifetime: unhooking cannot recall an in-flight
    /// out-of-context callback, and a collected delegate there fail-fasts the process.</summary>
    private static readonly ConcurrentDictionary<WinEventProc, byte> HookRoots = new();

    /// <summary>Class name per top-level HWND. A window's class is fixed for the window's
    /// lifetime, so this is resolved once per HWND instead of once per window per evaluation
    /// (a cross-process <c>GetClassNameW</c> plus a <c>StringBuilder</c> allocation each, on
    /// every capture). Concurrent because a full capture and a trailing debounce can overlap:
    /// the direct-call entry points (Resume/SessionLocked/DisplayOff/Invalidate) bypass the
    /// evaluation gate, so two captures really can run at once. Concurrent + best-effort
    /// pruning is safe here because this is pure memoization — a lost entry costs one
    /// recomputation, never a wrong answer.</summary>
    private readonly ConcurrentDictionary<IntPtr, string> _classNames = new();

    /// <summary>Reused <c>StringBuilder</c> for class-name reads, so a capture allocates no
    /// per-window garbage. Per-thread: a capture can run on the hook thread, the timer pool or
    /// a Win32 notification thread, and must not share a buffer across them.</summary>
    [ThreadStatic] private static StringBuilder? _classNameBuffer;

    /// <summary>Reused so a capture allocates no delegate per call. Rooted for the
    /// supervisor's lifetime, which is what <c>EnumWindows</c> requires of a live callback.</summary>
    private readonly EnumWindowsProc _enumWindowsProc;

    /// <summary>List the in-flight <see cref="OnEnumWindow"/> callback appends to. Per-thread
    /// because captures really can overlap: the direct-call entry points (Resume /
    /// SessionLocked / DisplayOff / Invalidate) bypass the evaluation gate, so one capture can
    /// run on the hook thread while another runs on the timer pool. A shared field would let
    /// one capture's windows land in the other's list. <c>EnumWindows</c> itself is
    /// single-threaded and synchronous, so thread-local is exactly the right scope.</summary>
    [ThreadStatic] private static List<TopWindowInfo>? _capturing;

    /// <summary>Last known rect per top-level window, for coalescing LOCATIONCHANGE storms.
    /// Rebuilt from every full capture; any window's geometry can now flip some monitor's
    /// coverage, not just the foreground window's.</summary>
    private readonly Dictionary<IntPtr, RECT> _lastRects = [];

    /// <summary>Minimum spacing between two full evaluations from event sources. A drag,
    /// resize or focus fight fires dozens of WinEvents per second and each full evaluation
    /// walks every top-level window (<c>EnumWindows</c> + class name + rect + cloaked check
    /// each), so unthrottled storms pinned the hook thread and spiked CPU. First event in a
    /// quiet period still evaluates immediately (pause latency unchanged); the overflow
    /// collapses into one trailing evaluation. Direct calls (Resume, SessionLocked,
    /// DisplayOff, Invalidate) bypass the gate — they are rare and must apply at once.</summary>
    private const int EvalMinIntervalMs = 200;

    /// <summary>Single-shot trailing edge for throttled events: fires one last evaluation
    /// after a storm settles so a skipped final state can never stay stale. Armed only
    /// while a storm is being throttled (and disarmed by the next leading evaluation), so it
    /// is idle — no ticking, no polling — outside storms. Disposed with the supervisor.</summary>
    private readonly Timer _locationDebounce;
    private long _lastEvalMs;
    /// <summary>Whether the trailing edge is currently armed. Guarded by <see cref="_gate"/>;
    /// lets a storm re-arm the timer once instead of once per event.</summary>
    private bool _debounceArmed;

    /// <summary>Inputs of the last applied evaluation. QUNS (exclusive-D3D-fullscreen) has no
    /// owning event and flaps during mode transitions; a flip unaccompanied by any other input
    /// change is held, not applied — genuine entries always move/focus windows too.</summary>
    internal sealed record LastInputs(IReadOnlyList<TopWindowInfo> Windows,
        bool Locked, bool Remote, bool Battery, bool DisplayOff, bool Quns)
    {
        public bool SameExceptQuns(LastInputs other) =>
            Locked == other.Locked && Remote == other.Remote && Battery == other.Battery &&
            DisplayOff == other.DisplayOff && Windows.SequenceEqual(other.Windows);
    }

    /// <summary>Adds a host. The supervisor is NOT rebuilt for this: a rebuild would re-install
    /// the hooks and start every existing subscriber from empty state, re-firing transitions
    /// they had already reported. Instead the list is mutated in place, so a host joining or
    /// leaving costs one re-evaluation and nothing else. Evaluates immediately so the new host
    /// applies any condition that is already true (it missed the previous transitions).</summary>
    internal void AddSubscriber(PauseSubscription subscription)
    {
        lock (_gate)
        {
            if (_disposed) return;
            for (int i = 0; i < _subscribers.Length; i++)
            {
                if (ReferenceEquals(_subscribers[i], subscription)) return;
            }
            var next = new PauseSubscription[_subscribers.Length + 1];
            Array.Copy(_subscribers, next, _subscribers.Length);
            next[^1] = subscription;
            _subscribers = next;
            RebuildExclusions();
        }
        Reevaluate();
    }

    /// <summary>Removes a host. Only disposes the supervisor when the caller decides to (the
    /// shared owner does that, when the last host leaves) — disposing here would tear down the
    /// hooks while other subscribers are still attached.</summary>
    internal void RemoveSubscriber(PauseSubscription subscription)
    {
        lock (_gate)
        {
            if (_disposed) return;
            int at = Array.IndexOf(_subscribers, subscription);
            if (at < 0) return;
            if (_subscribers.Length == 1) return; // owner disposes; never leave zero here
            var next = new PauseSubscription[_subscribers.Length - 1];
            Array.Copy(_subscribers, next, at);
            Array.Copy(_subscribers, at + 1, next, at, _subscribers.Length - at - 1);
            _subscribers = next;
            RebuildExclusions();
        }
    }

    /// <summary>Recomputes the union of every subscriber's extra exclusions. An exclusion can
    /// only ever remove a window from consideration, and each host's own windows are already
    /// excluded by PID, so no host can lose coverage because another host excluded a class.</summary>
    private void RebuildExclusions()
    {
        HashSet<string>? union = null;
        foreach (var sub in _subscribers)
        {
            var extra = sub.ExtraExcludedWindowClasses;
            if (extra is null || extra.Count == 0) continue;
            union ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            union.UnionWith(extra);
        }
        _extraExcluded = union;
    }

    /// <param name="subscribers">Hosts to evaluate. At least one; the shared
    /// <see cref="PauseSupervision"/> only constructs a supervisor while at least one host is
    /// attached. Each keeps its own policy, monitor list, excluded classes and applied state —
    /// only the window capture and the system-flag read are shared.</summary>
    public PlaybackSupervisor(IReadOnlyList<PauseSubscription> subscribers)
    {
        if (subscribers is null) throw new ArgumentNullException(nameof(subscribers));
        if (subscribers.Count == 0) throw new ArgumentException("A supervisor needs at least one subscriber.", nameof(subscribers));
        _subscribers = subscribers.ToArray();
        lock (_gate) { RebuildExclusions(); }
        _winEventProc = OnWinEvent;
        HookRoots.TryAdd(_winEventProc, 0);
        _enumWindowsProc = OnEnumWindow;
        _locationDebounce = new Timer(_ => LocationDebounced(), null, Timeout.Infinite, Timeout.Infinite);
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "PlaybackEvents" };
        _hookThread.Start();
        // Bounded wait: close the gap where a foreground switch could land before the hooks
        // exist. The initial Reevaluate below covers the state regardless.
        _hookReady.Wait(TimeSpan.FromSeconds(10));
        Reevaluate();
    }

    /// <summary>Last evaluated pause reason for one subscriber's monitor (None when unknown).
    /// Thread-safe: diagnostics read this off-thread while hook/notification threads write it.</summary>
    internal PauseReason GetPauseReason(string subscriberName, string monitorDevice)
    {
        var sub = FindSubscriber(subscriberName);
        if (sub is null) return PauseReason.None;
        lock (_gate)
        {
            return sub.State.TryGetValue(monitorDevice, out var reason) ? reason : PauseReason.None;
        }
    }

    private PauseSubscription? FindSubscriber(string name)
    {
        foreach (var s in _subscribers)
        {
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    /// <summary>Forget one subscriber's cached per-monitor state and re-fire its transitions —
    /// call after creating content while a pause condition may already hold. Other subscribers
    /// keep their state (they were not invalidated by this host's content change).</summary>
    internal void Invalidate(PauseSubscription subscription)
    {
        if (FindSubscriber(subscription.Name) is null) return;
        lock (_gate) { subscription.State.Clear(); subscription.LastInputs = null; }
        Reevaluate();
    }

    /// <summary>Re-evaluate now, bypassing the storm throttle. For a pushed notification, which
    /// is rare and must apply at once. Distinct from the private throttled
    /// <see cref="RequestEvaluate"/> used by the WinEvent path.</summary>
    internal void EvaluateNow() => Reevaluate();

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
            // No TranslateMessage: this thread only pumps WinEvent callbacks, which arrive
            // already posted and never need WM_CHAR synthesis. Calling it would post extra
            // messages for any WM_KEYDOWN/WM_CHAR that ever crossed this queue.
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
                RequestEvaluate();
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
                RequestEvaluate();
                break;
        }
    }

    /// <summary>Single entry point for every WinEvent source: leading-edge immediate when
    /// quiet (first event evaluates at once, so pause latency never changes), throttled to
    /// one full evaluation per <see cref="EvalMinIntervalMs"/> with the overflow collapsing
    /// into a single trailing evaluation (see <see cref="LocationDebounced"/>). The per-window
    /// rect cache above is still updated on every LOCATIONCHANGE, so the trailing evaluation
    /// observes the settled geometry even though the intermediate ones were skipped.
    /// Direct calls (Resume/SessionLocked/DisplayOff/Invalidate) bypass this gate.</summary>
    private void RequestEvaluate()
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_disposed || _suspended) return;
            if (now - _lastEvalMs < EvalMinIntervalMs)
            {
                // Re-arm only on the transition into "armed". A storm delivers events far
                // faster than the interval, and re-arming an already-armed single-shot timer
                // is pure overhead — a timer-queue operation per event, which at drag rates
                // costs more than the evaluation it is protecting. The fixed deadline from
                // the first throttled event is also the better semantic: it evaluates the
                // settled state at most one interval after motion began.
                if (!_debounceArmed)
                {
                    _debounceArmed = true;
                    try { _locationDebounce.Change(EvalMinIntervalMs, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }
                return;
            }
            _lastEvalMs = now;
            // A leading evaluation already observes the latest inputs, so a previously armed
            // trailing edge would only re-evaluate identical state — disarm it.
            if (_debounceArmed)
            {
                _debounceArmed = false;
                try { _locationDebounce.Change(Timeout.Infinite, Timeout.Infinite); }
                catch (ObjectDisposedException) { }
            }
        }
        Reevaluate();
    }

    /// <summary>Runs on the pool (single-shot timer): the settled evaluation for a throttled
    /// storm. <see cref="Reevaluate"/> re-reads every input itself and early-outs when
    /// disposed/suspended, so this needs no additional guarding beyond the timestamp.</summary>
    private void LocationDebounced()
    {
        lock (_gate)
        {
            if (_disposed || _suspended) return;
            _lastEvalMs = Environment.TickCount64;
            _debounceArmed = false;
        }
        Reevaluate();
    }

    private void Reevaluate()
    {
        // Snapshot the subscriber list and the exclusion union once: both can change
        // underneath us if a host joins or leaves while this evaluation is running.
        PauseSubscription[] subscribers;
        IReadOnlySet<string>? extraExcluded;
        lock (_gate)
        {
            if (_disposed) return;
            subscribers = _subscribers;
            extraExcluded = _extraExcluded;
        }
        if (subscribers.Length == 0) return;

        // A subscriber that user-paused stops contributing, but the others keep running. If
        // that leaves nobody (e.g. the engine is user-paused and there is no desktop app),
        // skip the whole evaluation — including the window capture, which is the expensive part.
        bool anyActive = false;
        foreach (var s in subscribers)
        {
            if (!s.IsSuspended) { anyActive = true; break; }
        }
        if (!anyActive) return;

        // This runs on the hook thread, the engine thread, or a Win32 notification thread —
        // any of which may carry a different DPI context. The window rects captured here
        // must be in the same physical pixels as the monitor bounds.
        var awareness = User32.GetThreadDpiAwarenessContext();
        if (User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == IntPtr.Zero)
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);

        // Shared across subscribers: read once, capture at most once.
        var sharedRemote = User32.GetSystemMetrics(SM_REMOTESESSION) != 0;
        var sharedBattery = IsBatterySaverOn();
        var sharedQuns = IsD3DFullscreen();
        List<TopWindowInfo>? captured = null;
        List<(PauseSubscription Sub, string Device, PauseReason Reason, string By)> transitions = [];
        List<(PauseSubscription Sub, IReadOnlyList<TopWindowInfo>? Windows, SystemFlags Flags)> pendingLogs = [];
        try
        {
            foreach (var sub in subscribers)
            {
                if (sub.IsSuspended) continue;

                // Per subscriber: session lock and display state are pushed by that host's own
                // notifications, so a host with no supervisor at the moment cannot drop one.
                var flags = new SystemFlags(sub.SessionLocked, sharedRemote, sharedBattery, sharedQuns, sub.DisplayOff);
                var policy = sub.Policy();

                var monitors = new List<(string Device, RECT Bounds, RECT WorkArea, IntPtr Handle)>();
                foreach (var monitor in sub.Monitors())
                    monitors.Add((monitor.Device, monitor.Bounds, monitor.WorkArea, MonitorHandle(monitor.Bounds)));

                // Fast path: a globally-forced reason (or every trigger off) needs no window
                // capture at all. The QUNS hold-cache is dropped so the next full evaluation
                // after a forced interval always applies fresh instead of holding stale state.
                var forced = PauseDecision.ForcedReason(flags, policy);
                List<(string Device, PauseReason Reason, string By)> current;
                if (forced is not null)
                {
                    lock (_gate) { sub.LastInputs = null; }
                    current = [];
                    foreach (var (device, _, _, _) in monitors)
                        current.Add((device, forced.Value, "-"));
                    pendingLogs.Add((sub, null, flags));
                }
                else
                {
                    // The capture is the expensive part and is identical for every subscriber;
                    // take it at most once per evaluation.
                    captured ??= CaptureTopWindows();
                    var inputs = new LastInputs(captured, flags.SessionLocked, flags.RemoteSession, flags.BatterySaver, flags.DisplayOff, flags.D3DFullscreen);
                    bool hold;
                    lock (_gate)
                    {
                        // Lone QUNS flip (window-for-window identical): hold this subscriber,
                        // touch nothing. Per subscriber, not global: a flip counts as "lone"
                        // only relative to what that subscriber actually observed.
                        hold = sub.LastInputs is not null && sub.LastInputs.SameExceptQuns(inputs);
                        if (!hold) sub.LastInputs = inputs;
                    }
                    if (hold) continue;

                    current = [];
                    foreach (var (device, bounds, workArea, handle) in monitors)
                    {
                        var reason = PauseDecision.EvaluateForMonitor(captured, bounds, workArea, handle, flags, policy, extraExcluded);
                        string by = "-";
                        if (reason == PauseReason.Fullscreen)
                        {
                            foreach (var w in captured)
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
                    pendingLogs.Add((sub, captured, flags));
                }

                lock (_gate)
                {
                    if (_disposed) break;
                    foreach (var (device, reason, by) in current)
                    {
                        if (!sub.State.TryGetValue(device, out var previous) || previous != reason)
                        {
                            sub.State[device] = reason;
                            transitions.Add((sub, device, reason, by));
                        }
                    }
                    // Drop unplugged monitors so stale entries cannot misfire on reconnect.
                    foreach (var device in sub.State.Keys.ToArray())
                    {
                        bool gone = true;
                        foreach (var (d, _, _) in current)
                        {
                            if (string.Equals(d, device, StringComparison.OrdinalIgnoreCase)) { gone = false; break; }
                        }
                        if (gone) sub.State.Remove(device);
                    }
                }
            }

            // The rect cache tracks coalescing for the next event storm, so it follows the
            // capture rather than any one subscriber's decision path.
            if (captured is not null)
            {
                lock (_gate)
                {
                    _lastRects.Clear();
                    foreach (var w in captured) _lastRects[w.Hwnd] = w.WindowRect;
                }
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

        if (transitions.Count == 0) return;
        // A Suspend landing mid-evaluation must still win: applying transitions now would
        // resume content behind the user's manual pause.
        foreach (var (sub, device, reason, by) in transitions)
        {
            if (sub.IsSuspended || _disposed) continue;
            // Foreground identity is log-only: resolve it last, and only when something
            // actually transitioned — never on the hot path.
            var canLogInfo = Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information);
            string inputDesc = string.Empty;
            if (canLogInfo)
            {
                foreach (var (s, windows, flags) in pendingLogs)
                {
                    if (!ReferenceEquals(s, sub)) continue;
                    inputDesc = DescribeInputs(windows, flags);
                    break;
                }
            }
            Serilog.Log.Information("Pause transition [{Host}]: {device} -> {reason} by={by} ({inputDesc})",
                sub.Name, device, reason, by, inputDesc);
            try { sub.OnTransition(device, reason); }
            catch (Exception ex) { Serilog.Log.Error("Pause transition handler failed", ex); }
        }
    }

    /// <summary>Log-only input summary (foreground window, capture size, flags). Runs after the
    /// decision, and only when a transition actually fired.</summary>
    private static string DescribeInputs(IReadOnlyList<TopWindowInfo>? windows, SystemFlags flags)
    {
        var fgHwnd = User32.GetForegroundWindow();
        string fgDesc = "none";
        if (fgHwnd != IntPtr.Zero)
        {
            if (windows is not null)
            {
                foreach (var w in windows)
                {
                    if (w.Hwnd == fgHwnd) { fgDesc = w.ClassName; break; }
                }
            }
            if (fgDesc == "none")
            {
                // No capture in the forced path (windows null): report the plain class with
                // no exclusion claim. With a capture, absence from the list means excluded.
                var sb = new StringBuilder(64);
                if (User32.GetClassNameW(fgHwnd, sb, sb.Capacity) > 0)
                    fgDesc = windows is null ? sb.ToString() : sb.ToString() + " (excluded)";
                else fgDesc = "unknown";
            }
        }
        string wins = windows is null ? "skipped" : windows.Count.ToString();
        return $"fg=0x{fgHwnd:X} {fgDesc} wins={wins} | quns={flags.D3DFullscreen} batt={flags.BatterySaver} rem={flags.RemoteSession} lock={flags.SessionLocked} disp={flags.DisplayOff}";
    }

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Every visible, non-minimized, non-cloaked top-level window outside our own
    /// process, the shell, and non-occluding overlays. Runs synchronously on the caller's
    /// thread (awareness pinned by Reevaluate, for life by the hook thread).</summary>
    private List<TopWindowInfo> CaptureTopWindows()
    {
        _capturing = new List<TopWindowInfo>(64);
        List<TopWindowInfo>? list = _capturing;
        try { User32.EnumWindows(_enumWindowsProc, IntPtr.Zero); }
        catch { }
        finally { _capturing = null; }
        // Prune the class-name memo to exactly what this capture saw. Without this, a
        // destroyed window's HWND could be recycled by a new window of a different class and
        // serve the stale name. A capture has just enumerated every live top-level window,
        // so "live" is precisely the set we are about to hand back.
        if (_classNames.Count > list.Count)
        {
            var live = new HashSet<IntPtr>(list.Count);
            foreach (var w in list) live.Add(w.Hwnd);
            foreach (var hwnd in _classNames.Keys)
            {
                if (!live.Contains(hwnd)) _classNames.TryRemove(hwnd, out _);
            }
        }
        return list;
    }

    private bool OnEnumWindow(IntPtr hwnd, IntPtr _)
    {
        // The callback is a field-held method (not a closure over a local) so a capture
        // allocates nothing; the list it fills therefore has to travel through a field.
        var list = _capturing;
        if (list is null) return true; // no capture in flight (stale callback)
        try
        {
            if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;
            // Never pause for our own windows: interacting with
            // the app itself must not count as covering the content.
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnPid) return true;
            if (!User32.GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            if (!_classNames.TryGetValue(hwnd, out string? cls))
            {
                var sb = _classNameBuffer ??= new StringBuilder(64);
                sb.Clear();
                if (User32.GetClassNameW(hwnd, sb, sb.Capacity) == 0) return true;
                cls = sb.ToString();
                _classNames[hwnd] = cls;
            }
            if (PauseDecision.IsShellOrOwnWindow(cls, _extraExcluded)) return true;
            // Fullscreen click-through or fully-transparent overlays (cursor overlays,
            // watermarks, crosshairs) never occlude what is beneath them: pausing the
            // wallpaper and hiding widgets for an invisible window is wrong.
            if (IsNonOccludingOverlay(hwnd)) return true;
            // Suspended Store apps keep stale fullscreen rects while invisible.
            if (IsCloaked(hwnd)) return true;
            list.Add(new TopWindowInfo(hwnd, cls, rect, User32.IsZoomed(hwnd), User32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)));
        }
        catch { }
        return true;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try { return DwmApi.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch { return false; }
    }

    /// <summary>True for top-level windows that cannot hide content: click-through
    /// (<c>WS_EX_TRANSPARENT</c>, never takes mouse input — the standard fullscreen cursor /
    /// watermark / crosshair overlay shape) or layered with zero alpha (paints nothing).
    /// An opaque click-through window is pathological; the common case this excludes is an
    /// invisible overlay permanently "covering" a monitor.</summary>
    private static bool IsNonOccludingOverlay(IntPtr hwnd)
    {
        try
        {
            int ex = User32.GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & (int)WS_EX_TRANSPARENT) != 0) return true;
            if ((ex & (int)WS_EX_LAYERED) != 0 &&
                User32.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _) &&
                alpha == 0)
                return true;
        }
        catch { }
        return false;
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
        bool first;
        lock (_gate)
        {
            first = !_disposed;
            _disposed = true;
        }
        if (first)
        {
            _suspended = true;
            // Stop the trailing edge first: an in-flight callback still early-outs on _disposed.
            try { _locationDebounce.Dispose(); } catch { }
            // Every cached class name is a string this supervisor rooted; drop them with it.
            try { _classNames.Clear(); } catch { }
            try
            {
                uint threadId = _hookThreadId;
                if (threadId != 0)
                    User32.PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hookThread?.Join(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
        // Idempotent, so attempted on every call: with the hook thread joined above, its
        // message queue — including any already-queued hook callbacks, which early-out on
        // _disposed — is drained and the thread is dead, so no callback can land afterward.
        // Unhook explicitly (thread death also removes them, eventually), then release the
        // process-lifetime root: without this, every supervisor — with its policy/monitor
        // closures reaching into the host — stays reachable forever, one per enable/disable
        // cycle. (The sibling root in DesktopLayerHostBase stays by design: its hook lives
        // on a pumping UI thread that outlives the unhook, so already-queued callbacks could
        // still need the delegate after disposal.)
        if (_hookThread is null || !_hookThread.IsAlive)
        {
            foreach (var hook in _hooks)
            {
                try { User32.UnhookWinEvent(hook); } catch { }
            }
            _hooks.Clear();
            HookRoots.TryRemove(_winEventProc, out _);
        }
        if (first)
        {
            try { _hookReady.Dispose(); } catch { }
        }
    }
}
