using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsNative.Playback;

/// <summary>
/// One host's registration in the shared <see cref="PauseSupervision"/>: what to pause, which
/// monitors, which window classes to ignore, and where transitions go.
/// <para>
/// The whole point of the shared supervisor is that hosts do NOT have to agree on their
/// <see cref="PausePolicy"/>. The expensive part of an evaluation is the window capture
/// (<c>EnumWindows</c> plus several cross-process calls per window) and the system-flag read;
/// the per-monitor decision is pure arithmetic over the already-captured list. So hosts share
/// the capture and keep their own policy, which means each host's separately persisted
/// settings keep working exactly as before. Merging the policies (OR-ing them) would silently
/// change what pauses when.
/// </para>
/// <para>
/// This type is also the host's durable handle: <see cref="SessionLocked"/>,
/// <see cref="DisplayOff"/> and <see cref="Suspended"/> live here rather than on the
/// supervisor, so they survive the supervisor being rebuilt when another host comes or goes
/// (the supervisor is torn down and recreated whenever the subscriber set changes).
/// </para></summary>
public sealed class PauseSubscription : IDisposable
{
    private readonly object _gate = new();
    private bool _sessionLocked;
    private bool _displayOff;
    private bool _suspended;
    private PauseSupervision? _owner;

    /// <summary>Applied pause reason per monitor for THIS host. Lives on the subscription, not on
    /// the supervisor, so a host joining or leaving never resets another host's state (and never
    /// re-fires transitions it already reported). Written only under the owning supervisor's
    /// evaluation gate, read via <see cref="GetPauseReason"/>.</summary>
    internal readonly Dictionary<string, PauseReason> State = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>QUNS hold-cache for this host. Per host, not global: a flip counts as "lone"
    /// only relative to what this host actually observed, so one host's coincidental flap must
    /// never hold back another host's real transition.</summary>
    internal PlaybackSupervisor.LastInputs? LastInputs;

    /// <param name="name">Stable identity, used for logging and for the supervisor to find this
    /// subscription's state. Must be unique among live subscribers.</param>
    /// <param name="policy">Live policy snapshot, read fresh on every evaluation.</param>
    /// <param name="monitors">Live monitor snapshot (device, bounds, work area).</param>
    /// <param name="extraExcludedWindowClasses">Host-owned window classes that must never pause
    /// a monitor. Pass only your own classes.</param>
    /// <param name="onTransition">Called on the supervisor's hook or notification thread —
    /// marshal to your own UI thread.</param>
    public PauseSubscription(
        string name,
        Func<PausePolicy> policy,
        Func<IReadOnlyList<PauseMonitor>> monitors,
        Action<string, PauseReason> onTransition,
        IReadOnlySet<string>? extraExcludedWindowClasses = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        OnTransition = onTransition ?? throw new ArgumentNullException(nameof(onTransition));
        ExtraExcludedWindowClasses = extraExcludedWindowClasses;
    }

    public string Name { get; }
    public Func<PausePolicy> Policy { get; }
    public Func<IReadOnlyList<PauseMonitor>> Monitors { get; }
    public IReadOnlySet<string>? ExtraExcludedWindowClasses { get; }
    public Action<string, PauseReason> OnTransition { get; }

    /// <summary>Set from session-change notifications; assigning re-evaluates immediately
    /// (unless this subscription is suspended — the pending state is picked up by
    /// <see cref="Resume"/>). Held per subscription so one host's notifications cannot be
    /// dropped because it has no supervisor at the moment.</summary>
    public bool SessionLocked
    {
        get { lock (_gate) return _sessionLocked; }
        set { lock (_gate) _sessionLocked = value; _owner?.RequestEvaluate(); }
    }

    /// <summary>Set from console-display-state notifications; assigning re-evaluates
    /// immediately. Per subscription, for the same reason as <see cref="SessionLocked"/>.</summary>
    public bool DisplayOff
    {
        get { lock (_gate) return _displayOff; }
        set { lock (_gate) _displayOff = value; _owner?.RequestEvaluate(); }
    }

    /// <summary>True while this host has user-paused its content. Only this host's transitions
    /// are suppressed — the other subscribers keep evaluating, which is why suspension lives
    /// per subscription and not on the supervisor.</summary>
    public bool IsSuspended
    {
        get { lock (_gate) return _suspended; }
    }

    /// <summary>Stop this host's auto-evaluation (its user-pause). Idempotent, thread-safe.
    /// The supervisor keeps evaluating for the other subscribers, and skips the window capture
    /// entirely if that leaves nobody.</summary>
    public void Suspend()
    {
        lock (_gate) _suspended = true;
        _owner?.RequestEvaluate();
    }

    /// <summary>Clears this host's suspension and re-evaluates immediately (idempotent,
    /// thread-safe). Caches for this subscription are dropped so the post-suspend world is
    /// observed fresh rather than held.</summary>
    public void Resume()
    {
        lock (_gate) _suspended = false;
        _owner?.Invalidate(this);
    }

    /// <summary>Forget this host's cached per-monitor state and re-fire transitions — call
    /// after creating content while a pause condition may already hold.</summary>
    public void Invalidate() => _owner?.Invalidate(this);

