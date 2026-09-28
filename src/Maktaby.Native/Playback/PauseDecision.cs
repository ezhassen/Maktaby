namespace Maktaby.Native.Playback;

public sealed record ForegroundInfo(string ClassName, RECT WindowRect, bool IsZoomed, IntPtr MonitorHandle);

/// <summary>A visible top-level candidate window for coverage checks — foreground or not.
/// Minimized, cloaked, shell and excluded windows never make it into the capture.</summary>
public sealed record TopWindowInfo(IntPtr Hwnd, string ClassName, RECT WindowRect, bool IsZoomed, IntPtr MonitorHandle);

/// <summary><paramref name="DisplayOff"/> comes from GUID_CONSOLE_DISPLAY_STATE as a pushed
/// notification, not from a poll.</summary>
public sealed record SystemFlags(bool SessionLocked, bool RemoteSession, bool BatterySaver, bool D3DFullscreen,
    bool DisplayOff = false);

public enum PauseReason { None, Fullscreen, SessionLocked, RemoteSession, BatterySaver, DisplayOff, ByUser }

/// <summary>Which pause triggers are armed. Each consumer maps its own settings object to
/// this (the live-wallpaper engine maps its persisted PauseConfig; the desktop app maps
/// its user settings) so the decision never depends on either.</summary>
public sealed class PausePolicy
{
    public bool OnFullscreen { get; set; } = true;
    public bool OnBatterySaver { get; set; } = true;
    public bool OnRemoteSession { get; set; } = true;
}

/// <summary>Config-file shape for pause triggers (everything on by default). Each host
/// persists it its own way (engine: EngineConfig.Pause; desktop: UserSettings) and maps it
/// through <see cref="ToPolicy"/> at evaluation time, so the decision never depends on
/// either. Unsealed so the engine can subclass it for its persisted config (same JSON).</summary>
public class PauseConfig
{
    public bool OnFullscreen { get; set; } = true;
    public bool OnBatterySaver { get; set; } = true;
    public bool OnRemoteSession { get; set; } = true;

    public PausePolicy ToPolicy() => new()
    {
        OnFullscreen = OnFullscreen,
        OnBatterySaver = OnBatterySaver,
        OnRemoteSession = OnRemoteSession,
    };
}

/// <summary>Pure pause policy — no Win32 calls, fully unit-testable.</summary>
public static class PauseDecision
{
    /// <summary>Shell windows, always ignored by every consumer. Consumer-owned classes
    /// (the live-wallpaper surface, Box windows, …) arrive per-call via
    /// <paramref name="extraExcludedClasses"/> instead — each host passes only its own.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "SHELLDLL_DefView", "SysListView32",
    };

    public const double CoverageThreshold = 0.95;

    public static bool IsShellOrOwnWindow(string className, IReadOnlySet<string>? extraExcludedClasses = null) =>
        ShellClasses.Contains(className) || (extraExcludedClasses?.Contains(className) == true);

    /// <summary>True when the foreground window effectively hides the desktop of the given
    /// monitor: maximized on it, or covering ≥95% of its work area.</summary>
    public static bool CoversMonitor(in RECT windowRect, in RECT monitorBounds, in RECT monitorWorkArea, bool isZoomed, IntPtr windowMonitor, IntPtr monitor)
    {
        if (isZoomed) return windowMonitor == monitor;
        if (!windowRect.IntersectsWith(monitorWorkArea)) return false;
        long covered = windowRect.IntersectionArea(monitorWorkArea);
        return covered >= monitorWorkArea.Area * CoverageThreshold;
    }

    public static PauseReason Evaluate(
        ForegroundInfo? foreground,
        in RECT monitorBounds,
        in RECT monitorWorkArea,
        IntPtr monitorHandle,
        SystemFlags flags,
        PausePolicy policy,
        IReadOnlySet<string>? extraExcludedClasses = null)
    {
        // Legacy single-window entry: the foreground window is simply the only candidate.
        // (Kept for the polling monitor; the supervisor evaluates the full window list.)
        List<TopWindowInfo> windows = [];
        if (foreground is not null && !IsShellOrOwnWindow(foreground.ClassName, extraExcludedClasses))
            windows.Add(new TopWindowInfo(IntPtr.Zero, foreground.ClassName, foreground.WindowRect, foreground.IsZoomed, foreground.MonitorHandle));
        return EvaluateForMonitor(windows, monitorBounds, monitorWorkArea, monitorHandle, flags, policy, extraExcludedClasses);
    }

    /// <summary>Globally-forced reason when a system state outranks every window (or
    /// <see cref="PauseReason.None"/> when the policy has every trigger off): the caller can
    /// apply it to all monitors without capturing windows. Null when a covering window could
    /// still matter — the caller must run the full evaluation then.</summary>
    public static PauseReason? ForcedReason(SystemFlags flags, PausePolicy policy)
    {
        if (flags.DisplayOff) return PauseReason.DisplayOff;
        if (flags.SessionLocked) return PauseReason.SessionLocked;
        if (policy.OnRemoteSession && flags.RemoteSession) return PauseReason.RemoteSession;
        if (policy.OnBatterySaver && flags.BatterySaver) return PauseReason.BatterySaver;
        if (!policy.OnFullscreen) return PauseReason.None;
        return null;
    }

    /// <summary>A monitor pauses when ANY qualifying top-level window covers it — a fullscreen app
    /// keeps its monitor paused even while unfocused (e.g. the user clicked over to the other
    /// screen). Foreground-only checks wrongly resume the fullscreen monitor the moment focus
    /// leaves it, which is exactly the multi-monitor flap in the field.</summary>
    public static PauseReason EvaluateForMonitor(
        IReadOnlyList<TopWindowInfo> windows,
        in RECT monitorBounds,
        in RECT monitorWorkArea,
        IntPtr monitorHandle,
        SystemFlags flags,
        PausePolicy policy,
        IReadOnlySet<string>? extraExcludedClasses = null)
    {
        // Outranks everything, and is not configurable: there is no reading of "pause on
        // fullscreen: off" under which the user wants frames decoded into a dark panel.
        if (flags.DisplayOff) return PauseReason.DisplayOff;
        if (flags.SessionLocked) return PauseReason.SessionLocked;
        if (policy.OnRemoteSession && flags.RemoteSession) return PauseReason.RemoteSession;
        if (policy.OnBatterySaver && flags.BatterySaver) return PauseReason.BatterySaver;
        if (!policy.OnFullscreen) return PauseReason.None;
        if (flags.D3DFullscreen) return PauseReason.Fullscreen;
        foreach (var w in windows)
        {
            if (CoversMonitor(w.WindowRect, monitorBounds, monitorWorkArea, w.IsZoomed, w.MonitorHandle, monitorHandle))
                return PauseReason.Fullscreen;
        }
        return PauseReason.None;
    }
}
