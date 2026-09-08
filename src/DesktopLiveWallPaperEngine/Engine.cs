using DesktopLiveWallPaperEngine.Common;
using DesktopLiveWallPaperEngine.Config;
using DesktopLiveWallPaperEngine.Desktop;
using DesktopLiveWallPaperEngine.Interop;
using DesktopLiveWallPaperEngine.Playback;
using DesktopLiveWallPaperEngine.Rendering;
using System.Collections.Concurrent;
using System.Text;
using static DesktopLiveWallPaperEngine.Interop.Win32Constants;

namespace DesktopLiveWallPaperEngine;

/// <summary>Wires everything together: desktop layer, per-monitor wallpaper windows,
/// clock widget, pause monitor, tray UI, config persistence.</summary>
public sealed class Engine : IDisposable
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private readonly EngineConfig _config;
    private readonly string _appDataDir;
    private DesktopLayerHost? _host;
    private readonly Dictionary<string, WallpaperWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private DeviceLossGuard? _deviceLoss;
    private readonly uint _taskbarCreatedMessage = User32.RegisterWindowMessageW("TaskbarCreated");

    private MessageWindow? _messageWindow;
    private PowerNotifications? _power;

    private PlaybackMonitor? _playback;
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

    private string StaticDir => Path.Combine(_appDataDir, "static");
    private string OriginalWallpaperFile => Path.Combine(_appDataDir, "original-wallpaper.txt");
    private string OriginalDesktopWallpapersFile => Path.Combine(_appDataDir, "original-vd-wallpapers.tsv");
    private string StaticPath(string device) =>
        Path.Combine(StaticDir, new string(device.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".png");

    public Engine(EngineConfig config, string appDataDir)
    {
        _config = config;
        _appDataDir = appDataDir;
    }

    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Start/Enable the engine. Waits (bounded) for a previous background teardown:
    /// it owns the original-wallpaper files this method re-reads, and its windows
    /// must be gone before new ones attach.
    /// </summary>
    public void Enable()
    {
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

        _playback = new PlaybackMonitor(() => _config.Pause);
        _playback.PauseStateChanged += (device, reason) =>
        {
            var epoch = Volatile.Read(ref _pauseEpoch);
            RunOnMainThread(() =>
            {
                if (epoch != Volatile.Read(ref _pauseEpoch)) return;
                OnPauseStateChanged(device, reason);
            });
        };
        ApplyFromConfig();
        IsEnabled = true;
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
            // A previous teardown is still draining (only possible after an Enable timed
            // out waiting for it). If this session left nothing live, there is nothing
            // to snapshot; otherwise drain it too — disjoint objects, idempotent restore.
            bool draining = _teardownTask is { IsCompleted: false };
            bool live = _playback is not null || _windows.Count > 0 || _power is not null
                || _messageWindow is not null || _host is not null;
            if (draining && !live)
            {
                IsEnabled = false;
                return;
            }
            IsEnabled = false;
            // Unsubscribe first: a dead host must never fire rebuilds into a newer
            // session (overlapping rebuild paths double-dispose native objects).
            if (_host is not null) _host.LayerLost -= OnLayerLost;
            Interlocked.Increment(ref _pauseEpoch);
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
        // Fires on the monitor's timer thread; _windows belongs to the main thread.
        RunOnMainThread(() =>
        {
            foreach (var window in _windows.Values)
            {
                if (window.Renderer is null) return;
                window.Renderer.Resume();
                Serilog.Log.Information($"Resumed By PlayStart");
            }
        });
        // After the resume above: re-poll immediately so a covering fullscreen app
        // re-pauses through the normal event path instead of playing over it.
        _playback?.Resume();
    }

    public void PlayPause()
    {
        // Suspend auto evaluation first: no transitions can fire while user-paused
        // (previously a closing fullscreen app auto-resumed behind the user's back).
        _playback?.Suspend();
        // Fires on the monitor's timer thread; _windows belongs to the main thread.
        RunOnMainThread(() =>
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
            if (!File.Exists(path))
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
        var host = window.EnsureHost();

        // When the first frame is captured, set it as the OS static wallpaper so a
        // virtual-desktop switch (or Task View) paints a matching frame instead of the
        // previous wallpaper — no flash before our live layer returns.
        var staticPath = StaticPath(monitor.Device);
        var captured = monitor;
        Action onStatic = () => RunOnMainThread(() =>
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
            renderer = new ImageRenderer(host, monitor.Bounds.Width, monitor.Bounds.Height, _config.Fit, staticPath, onStatic);
        }
        else
        {
            var video = new VideoRenderer(host, monitor.Bounds.Width, monitor.Bounds.Height, _config.Fit,
                _config.MuteVideo, _config.Volume, staticPath, onStatic);
            video.PlaybackFailed += OnPlaybackFailed;
            renderer = video;
        }
        window.SetRenderer(renderer);
        renderer.Load(path);

        _playback?.Invalidate(); // re-evaluate pause state for the fresh renderer
        Serilog.Log.Information($"Wallpaper applied on {monitor.Device}: {path}");
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
    }

    /// <summary>Full rebuild: display change, explorer restart, or layer destruction.</summary>
    /// <summary>Playback failed twice on a file. Before this, an undecodable codec produced a
    /// log line and a black desktop with nothing shown to the user — one of the two worst first
    /// impressions the product can make, and the one a stranger from a download link is most
    /// likely to hit. A tray balloon rather than a dialog: the wallpaper is decoration, and a
    /// modal box over someone's desktop is a worse bug than the one being reported.</summary>
    private void OnPlaybackFailed(string path, string detail)
    {
        RunOnMainThread(() =>
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
            // The pause poll reads battery saver directly; nudging it just makes the transition
            // land immediately instead of up to 500 ms later.
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
            // Deferred: the full blocking GC is the stall here, and reclamation is
            // eventual by design — the rebuild below must not wait for it.
            Task.Run(VideoRenderer.ReclaimMediaPipeline);
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
        }
    }

    // ---- pause/resume ---------------------------------------------------------------------

    private void OnPauseStateChanged(string monitorDevice, PauseReason reason)
    {
        // Fires on the monitor's timer thread; _windows belongs to the main thread.
        RunOnMainThread(() =>
        {
            if (!_windows.TryGetValue(monitorDevice, out var window) || window.Renderer is null) return;
            if (reason == PauseReason.None)
            {
                window.Renderer.Resume();
                Serilog.Log.Information($"Resumed {monitorDevice}");
            }
            else
            {
                window.Renderer.Pause();
                Serilog.Log.Information($"Paused {monitorDevice}: {reason}");
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
                if (path is null || !File.Exists(path)) continue;
                try
                {
                    ApplyToMonitor(monitor, path);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error($"Refresh failed on {monitor.Device}", ex);
                }
            }
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
    }

    // ---- diagnostics --------------------------------------------------------------------------

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
                _engine.RunOnMainThread(_engine.ReapplyAll);
                return IntPtr.Zero;
            }
            switch (msg)
            {
                case WM_CLOSE: // polite shutdown (taskkill without /F, system shutdown)
                    User32.PostQuitMessage(0);
                    return IntPtr.Zero;
                case WM_DISPLAYCHANGE:
                    Serilog.Log.Information("Display change — re-applying");
                    _engine.RunOnMainThread(_engine.ReapplyAll);
                    return IntPtr.Zero;
                case WM_WTSSESSION_CHANGE:
                    if (_engine._playback is { } playback)
                    {
                        if ((int)wParam == WTS_SESSION_LOCK) playback.SessionLocked = true;
                        else if ((int)wParam == WTS_SESSION_UNLOCK)
                        {
                            playback.SessionLocked = false;
                            _engine._host?.ValidateLayer();
                        }
                    }
                    return IntPtr.Zero;
                case WM_POWERBROADCAST when (int)wParam == PBT_APMRESUMEAUTOMATIC:
                    Serilog.Log.Information("Resumed from sleep — refreshing and validating layer");
                    _engine._host?.ValidateLayer();
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
        Task? teardown;
        lock (_gate) teardown = _teardownTask;
        if (teardown is not null && !teardown.IsCompleted)
        {
            try { teardown.Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { Serilog.Log.Warning($"Teardown wait failed: {ex.Message}"); }
        }
    }
}