    /// <summary>Last evaluated pause reason for a monitor (None when unknown, or when this
    /// subscription is not currently attached). Thread-safe: diagnostics read this off-thread
    /// while hook/notification threads write it.</summary>
    public PauseReason GetPauseReason(string monitorDevice)
    {
        var owner = _owner;
        return owner is null ? PauseReason.None : owner.GetReason(this, monitorDevice);
    }

    /// <summary>Detaches from the shared supervisor. Idempotent.</summary>
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.Unsubscribe(this);
    }

    internal void Attach(PauseSupervision owner) => Volatile.Write(ref _owner, owner);
    internal void Detach(PauseSupervision owner) => Interlocked.CompareExchange(ref _owner, null, owner);
}

/// <summary>
/// Process-scoped owner of the single shared <see cref="PlaybackSupervisor"/>. Hosts subscribe;
/// the supervisor is created on the first subscriber and disposed on the last, so an app with
/// neither live wallpapers nor desktop containers runs no hook thread at all.
/// <para>
/// Subscribing and unsubscribing mutates the live supervisor in place. Rebuilding it instead
/// would be wrong twice over: it would re-install the WinEvent hook set, and it would start
/// every already-attached host from empty state, re-firing transitions it had already
/// reported. The per-subscription applied state lives on the <see cref="PauseSubscription"/>,
/// so hosts that stay attached keep their state untouched.
/// </para>
/// </summary>
public sealed class PauseSupervision
{
    /// <summary>The shared instance. Process-global by nature: it exists to deduplicate work
    /// across two hosts in one process, and both the engine and the desktop manager reach it
    /// without a DI hop.</summary>
    public static PauseSupervision Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<PauseSubscription> _subscribers = new();
    private PlaybackSupervisor? _supervisor;

    /// <summary>Live subscriber count. 0 means no hook thread is running.</summary>
    public int SubscriberCount
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    /// <summary>Attaches a host. Returns the same subscription, which the caller disposes to
    /// detach. Throws on a duplicate <see cref="PauseSubscription.Name"/> — a silent second
    /// subscriber with the same name would share state with the first and corrupt its
    /// transition diffing.</summary>
    public PauseSubscription Subscribe(PauseSubscription subscription)
    {
        if (subscription is null) throw new ArgumentNullException(nameof(subscription));
        lock (_gate)
        {
            foreach (var existing in _subscribers)
            {
                if (string.Equals(existing.Name, subscription.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"A pause subscriber named '{subscription.Name}' is already registered.");
            }
            _subscribers.Add(subscription);
            subscription.Attach(this);
            // Mutate the live supervisor in place. Rebuilding it here would re-install the
            // hook set and start every already-attached host from empty state, re-firing
            // transitions it had already reported. Only the very first host pays a construction.
            bool created = _supervisor is null;
            if (created) _supervisor = new PlaybackSupervisor(_subscribers);
            else _supervisor.AddSubscriber(subscription);
            Serilog.Log.Information("Pause supervision: '{Host}' attached ({Count} active, {Action})",
                subscription.Name, _subscribers.Count, created ? "started hooks" : "reused hooks");
        }
        return subscription;
    }

    internal void Unsubscribe(PauseSubscription subscription)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(subscription)) return;
            subscription.Detach(this);
            // Last host out disposes the supervisor, which is what actually stops the hook
            // thread and its pump. With others still attached, just drop this one.
            if (_subscribers.Count == 0)
            {
                _supervisor?.Dispose();
                _supervisor = null;
                Serilog.Log.Information("Pause supervision: '{Host}' detached (0 active, stopped hooks)",
                    subscription.Name);
            }
            else
            {
                _supervisor?.RemoveSubscriber(subscription);
                Serilog.Log.Information("Pause supervision: '{Host}' detached ({Count} active, hooks kept)",
                    subscription.Name, _subscribers.Count);
            }
        }
    }

    /// <summary>Disposes the shared supervisor and forgets every subscriber. App teardown only
    /// — hosts normally detach by disposing their own subscription.</summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            foreach (var sub in _subscribers) sub.Detach(this);
            _subscribers.Clear();
            _supervisor?.Dispose();
            _supervisor = null;
        }
    }

    /// <summary>Unconditional re-evaluation, for a pushed notification that may change several
    /// subscribers at once. The capture is shared, so re-evaluating everyone costs no more than
    /// re-evaluating one.</summary>
    internal void RequestEvaluate() => _supervisor?.EvaluateNow();

    /// <summary>Drops one subscription's cached state and re-evaluates. Used by
    /// <see cref="PauseSubscription.Resume"/>/<see cref="PauseSubscription.Invalidate"/>.</summary>
    internal void Invalidate(PauseSubscription subscription) => _supervisor?.Invalidate(subscription);

    internal PauseReason GetReason(PauseSubscription subscription, string monitorDevice)
    {
        var supervisor = _supervisor;
        if (supervisor is null) return PauseReason.None;
        return supervisor.GetPauseReason(subscription.Name, monitorDevice);
    }
}
