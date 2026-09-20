using DesktopLiveWallPaperEngine.Common;
using DesktopLiveWallPaperEngine.Config;
using DesktopLiveWallPaperEngine.Desktop;
using DesktopLiveWallPaperEngine.Interop;
using DesktopLiveWallPaperEngine.Rendering;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using WindowsNative;
using WindowsNative.Desktop;
using WindowsNative.Playback;
using static WindowsNative.Win32Constants;

namespace DesktopLiveWallPaperEngine;

/// <summary>Wires everything together: desktop layer, per-monitor wallpaper windows,
/// clock widget, pause monitor, tray UI, config persistence.</summary>
public sealed class Engine : IDisposable
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Entry-point extensions for HTML wallpapers (WebviewRender). A folder whose
    /// index.html is used also counts — see <see cref="IsWebPath"/>.</summary>
    public static readonly IReadOnlySet<string> WebExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".css", ".js" };

    public static bool IsWebPath(string path) =>
        Directory.Exists(path) || WebExtensions.Contains(Path.GetExtension(path));

    private readonly EngineConfig _config;
    private readonly string _appDataDir;
    private DesktopWallpaperLayerHost? _host;
    private readonly ConcurrentDictionary<string, WallpaperWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private DeviceLossGuard? _deviceLoss;
    private readonly uint _taskbarCreatedMessage = User32.RegisterWindowMessageW("TaskbarCreated");

    private MessageWindow? _messageWindow;
    private PowerNotifications? _power;

    private PlaybackSupervisor? _playback;
    private bool _reapplying;
    private string _originalWallpaper = "";

    /// <summary>Guards <see cref="_teardownTask"/>. All public engine methods run on the
    /// main thread; only the teardown body runs on the pool, touching its snapshot.</summary>
    private readonly object _gate = new();
    private Task? _teardownTask;

    /// <summary>Bumped on Disable/ReapplyAll: transitions computed by an in-flight poll
    /// before the bump name windows that no longer exist, and must not land on the
    /// same-named fresh windows created after it.</summary>
    private int _pauseEpoch;

    /// <summary>User's manual pause (PlayPause sets, PlayStart clears). Preserved across
    /// supervisor recreation so an all-static interval cannot silently lose it.</summary>
    private bool _userPaused;

    /// <summary>Convergence backstop for display-topology changes. A display notification can
    /// arrive before the new monitor enumerates and before Explorer resizes the layer parent:
    /// the immediate re-apply then attaches the new window against stale geometry (fully
    /// outside the parent → clipped → black, or missing entirely) with nothing re-checking
    /// afterwards — until now, only a manual disable/enable healed it. The trailing pass
    /// rebuilds, but only when the live windows still disagree with the settled OS topology,
    /// so a converged immediate pass costs one cheap comparison and no flash.</summary>
    private readonly object _settleGate = new();
    private System.Threading.Timer? _settleTimer;
    private int _settleGen;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>Post-resume settle: after hibernate/sleep the display driver and the shell
    /// need longer than a hot-plug to converge (mode handshake, layer parent resize), so a
    /// resume-armed trailing pass waits longer before judging the topology.</summary>
    private static readonly TimeSpan ResumeSettleDelay = TimeSpan.FromSeconds(10);

    /// <summary>Native video-memory stranding, measured 2026-09-20 from a 2.3 GB dump + ETL:
    /// destroying an active frame-server MediaPlayer orphans ~32 MB of Intel GPU driver video
    /// memory per monitor (d3d9 → igd9trinity64 → dxgkrnl write-combined private mappings with
    /// no managed owner left behind — managed teardown is complete, threads are reaped). Only
    /// a driver reset reclaims them, so every full pipeline destroy has a permanent ~32 MB /
    /// monitor cost. The fields below implement the two consequences: (a) notification storms
    /// must never destroy pipelines more than once, and (b) after a resume, one debounced
    /// recycle is allowed when native memory is actually elevated — never blindly.</summary>
    private const long BytesPerStrandedPool = 32L * 1024 * 1024;

    /// <summary>Absolute private-bytes level that justifies a post-resume recycle. Normal
    /// steady state for two 1080p video wallpapers is a few hundred MB; 1–3 GB is the
    /// reported post-hibernation blowup.</summary>
    private const long RecyclePrivateBytesThreshold = 900L * 1024 * 1024;

    /// <summary>Growth since the resume baseline that justifies a post-resume recycle even
    /// below the absolute threshold (fast post-resume stranding: ~22 pools / 107 s measured).</summary>
    private const long RecycleGrowthBytesThreshold = 350L * 1024 * 1024;

    /// <summary>Post-resume recycles are once per resume plus a cooldown — a recycle that did
    /// not help must not loop the desktop forever.</summary>
    private static readonly TimeSpan RecycleCooldown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ResumeRecycleWindow = TimeSpan.FromMinutes(15);

    private DateTime _lastResumeUtc = DateTime.MinValue;
    private long _resumeBaselinePrivateBytes;
    private bool _resumeRecycled;
    private DateTime _lastRecycleUtc = DateTime.MinValue;

    private string StaticDir => Path.Combine(_appDataDir, "static");
    private string OriginalWallpaperFile => Path.Combine(_appDataDir, "original-wallpaper.txt");
    private string OriginalDesktopWallpapersFile => Path.Combine(_appDataDir, "original-vd-wallpapers.tsv");
    private string StaticPath(string device) =>
        Path.Combine(StaticDir, new string(device.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".png");

    public Engine(EngineConfig config, string appDataDir)
    {
        _config = config;
        _appDataDir = appDataDir;
        _preload = new PreloadedMediaCache(config.PreloadMaxBytes);
    }

    public bool IsEnabled { get; private set; }

    /// <summary>Shared in-memory media store (videos + animated GIFs under the cap).</summary>
    public PreloadedMediaCache PreloadCache => _preload;
    private readonly PreloadedMediaCache _preload;

    /// <summary>Live-updates the preload cap (settings change); over-cap unreferenced bytes drop.</summary>
    public void SetPreloadCap(long maxBytes)
    {
        try { _preload.MaxBytes = maxBytes; } catch { }
    }

    /// <summary>Total preloaded bytes currently pinned (shared across monitors).</summary>
    public long PreloadTotalBytes()
    {
        try { return _preload.TotalBytes; } catch { return 0; }
    }

    /// <summary>
    /// Start/Enable the engine. Waits (bounded) for a previous background teardown:
    /// it owns the original-wallpaper files this method re-reads, and its windows
    /// must be gone before new ones attach.
    /// </summary>
    public void Enable()
    {
        // Re-entrancy guard: a second Enable over a live session would orphan the message
        // window, power registrations, layer host, GPU device and hook thread it replaces.
        // All refresh paths go through RefreshWallpapers/ReapplyAll instead.
        if (IsEnabled)
        {
            Serilog.Log.Information("Enable called while already enabled — ignoring");
            return;
        }
        // New session, new generation: transitions computed before this point name
        // windows from older sessions and must be dropped (see _pauseEpoch).
        Interlocked.Increment(ref _pauseEpoch);
        Task? previous;
        lock (_gate) previous = _teardownTask;
        if (previous is not null && !previous.IsCompleted)
        {
            try
            {
                if (!previous.Wait(TimeSpan.FromSeconds(10)))
                    Serilog.Log.Warning("Previous teardown still running after 10 s — enabling anyway");
            }
            catch (Exception ex) { Serilog.Log.Warning($"Teardown wait failed: {ex.Message}"); }
        }
        _messageWindow = new MessageWindow(this);
        WtsApi32.WTSRegisterSessionNotification(_messageWindow.Hwnd, WtsApi32.NOTIFY_FOR_THIS_SESSION);
        _power = new PowerNotifications(_messageWindow.Hwnd);

        SaveOriginalWallpaper();

        // An explorer restart or display change rebuilds the layer for reasons unrelated to the
        // GPU, and produces a fresh device anyway — so it clears any device-loss failure history.
        _host = new();
        _deviceLoss = new();
        _host.LayerLost += OnLayerLost;
        _host.EnsureLayer();

        //_tray = new TrayIcon(_messageWindow.Hwnd);

        // Supervision BEFORE creating renderers: the pause state (e.g. a fullscreen app
        // already covering the desktop) must exist when ApplyToMonitor enforces it post-Load.
        // The supervisor only fires on transitions — a pre-existing fullscreen produces none,
        // so creating renderers first left them Playing under it with no event ever coming.
        EnsurePlayback();
        ApplyFromConfig();
        IsEnabled = true;
        // Only when something can actually pause: static images cost nothing when covered,
        // so an all-static session runs no hook thread and evaluates nothing. The fresh
        // supervisor's own initial evaluation covers the current state (renderers already
        // exist, unlike the old create-before-apply order that needed an Invalidate).
        UpdatePauseSupervision();
        // The layer parent can still be settling (lazy WorkerW sizing after logon, display
        // handshake): converge it the same way display changes do, instead of leaving a
        // mis-attached monitor black until the next notification.
        ScheduleSettledReapply();
    }

    /// <summary>Creates the pause supervisor unless one is already live. Carries over the
    /// user's manual pause so a supervision gap (all-static interval) cannot lose it.</summary>
    private void EnsurePlayback()
    {
        if (_playback is not null) return;
        var playback = new PlaybackSupervisor(
            // The supervisor reads a shared PausePolicy; map the persisted config each time.
            () => new PausePolicy
            {
                OnFullscreen = _config.Pause.OnFullscreen,
                OnBatterySaver = _config.Pause.OnBatterySaver,
                OnRemoteSession = _config.Pause.OnRemoteSession,
            },
            () => MonitorTracker.Enumerate()
                .Select(m => new PauseMonitor(m.Device, m.Bounds, m.WorkArea))
                .ToList(),
            // Our own surface window must never pause a monitor — engine-only exclusion
            // (the desktop app passes its own classes instead).
            new HashSet<string>([WallpaperWindow.ClassName], StringComparer.OrdinalIgnoreCase));
        playback.PauseStateChanged += (device, reason) =>
        {
            var epoch = Volatile.Read(ref _pauseEpoch);
            RunOnMainThread(() =>
            {
                if (epoch != Volatile.Read(ref _pauseEpoch)) return;
                OnPauseStateChanged(device, reason);
            });
        };
        if (_userPaused) playback.Suspend();
        _playback = playback;
    }

    /// <summary>Aligns the supervisor with the live renderer mix: video, web and animated GIF
    /// need pause supervision; static images never do. Called after every apply loop so only
    /// mix transitions (not repeat applies) pay the hook-thread teardown/startup.</summary>
    private void UpdatePauseSupervision()
    {
        if (!IsEnabled) return;
        bool need = false;
        foreach (var window in _windows.Values)
        {
            if (window.Renderer is VideoRenderer or WebViewRenderer) { need = true; break; }
            if (window.Renderer is ImageRenderer image && image.IsAnimated) { need = true; break; }
        }
        if (need) EnsurePlayback();
        else if (_playback is not null)
        {
            _playback.Dispose();
            _playback = null;
        }
    }
    public void EnsureEnabled()
    {
        if (!IsEnabled) Enable();
    }

    /// <summary>
    /// Stop/Disable the engine. Returns fast: the playback monitor stops and the field
    /// snapshot is taken synchronously, while MF pipeline teardown, D3D release and the
    /// SetWallpaper restore run on the thread pool. Previously all of that blocked the
    /// calling (UI) thread — MF topology teardown and the shell wallpaper broadcast are
    /// the stalls users felt as a hang.
    /// </summary>
    public void Disable()
    {
        TeardownSnapshot? snapshot = null;
        lock (_gate)
        {
            // If this session left nothing live (already/never enabled, possibly with a
            // previous teardown still draining), there is nothing to snapshot; otherwise
            // drain that too — disjoint objects, idempotent restore.
            bool live = _playback is not null || _windows.Count > 0 || _power is not null
                || _messageWindow is not null || _host is not null;
            if (!live)
            {
                // Nothing to snapshot, no teardown to run — skip the pointless full GC +
                // file ops below.
                IsEnabled = false;
                return;
            }
            IsEnabled = false;
            // Unsubscribe first: a dead host must never fire rebuilds into a newer
            // session (overlapping rebuild paths double-dispose native objects).
            if (_host is not null) _host.LayerLost -= OnLayerLost;
            Interlocked.Increment(ref _pauseEpoch);
            // Deterministically retire any armed settled-topology pass: its epoch guard
            // would drop it anyway, but it belongs to this session and must not run in the next.
            lock (_settleGate) _settleGen++;
            _playback?.Dispose();
            _playback = null;
            var messageHwnd = _messageWindow?.Hwnd ?? IntPtr.Zero;
            snapshot = new TeardownSnapshot(
                [.. _windows.Values], _power, _messageWindow, messageHwnd, _host,
                _appDataDir, _originalWallpaper);
            _windows.Clear();
            _power = null;
            _messageWindow = null;
            _host = null;
            _deviceLoss = null;
            // Drop actions posted by the dying session (first-frame static captures,
            // pause/resume requests, re-apply requests): the message window is gone so
            // nothing drains them anymore, and the next Enable must not inherit them — a
            // stale static fallback would overwrite the fresh session's OS wallpaper, a
            // stale pause would freeze it, a stale re-apply would flash-rebuild it.
            // Worker posts landing after this clear carry the pre-bump epoch and are
            // dropped at drain by RunOnSessionAction, so this cannot strand live work.
            int dropped = 0;
            while (_mainThreadActions.TryDequeue(out _)) dropped++;
            if (dropped > 0) Serilog.Log.Information($"Dropped {dropped} stale main-thread action(s) from the disabled session");
            Task? task = null;
            task = Task.Run(() =>
            {
                try { snapshot.Run(); }
                catch (Exception ex) { Serilog.Log.Error("Background teardown failed", ex); }
                finally { lock (_gate) { if (ReferenceEquals(_teardownTask, task)) _teardownTask = null; } }
            });
            _teardownTask = task;
        }
    }

    /// <summary>Everything a teardown needs, owned exclusively by the background task.
    /// Main-thread entry points can no longer reach these objects once Disable clears
    /// the fields, so no lock is needed inside <see cref="Run"/>.</summary>
    private sealed class TeardownSnapshot(
        List<WallpaperWindow> windows,
        IDisposable? power,
        IDisposable? messageWindow,
        IntPtr messageHwnd,
        IDisposable? layerHost,
        string appDataDir,
        string originalWallpaper)
    {
        public void Run()
        {
            // Windows first: visuals die in milliseconds with no COM teardown yet.
            foreach (var window in windows)
            {
                try { window.Dispose(); } catch (Exception ex) { Serilog.Log.Error("Window teardown failed", ex); }
            }
            // Stranded MF worker threads + finalizable COM. Blocking GC, hence here.
            try { VideoRenderer.ReclaimMediaPipeline(); } catch (Exception ex) { Serilog.Log.Error("Pipeline reclaim failed", ex); }
            try { power?.Dispose(); } catch (Exception ex) { Serilog.Log.Error("Power teardown failed", ex); }
            try
            {
                if (messageHwnd != IntPtr.Zero) WtsApi32.WTSUnRegisterSessionNotification(messageHwnd);
                messageWindow?.Dispose();
            }
            catch (Exception ex) { Serilog.Log.Error("Message window teardown failed", ex); }
            try { layerHost?.Dispose(); } catch (Exception ex) { Serilog.Log.Error("Layer teardown failed", ex); }
            RestoreOriginalWallpaper(appDataDir, originalWallpaper);
        }
    }

    //public bool IsPlayStarted { get; private set; } = true;

    public void PlayStart()
    {
        _userPaused = false;
        // Fires on the monitor's timer thread; _windows belongs to the main thread.
        // Session-scoped: a resume posted just before a Disable must not resume the next session.
        RunOnSessionAction(() =>
        {
            foreach (var window in _windows.Values)
            {
                if (window.Renderer is null) return;
                window.Renderer.Resume();
                Serilog.Log.Information($"Resumed By PlayStart");
            }
        });
        // After the resume above: re-evaluate immediately so a covering fullscreen app
        // re-pauses through the normal event path instead of playing over it.
        _playback?.Resume();
    }

    public void PlayPause()
    {
        _userPaused = true;
        // Suspend auto evaluation first: no transitions can fire while user-paused
        // (previously a closing fullscreen app auto-resumed behind the user's back).
        _playback?.Suspend();
        // Fires on the monitor's timer thread; _windows belongs to the main thread.
        // Session-scoped: a pause posted just before a Disable must not pause the next session.
        RunOnSessionAction(() =>
        {
            foreach (var window in _windows.Values)
            {
                if (window.Renderer is null) return;
                window.Renderer.Pause();
                Serilog.Log.Information($"Paused By PlayPause");
            }
        });
    }

    // ---- wallpaper application ----------------------------------------------------------

    private void ApplyFromConfig()
    {
        foreach (var monitor in MonitorTracker.Enumerate())
        {
            var path = _config.WallpaperFor(monitor.Device);
            if (path is null) continue;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Serilog.Log.Warning($"Configured wallpaper missing: {path}");
                continue;
            }
            try
            {
                ApplyToMonitor(monitor, path);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"Failed to apply wallpaper on {monitor.Device}", ex);
            }
        }
        UpdatePauseSupervision();
    }

    private void ApplyToMonitor(MonitorInfo monitor, string path)
    {
        // Reuse the existing window + composition host when we have one: no re-attach,
        // no flash of the shell wallpaper, and the clock overlay never blinks.
        if (!_windows.TryGetValue(monitor.Device, out var window))
        {
            window = new WallpaperWindow(monitor);
            window.DeviceLost += OnDeviceLost;
            _host?.Attach(window.Hwnd, monitor.Bounds);
            window.EnsureHost();
            _windows[monitor.Device] = window;
        }
        else
        {
            // The window outlives wallpaper changes: re-assert its placement against the
            // current layer geometry. A stale rect (shell moved the parent under us) renders
            // offset/clipped on that monitor until something recreates the window.
            _host?.ReassertPlacement(window.Hwnd, monitor.Bounds);
        }
        var host = window.EnsureHost();

        // A switch to a renderer that owns no surface (web) must drop the previous
        // renderer's last frame from the tree first — otherwise it stays opaque and on
        // top, and the wallpaper looks stuck on the old image/video with no errors.
        // (Video/image renderers create their own replacement surface in the constructor,
        // so their handoff stays smooth without this.)
        if (IsWebPath(path))
            host.ClearContent();

        // When the first frame is captured, set it as the OS static wallpaper so a
        // virtual-desktop switch (or Task View) paints a matching frame instead of the
        // previous wallpaper — no flash before our live layer returns.
        var staticPath = StaticPath(monitor.Device);
        var captured = monitor;
        // Session-scoped: the renderer invokes this on its own (MF/WebView) thread, which
        // can be in flight across a Disable — a stale fallback must not overwrite the next
        // session's OS wallpaper.
        Action onStatic = () => RunOnSessionAction(() =>
        {
            try { DesktopWallpaper.SetForMonitor(captured.Device, captured.Bounds, staticPath); }
            catch (Exception ex) { Serilog.Log.Error("Set static fallback failed", ex); }

            // Point every virtual desktop at this frame so switching desktops paints a
            // matching wallpaper during the slide animation instead of each desktop's own.
            if (captured.Primary)
            {
                try { VirtualDesktopWallpaper.SetAll(staticPath); }
                catch (Exception ex) { Serilog.Log.Error("Set per-desktop fallback failed", ex); }
            }
        });

        IWallpaperRenderer renderer;
        if (ImageExtensions.Contains(Path.GetExtension(path)))
        {
            renderer = new ImageRenderer(host, monitor.Bounds.Width, monitor.Bounds.Height, _config.Fit, staticPath, onStatic, _preload);
        }
        else if (IsWebPath(path))
        {
            renderer = new WebViewRenderer(host, window.Hwnd, monitor.Bounds.Width, monitor.Bounds.Height,
                _appDataDir, _config.MuteVideo, staticPath, onStatic);
        }
        else
        {
            var video = new VideoRenderer(host, monitor.Bounds.Width, monitor.Bounds.Height, _config.Fit,
                _config.MuteVideo, _config.Volume, staticPath, onStatic, _preload);
            video.PlaybackFailed += OnPlaybackFailed;
            renderer = video;
        }
        window.SetRenderer(renderer);
        try
        {
            renderer.Load(path);
        }
        catch
        {
            // Load failed after the install above (and after the new surface replaced the
            // old one in the renderer constructor): leave no zombie behind. The per-monitor
            // catch in the caller logs; the monitor falls through to the OS wallpaper.
            window.ClearRenderer();
            throw;
        }

        // Enforce the current pause state on the fresh renderer: pause events fire on
        // transitions, so a pause that predates (or lands mid-) creation is already over
        // and would never be delivered again — e.g. starting the app under a fullscreen
        // window left every monitor Playing with Fullscreen as the reason.
        try
        {
            if (_userPaused || (_playback?.GetPauseReason(monitor.Device) ?? PauseReason.None) != PauseReason.None)
            {
                renderer.Pause();
            }
        }
        catch { }

        // A switch away from GPU presentation must not pin GPU objects on the reused
        // window: when the new renderer owns no surface, drop this host's target/visual
        // (and the shared device reference with it when no other host needs it). The next
        // video/image switch re-initializes lazily; widget overlays, if any, keep the
        // device via ReleaseDeviceIfUnused's in-use guard.
        if (!renderer.IsGPURender)
            host.ReleaseDeviceIfUnused();

        _playback?.Invalidate(); // re-evaluate pause state for the fresh renderer
        Serilog.Log.Information($"Wallpaper applied on {monitor.Device} {monitor.Bounds}: {path}");
    }

    public void ApplyUserSelection(string path, string monitorDevice = "*")
    {
        bool anyApplied = false;
        foreach (var monitor in MonitorTracker.Enumerate())
        {
            if (monitorDevice != "*" && !string.Equals(monitor.Device, monitorDevice, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                ApplyToMonitor(monitor, path);
                anyApplied = true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"Failed to apply {path} on {monitor.Device}", ex);
            }
        }
        if (anyApplied)
        {
            _config.Assign(monitorDevice, path);
            //ConfigStore.Save(_config);
        }
        UpdatePauseSupervision();
    }

    /// <summary>Full rebuild for genuine loss only (layer destruction, GPU device loss):
    /// every pipeline destroy strands ~32 MB/monitor of driver video memory permanently, so
    /// transient notifications (display change, explorer restart, resume, unlock) go through
    /// <see cref="OnTopologyMightHaveChanged"/> + the settled pass instead and only reach
    /// here when the topology actually disagrees.</summary>
    /// <summary>Playback failed twice on a file. Before this, an undecodable codec produced a
    /// log line and a black desktop with nothing shown to the user — one of the two worst first
    /// impressions the product can make, and the one a stranger from a download link is most
    /// likely to hit. A tray balloon rather than a dialog: the wallpaper is decoration, and a
    /// modal box over someone's desktop is a worse bug than the one being reported.</summary>
    private void OnPlaybackFailed(string path, string detail)
    {
        // Session-scoped like the rest: a failure surfacing after a session change belongs
        // to dead windows and must not log against (or confuse) the new session.
        RunOnSessionAction(() =>
        {
            string codec = detail == "unsupported or missing decoder" ? "unsupported" : detail;
            // No codec identifier reaches here: MediaPlayerFailedEventArgs carries no FourCC, and
            // the container extension does not identify the codec — an .mp4 can hold HEVC. Feeding
            // the extension to StoreExtensionFor only ever produced the generic branch anyway, so
            // the guidance says so plainly rather than pretending to know which extension to name.
            //const string hint = "HEVC, VP9 and AV1 need their free extension from the Microsoft Store; anything else needs re-encoding to H.264.";

            Serilog.Log.Warning($"Surfacing playback failure to the user: {Path.GetFileName(path)} ({codec})");
            //_tray?.ShowBalloon($"Cannot play {Path.GetFileName(path)}", $"{char.ToUpper(codec[0])}{codec[1..]} video. {hint}");
        });
    }

    /// <summary>A pushed power/display notification. Windows already knows the panel went dark
    /// or the laptop came off AC; this is the app being told instead of polling to find out.
    /// The pause poll still runs for the foreground-window cases it cannot be told about — this
    /// only removes work, it does not add a second source of truth.</summary>
    private void OnPowerSettingChanged(IntPtr lParam)
    {
        if (!PowerNotifications.TryRead(lParam, out var setting, out byte value)) return;

        if (PowerNotifications.IsDisplayState(setting))
        {
            bool off = value == PowerNotifications.DisplayOff;
            Serilog.Log.Information($"Display state: {value switch
            {
                PowerNotifications.DisplayOff => "off",
                PowerNotifications.DisplayOn => "on",
                PowerNotifications.DisplayDimmed => "dimmed",
                _ => $"unknown ({value})",
            }}");

            if (_playback is { } playback) playback.DisplayOff = off;

        }
        else if (setting == PowerNotifications.PowerSavingStatus || setting == PowerNotifications.AcDcPowerSource)
        {
            // Battery state is only read at evaluation time; invalidate to apply it now.
            _playback?.Invalidate();
        }
    }

    /// <summary>A surface reported DXGI device removal or reset — a GPU driver update, a TDR, or
    /// an adapter change. The device belongs to the composition host, so nothing recovers in
    /// place: the whole layer is rebuilt, which is what ReapplyAll already does for a lost
    /// wallpaper layer.
    ///
    /// Until 2026-08-20 this event was raised by CompositionSurface.Present and nothing
    /// subscribed to it, so a driver update logged a warning and left a dead device presenting
    /// nothing until the user re-applied by hand.</summary>
    private void OnDeviceLost()
    {
        // Called from arbitrary surface threads: capture the generation and touch shared
        // state only inside the posted main-thread action, so a torn-down session can
        // neither NRE on a nulled guard nor rebuild into a newer session.
        var epoch = Volatile.Read(ref _pauseEpoch);
        RunOnMainThread(() =>
        {
            if (epoch != Volatile.Read(ref _pauseEpoch)) return;
            var guard = _deviceLoss;
            if (guard is not null && !guard.TryBegin())
            {
                if (guard.GaveUp)
                    Serilog.Log.Error($"GPU device lost {DeviceLossGuard.MaxConsecutiveAttempts} times in a row — " +
                              "giving up on automatic recovery. Re-apply the wallpaper from the tray once the display driver is stable.");
                return;
            }
            try
            {
                Serilog.Log.Warning("GPU device lost — rebuilding the composition tree");
                ReapplyAll();
            }
            finally
            {
                // A device lost again *during* the rebuild is reported while the guard is still
                // in flight, so nothing else will raise it a second time — the replacement surface
                // is already dead and will never present again to say so. The guard held that
                // signal; act on it. Re-entry is bounded by the same attempt budget, and
                // RunOnMainThread only ever enqueues, so this is a queued retry, not recursion.
                if (guard is not null && guard.Complete())
                {
                    Serilog.Log.Warning("GPU device lost again during recovery — rebuilding once more");
                    OnDeviceLost();
                }
            }
        });
    }

    /// <summary>Named (not a lambda) so <see cref="Disable"/> can unsubscribe: a torn-down
    /// host firing into a newer session would overlap two rebuild paths over the same
    /// native objects. The epoch drops transitions already in flight when unsubscribed.</summary>
    private void OnLayerLost()
    {
        var epoch = Volatile.Read(ref _pauseEpoch);
        RunOnMainThread(() =>
        {
            if (epoch != Volatile.Read(ref _pauseEpoch)) return;
            _deviceLoss?.Reset();
            ReapplyAll();
        });
    }

    private void ReapplyAll()
    {
        if (_reapplying) return;
        // A stale queued rebuild (display-change/taskbar/watch event posted before a
        // Disable) must not resurrect windows after teardown.
        if (!IsEnabled) return;
        Interlocked.Increment(ref _pauseEpoch);
        _reapplying = true;
        try
        {
            Serilog.Log.Information("Re-applying all wallpapers");
            foreach (var window in _windows.Values) window.Dispose();
            _windows.Clear();
            _host?.EnsureLayer();
            ApplyFromConfig();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Re-apply failed", ex);
        }
        finally
        {
            _reapplying = false;
            // Fresh pipelines from here: re-baseline native memory so the post-resume
            // recycle evaluation measures new growth, not pre-existing strands.
            _resumeBaselinePrivateBytes = CurrentPrivateBytes();
            // After the replacements are installed and rooted — never concurrently with the
            // rebuild above. The forced full GC is stop-the-world: firing it before the rebuild
            // suspended the very thread constructing the new pipeline, and overlapping storms
            // (explorer restart + display change) piled up one stall each. Single-flight now
            // collapses those into one trailing pass on a pool thread.
            VideoRenderer.ScheduleReclaimMediaPipeline();
        }
    }

    /// <summary>Arms the settled-topology trailing pass (callers: the display-change / explorer-
    /// restart / resume handlers, and <see cref="Enable"/> for the symmetric attach-too-early race at
    /// startup). Re-armed by every notification, so a storm collapses into one trailing pass;
    /// each firing runs only for the latest generation. Thread-safe: handlers run on the main
    /// thread, the firing lands on the pool.</summary>
    private void ScheduleSettledReapply() => ScheduleSettledReapply(SettleDelay);

    /// <summary>Same as <see cref="ScheduleSettledReapply()"/> with an explicit delay, for
    /// sources that need longer convergence (post-resume driver/shell handshake).</summary>
    private void ScheduleSettledReapply(TimeSpan delay)
    {
        System.Threading.Timer? old;
        int gen;
        lock (_settleGate)
        {
            _settleGen++;
            gen = _settleGen;
            old = _settleTimer;
            _settleTimer = new System.Threading.Timer(_ => SettledReapplyFired(gen), null, delay, Timeout.InfiniteTimeSpan);
        }
        try { old?.Dispose(); } catch { }
    }

    private void SettledReapplyFired(int gen)
    {
        // Pool thread. Disabled/torn-down sessions have nothing live; the epoch guard in the
        // posted action covers a Disable/Enable that lands after this check.
        if (!IsEnabled) return;
        lock (_settleGate)
        {
            if (gen != _settleGen) return; // superseded by a newer notification, or torn down
        }
        RunOnSessionAction(() =>
        {
            lock (_settleGate)
            {
                if (gen != _settleGen) return;
            }
            // Converged already (the common storm case) — verifying costs one enumeration
            // and a few rect compares, while a blind rebuild would flash every monitor.
            if (!TopologyMatchesWindows())
            {
                Serilog.Log.Information("Settled topology disagrees with live windows — re-applying");
                ReapplyAll();
                return;
            }
            // Converged, but a resume may have left the video pipelines in the degraded
            // stranding state (GPU surfaces orphaned per minute with no topology change).
            // One bounded recycle then, and only when native memory is actually elevated.
            EvaluatePostResumeRecycle();
        });
    }

    /// <summary>Cheap storm response for display-change / explorer-restart notifications: re-probe
    /// the layer, re-assert surviving windows against it, and let the settled trailing pass do
    /// the only rebuild — and only when the topology actually disagrees.
    ///
    /// Previously every such notification ran an immediate full <see cref="ReapplyAll"/>,
    /// destroying both video pipelines; a post-hibernate storm (display-change + taskbar-created
    /// + unlock within seconds) therefore paid several ~32 MB/monitor permanent driver-memory
    /// strands per resume (measured 2026-09-20: 1–3 GB after hibernation).</summary>
    private void OnTopologyMightHaveChanged()
    {
        if (!IsEnabled) return;
        try { _host?.EnsureLayer(); }
        catch (Exception ex) { Serilog.Log.Warning($"Layer re-probe failed: {ex.Message}"); }
        ReassertAllPlacements();
        ScheduleSettledReapply();
    }

    /// <summary>Re-asserts every live window against the current layer geometry without
    /// rebuilding anything. Windows that died with the old shell are left for the settled
    /// pass (its <see cref="TopologyMatchesWindows"/> check rebuilds on dead handles).</summary>
    private void ReassertAllPlacements()
    {
        var host = _host;
        if (host is null) return;
        foreach (var entry in _windows.ToArray())
        {
            try
            {
                var window = entry.Value;
                if (window.Hwnd == IntPtr.Zero || !User32.IsWindow(window.Hwnd)) continue;
                host.ReassertPlacement(window.Hwnd, window.Monitor.Bounds);
            }
            catch (Exception ex) { Serilog.Log.Warning($"Placement re-assert failed on {entry.Key}: {ex.Message}"); }
        }
    }

    /// <summary>Power-resume entry point (PBT_APMRESUMEAUTOMATIC): records the resume for the
    /// recycle evaluation, re-validates the layer, and arms a long-settled trailing pass.
    /// Playback keeps running through the storm — pausing/resuming or rebuilding pipelines
    /// here is exactly what orphaned driver surfaces at ~6 MB/s in measurement.</summary>
    private void OnResumedFromSleep()
    {
        _lastResumeUtc = DateTime.UtcNow;
        _resumeRecycled = false;
        _resumeBaselinePrivateBytes = CurrentPrivateBytes();
        try { _host?.ValidateLayer(); }
        catch (Exception ex) { Serilog.Log.Warning($"Resume layer validation failed: {ex.Message}"); }
        ScheduleSettledReapply(ResumeSettleDelay);
        Serilog.Log.Information(
            "Resumed from sleep — layer validated, settled pass armed (private bytes baseline {BaselineMb} MB)",
            _resumeBaselinePrivateBytes / 1024 / 1024);
    }

    /// <summary>Session-unlock entry point: converge through the settled pass, without
    /// shortening a pending post-resume settle (the driver/shell handshake needs the long
    /// delay; an early verdict would see transient disagreement and rebuild for nothing).</summary>
    private void OnSessionUnlocked()
    {
        if (DateTime.UtcNow - _lastResumeUtc > TimeSpan.FromMinutes(1))
            ScheduleSettledReapply();
    }

    /// <summary>Single bounded post-resume recycle: when a recent resume was followed by
    /// genuinely elevated native memory, the video pipelines are already in the degraded
    /// stranding state — one full Disable/Enable drops them (and their stuck driver pools)
    /// instead of stranding hundreds of MB more. Gated to once per resume plus a cooldown,
    /// and skipped entirely when memory is healthy, so a clean resume costs nothing.</summary>
    private void EvaluatePostResumeRecycle()
    {
        if (!IsEnabled || _resumeRecycled) return;
        if (DateTime.UtcNow - _lastResumeUtc > ResumeRecycleWindow) return;
        if (DateTime.UtcNow - _lastRecycleUtc < RecycleCooldown) return;
        long current = CurrentPrivateBytes();
        if (current <= 0) return;
        long growth = current - _resumeBaselinePrivateBytes;
        if (current < RecyclePrivateBytesThreshold && growth < RecycleGrowthBytesThreshold) return;
        _resumeRecycled = true;
        _lastRecycleUtc = DateTime.UtcNow;
        Serilog.Log.Warning(
            "Post-resume native memory elevated ({CurrentMb} MB, +{GrowthMb} MB since resume) — " +
            "recycling video pipelines once to drop degraded decoder pools (~{PoolMb} MB stranded per pool)",
            current / 1024 / 1024, growth / 1024 / 1024, BytesPerStrandedPool / 1024 / 1024);
        try
        {
            Disable();
            EnsureEnabled();
        }
        catch (Exception ex) { Serilog.Log.Error("Post-resume recycle failed", ex); }
    }

    private static long CurrentPrivateBytes()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64;
        }
        catch { return -1; }
    }

    /// <summary>True when every OS monitor has exactly one live window and each sits exactly
    /// where the current layer geometry puts it. Runs on the main thread. Anything else —
    /// a monitor that arrived after the last attach, or a window attached against a
    /// stale-sized parent (fully clipped → black on the new display) — needs a rebuild,
    /// which is the proven-safe path (a manual disable/enable does exactly this).</summary>
    private bool TopologyMatchesWindows()
    {
        List<MonitorInfo> monitors;
        try
        {
            monitors = MonitorTracker.Enumerate();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Settled-topology check could not enumerate monitors: {ex.Message}");
            return false;
        }
        var host = _host;
        if (host is null || monitors.Count != _windows.Count) return false;
        RECT parent;
        try
        {
            parent = host.ParentScreenRect();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Settled-topology check could not read the layer parent: {ex.Message}");
            return false;
        }
        foreach (var monitor in monitors)
        {
            if (!_windows.TryGetValue(monitor.Device, out var window)) return false;
            var hwnd = window.Hwnd;
            if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return false;
            // Same mapping Attach used — but read-only: incremental repositioning proved
            // unsafe on multi-monitor, so drift here means rebuild, never nudge.
            var expected = MonitorTracker.ScreenToParentClient(monitor.Bounds, parent);
            int expLeft = parent.Left + expected.Left;
            int expTop = parent.Top + expected.Top;
            User32.GetWindowRect(hwnd, out var current);
            if (current.Left != expLeft || current.Top != expTop ||
                current.Width != expected.Width || current.Height != expected.Height)
                return false;
        }
        return true;
    }

    // ---- pause/resume ---------------------------------------------------------------------

    private void OnPauseStateChanged(string monitorDevice, PauseReason reason)
    {
        // Fires on the monitor's timer thread; _windows belongs to the main thread.
        RunOnMainThread(() =>
        {
            // PauseStateChanged arrives on the supervisor's hook thread; windows live here.
            if (!_windows.TryGetValue(monitorDevice, out var window) || window.Renderer is null) return;
            if (reason == PauseReason.None)
            {
                if (window.Renderer.IsPaused())
                {
                    window.Renderer.Resume();
                    Serilog.Log.Information("Resumed monitor: {monitorDevice}", monitorDevice);
                }
            }
            else
            {
                if (window.Renderer.IsPlaying())
                {
                    window.Renderer.Pause();
                    Serilog.Log.Information("Paused monitor: {monitorDevice}, Reason: {reason}", monitorDevice, reason);
                }
            }
        });
    }



    // ---- main-thread marshaling -------------------------------------------------------------

    public void RunOnMainThread(Action action)
    {
        _mainThreadActions.Enqueue(action);
        if (_messageWindow is not null)
            User32.PostMessageW(_messageWindow.Hwnd, MessageWindow.RunActionsMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Session-scoped <see cref="RunOnMainThread"/>: captures the current session epoch
    /// at post time and drops the action if a Disable/Enable/ReapplyAll intervened before it
    /// drains. Use this for everything that touches session-owned objects (windows, renderers,
    /// the OS wallpaper) — worker threads (first-frame static capture, pause transitions) and
    /// UI requests (play/pause, re-apply) can both be in flight across a session boundary, and
    /// without the guard their actions land on the next session's windows. Raw
    /// <see cref="RunOnMainThread"/> stays for the call sites that already capture and check the
    /// epoch themselves (<see cref="OnDeviceLost"/>, <see cref="OnLayerLost"/>, pause fan-out).</summary>
    public void RunOnSessionAction(Action action)
    {
        var epoch = Volatile.Read(ref _pauseEpoch);
        RunOnMainThread(() =>
        {
            if (epoch != Volatile.Read(ref _pauseEpoch)) return;
            action();
        });
    }

    private void DrainMainThreadActions()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Serilog.Log.Error("Posted action failed", ex); }
        }
    }

    private void SaveOriginalWallpaper()
    {
        try
        {
            Directory.CreateDirectory(_appDataDir);
            if (File.Exists(OriginalWallpaperFile))
            {
                // A prior run left this behind (crash / kill) — it holds the TRUE original.
                _originalWallpaper = File.ReadAllText(OriginalWallpaperFile).Trim();
            }
            else
            {
                _originalWallpaper = DesktopWallpaper.GetCurrent();
                File.WriteAllText(OriginalWallpaperFile, _originalWallpaper);
                Serilog.Log.Information($"Saved original wallpaper: {(_originalWallpaper.Length == 0 ? "(none)" : _originalWallpaper)}");
            }

            // Per-desktop wallpapers (Windows 11) — save once so we can restore them.
            if (!File.Exists(OriginalDesktopWallpapersFile))
            {
                var perDesktop = VirtualDesktopWallpaper.ReadAll();
                File.WriteAllLines(OriginalDesktopWallpapersFile,
                    perDesktop.Select(kv => $"{kv.Key}\t{kv.Value}"));
                Serilog.Log.Information($"Saved {perDesktop.Count} per-desktop wallpaper(s)");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Could not save original wallpaper: {ex.Message}");
        }
    }

    /// <summary>Static + best-effort so background teardown can run it without touching
    /// engine state. Behavior unchanged: per-desktop restore, file cleanup, then the
    /// (slow, shell-broadcasting) SetWallpaper restore.</summary>
    private static void RestoreOriginalWallpaper(string appDataDir, string originalWallpaper)
    {
        string tsv = Path.Combine(appDataDir, "original-vd-wallpapers.tsv");
        string txt = Path.Combine(appDataDir, "original-wallpaper.txt");
        try
        {
            if (File.Exists(tsv))
            {
                var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(tsv))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) saved[line[..tab]] = line[(tab + 1)..];
                }
                VirtualDesktopWallpaper.Restore(saved);
                File.Delete(tsv);
            }

            if (!string.IsNullOrEmpty(originalWallpaper))
                DesktopWallpaper.RestoreCurrent(originalWallpaper);
            if (File.Exists(txt))
                File.Delete(txt);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Could not restore original wallpaper: {ex.Message}");
        }
    }

    // ---- settings panel hooks ------------------------------------------------------------

    public EngineConfig Config => _config;

    //public void SaveConfig() => ConfigStore.Save(_config);

    /// <summary>Fit mode (or similar) changed: persist and swap renderers in place —
    /// windows and hosts are reused, so there is no flicker.</summary>
    public void RefreshWallpapers()
    {
        if (!IsEnabled)
        {
            this.Enable();
        }
        else
        {

            //ConfigStore.Save(_config);
            foreach (var monitor in MonitorTracker.Enumerate())
            {
                var path = _config.WallpaperFor(monitor.Device);
                if (path is null || (!File.Exists(path) && !Directory.Exists(path))) continue;
                try
                {
                    ApplyToMonitor(monitor, path);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error($"Refresh failed on {monitor.Device}", ex);
                }
            }
            UpdatePauseSupervision();
        }
    }

    public void ApplyAudioSettings()
    {
        //ConfigStore.Save(_config);
        foreach (var window in _windows.Values)
            if (window.Renderer is VideoRenderer video)
            {
                video.IsMuted = _config.MuteVideo;
                video.Volume = _config.Volume;
            }
            else if (window.Renderer is WebViewRenderer web)
            {
                web.IsMuted = _config.MuteVideo;
            }
    }

    // ---- diagnostics --------------------------------------------------------------------------

    /// <summary>Per-monitor live-wallpaper snapshot for diagnostics UI (e.g. the performance
    /// monitor). Best-effort and thread-safe: the UI polls this on its own timer while the
    /// engine mutates windows on its main thread.</summary>
    public sealed record MonitorWallpaperState(
        string Device, string Bounds, string File, string Renderer,
        bool IsLoaded, bool IsPaused, bool IsPlaying, string PauseReason,
        uint WebProcessId, long PreloadedBytes);

    public IReadOnlyList<MonitorWallpaperState> GetMonitorStates()
    {
        var list = new List<MonitorWallpaperState>();
        try
        {
            foreach (var entry in _windows.ToArray())
            {
                try
                {
                    var window = entry.Value;
                    var renderer = window.Renderer;
                    string file;
                    try { file = Path.GetFileName(_config.WallpaperFor(entry.Key) ?? ""); }
                    catch { file = ""; }
                    string kind = renderer is VideoRenderer ? "Video"
                        : renderer is ImageRenderer ? "Image"
                        : renderer is WebViewRenderer ? "Web"
                        : renderer is null ? "-" : renderer.GetType().Name;
                    bool loaded = false, paused = false, playing = false;
                    try
                    {
                        if (renderer is not null)
                        {
                            loaded = renderer.IsLoaded();
                            paused = renderer.IsPaused();
                            playing = renderer.IsPlaying();
                        }
                    }
                    catch { }
                    string reason = PauseReason.None.ToString();
                    try { reason = _playback?.GetPauseReason(entry.Key).ToString() ?? reason; }
                    catch { }
                    string bounds;
                    try { bounds = window.Monitor.Bounds.ToString(); }
                    catch { bounds = ""; }
                    uint webPid = 0;
                    try { if (renderer is WebViewRenderer web) webPid = web.BrowserProcessId ?? 0; }
                    catch { }
                    long preloaded = 0;
                    try
                    {
                        var full = _config.WallpaperFor(entry.Key);
                        if (!string.IsNullOrEmpty(full)) preloaded = _preload.PreloadedBytesFor(full);
                    }
                    catch { }
                    list.Add(new MonitorWallpaperState(entry.Key, bounds, file, kind, loaded, paused, playing, reason, webPid, preloaded));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public string Diagnostics()
    {
        var sb = new StringBuilder();
        var layer = _host?.Layer ?? new DesktopLayerInfo(DesktopTopology.ClassicWorkerW, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        sb.AppendLine($"AppVersion {typeof(Engine).Assembly.GetName().Version}");
        sb.AppendLine($"Topology: {layer.Topology}");
        sb.AppendLine($"Progman: 0x{layer.Progman:X}  WorkerW: 0x{layer.WorkerW:X}  DefView: 0x{layer.DefView:X}");
        foreach (var monitor in MonitorTracker.Enumerate())
            sb.AppendLine($"Monitor {monitor.Device}: {monitor.Bounds}{(monitor.Primary ? " (primary)" : "")}");
        return sb.ToString();
    }

    // ---- message window -----------------------------------------------------------------------

    private sealed class MessageWindow : Win32Window
    {
        public const uint RunActionsMessage = WM_APP + 2;
        private readonly Engine _engine;

        public MessageWindow(Engine engine)
        {
            _engine = engine;
            CreateWindow("DesktopLiveWallPaperEngineMessage", 0, 0, 0, 0, 0, 0, IntPtr.Zero);
        }

        protected override IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == RunActionsMessage)
            {
                _engine.DrainMainThreadActions();
                return IntPtr.Zero;
            }
            if (msg == _engine._taskbarCreatedMessage)
            {
                Serilog.Log.Warning("Explorer restarted (TaskbarCreated) — re-attaching");
                // Cheap re-glue now; the settled pass rebuilds only on real disagreement.
                // An immediate full re-apply here used to destroy both video pipelines per
                // notification (~32 MB/monitor of permanently stranded driver memory each).
                _engine.RunOnSessionAction(_engine.OnTopologyMightHaveChanged);
                _engine.ScheduleSettledReapply();
                return IntPtr.Zero;
            }
            switch (msg)
            {
                case WM_CLOSE: // polite shutdown (taskkill without /F, system shutdown)
                    User32.PostQuitMessage(0);
                    return IntPtr.Zero;
                case WM_DISPLAYCHANGE:
                    Serilog.Log.Information("Display change — re-attaching, settled pass will converge");
                    // The notification can precede the settled topology (new monitor not yet
                    // enumerable, layer parent not yet resized): re-glue cheaply now, rebuild
                    // (if needed at all) in the trailing pass.
                    _engine.RunOnSessionAction(_engine.OnTopologyMightHaveChanged);
                    _engine.ScheduleSettledReapply();
                    return IntPtr.Zero;
                case WM_WTSSESSION_CHANGE:
                    if (_engine._playback is { } playback)
                    {
                        if ((int)wParam == WTS_SESSION_LOCK) playback.SessionLocked = true;
                        else if ((int)wParam == WTS_SESSION_UNLOCK)
                        {
                            playback.SessionLocked = false;
                            _engine._host?.ValidateLayer();
                            // Unlock after hibernate lands in the middle of the resume storm;
                            // converge (and recycle if degraded) through the settled pass.
                            _engine.OnSessionUnlocked();
                        }
                    }
                    return IntPtr.Zero;
                case WM_POWERBROADCAST when (int)wParam == PBT_APMRESUMEAUTOMATIC:
                    Serilog.Log.Information("Resumed from sleep — refreshing and validating layer");
                    _engine.OnResumedFromSleep();
                    return IntPtr.Zero;
                case WM_POWERBROADCAST when (int)wParam == PBT_POWERSETTINGCHANGE:
                    _engine.OnPowerSettingChanged(lParam);
                    return IntPtr.Zero;
            }
            return base.HandleMessage(msg, wParam, lParam);
        }
    }



    /// <summary>Synchronous-looking shutdown: launches the background teardown, then
    /// waits for it bounded so the original wallpaper is actually restored before the
    /// process exits. The OS reclaims anything still in flight past the cap.</summary>
    public void Dispose()
    {
        Disable();
        // Retire the settled-topology timer: pending firings observe the bumped generation
        // (plus !IsEnabled) and return without posting.
        System.Threading.Timer? settle;
        lock (_settleGate)
        {
            settle = _settleTimer;
            _settleTimer = null;
            _settleGen++;
        }
        try { settle?.Dispose(); } catch { }
        Task? teardown;
        lock (_gate) teardown = _teardownTask;
        if (teardown is not null && !teardown.IsCompleted)
        {
            try { teardown.Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { Serilog.Log.Warning($"Teardown wait failed: {ex.Message}"); }
        }
    }
}
