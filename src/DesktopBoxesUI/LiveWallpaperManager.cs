using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Services;
using DesktopLiveWallPaperEngine;
using DesktopLiveWallPaperEngine.Config;
using Microsoft.Win32;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;

namespace DesktopBoxesUI;

/// <summary>
/// Standalone live-wallpaper (.mp4) feature, managed separately from <see cref="DesktopManager"/>.
/// Video hosting lives in <see cref="DesktopWallPaperEngine"/> (one borderless WinForms
/// window per monitor hosting the video, parented behind the desktop icons — no hooks,
/// no watchdogs; settings, layout and pause only). The tray Live Wallpaper submenu
/// owns these windows exclusively.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class LiveWallpaperManager
{
    private readonly ISettingsService _settings;
    //private DispatcherTimer? _layoutDebounce;

    private Engine? _engine;

    //private bool _Engine_IsEnabled;

    public LiveWallpaperManager(ISettingsService settings)
    {
        _settings = settings;
    }

    public bool IsEnabled => _settings.UserSettings.LiveWallpaperEnabled;

    public bool HasWallpaper
    {
        get
        {
            var path = _settings.UserSettings.LiveWallpaperPath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    public bool IsPlaying => _settings.UserSettings.LiveWallpaperPlaying;

    /// <summary>Engine alive (created and not torn down). The settings flags alone cannot tell
    /// whether windows/pause state actually exist — this can.</summary>
    public bool EngineIsLive => _engine?.IsEnabled == true;

    /// <summary>Per-monitor engine snapshot for diagnostics UI. Empty when the engine was never
    /// created or is disabled; never throws (the UI polls this on its own timer).</summary>
    public IReadOnlyList<DesktopLiveWallPaperEngine.Engine.MonitorWallpaperState> GetMonitorStates()
    {
        try { return _engine?.GetMonitorStates() ?? []; }
        catch { return []; }
    }

    private bool ShouldShow => IsEnabled && HasWallpaper;
    Engine? GetEngine()
    {
        if (ShouldShow && _engine is null)
        {
            WallpaperAssignment wallpaperAssignment = new WallpaperAssignment() { Monitor = "*", Path = _settings.UserSettings.LiveWallpaperPath! };
            var _config = new EngineConfig();
            //_config.Wallpapers.Add(wallpaperAssignment);
            _config.Assign("*", _settings.UserSettings.LiveWallpaperPath!);
            _config.PreloadMaxBytes = PreloadCapBytes(_settings.UserSettings.LiveWallpaperPreloadMaxMB);
            _engine = new Engine(_config, SettingsService.AppDataDir);
        }
        return _engine;
    }

    void DoWithEngine(Action<Engine> action)
    {
        var eng = GetEngine();
        if (eng is not null) action(eng);
    }

    private static long PreloadCapBytes(int mb) => Math.Max(0, Math.Min(1024, mb)) * 1024L * 1024L;

    /// <summary>Applies the preload cap (settings change): live-updates a running engine,
    /// otherwise stored for the next <see cref="GetEngine"/> build.</summary>
    public void ApplyPreloadCap()
    {
        try { _engine?.SetPreloadCap(PreloadCapBytes(_settings.UserSettings.LiveWallpaperPreloadMaxMB)); } catch { }
    }

    /// <summary>Total preloaded bytes currently pinned (shared across monitors).</summary>
    public long GetPreloadTotalBytes()
    {
        try { return _engine?.PreloadTotalBytes() ?? 0; } catch { return 0; }
    }

    /// <summary>Engine rebuild forensics for diagnostics UI (never throws).</summary>
    public string GetReapplyInfo()
    {
        try { return _engine?.ReapplyInfo ?? "rebuilds=?"; } catch { return "rebuilds=?"; }
    }

    public void Initialize()
    {
        //Not needed anymore the engine handles it automatically 

        /*SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionSwitch += OnSessionSwitch;*/
        if (ShouldShow)
        {
            DoWithEngine(eng =>
            {
                eng.Enable();
                if (!_settings.UserSettings.LiveWallpaperPlaying)
                {
                    eng.PlayPause();
                }
            });
        }
    }

    public void SetWallpaper(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
            Serilog.Log.Information("Live wallpaper changing to {Path}", path);
        _settings.UserSettings.LiveWallpaperPath = path;
        _settings.UserSettings.LiveWallpaperEnabled = true;
        _settings.UserSettings.LiveWallpaperPlaying = true;
        _settings.Save();
        //
        if (ShouldShow)
        {
            DoWithEngine(eng =>
            {
                eng.Config.Assign("*", path);
                eng.RefreshWallpapers();
            });
        }
    }

    public void SetPlaying(bool playing)
    {
        if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
            Serilog.Log.Information("Live wallpaper playing set to {Playing}", playing);
        _settings.UserSettings.LiveWallpaperPlaying = playing;
        _settings.Save();
        if (ShouldShow)
        {
            DoWithEngine(eng =>
            {
                eng.EnsureEnabled();
                if (playing)
                {
                    eng.PlayStart();
                }
                else
                {
                    eng.PlayPause();
                }
            });
        }
    }

    /// <summary>Transient playback pause for the global loading dialog: unlike
    /// <see cref="SetPlaying"/> it never touches persisted settings, and it never builds the
    /// engine — pre-init there is simply nothing playing yet.</summary>
    public void SetTransientPaused(bool paused)
    {
        try
        {
            var eng = _engine;
            if (eng is null || !ShouldShow) return;
            if (paused) eng.PlayPause();
            else eng.PlayStart();
        }
        catch { }
    }

    public void SetEnabled(bool enabled)
    {
        if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
            Serilog.Log.Information("Live wallpaper enabled set to {Enabled}", enabled);
        _settings.UserSettings.LiveWallpaperEnabled = enabled;
        _settings.Save();
        if (enabled)
        {
            if (ShouldShow)
            {
                DoWithEngine(eng =>
                {
                    eng.EnsureEnabled();
                    if (!_settings.UserSettings.LiveWallpaperPlaying)
                    {
                        eng.PlayPause();
                    }
                });
            }
        }
        else
        {
            _engine?.Disable();
        }
    }

    public void Remove()
    {
        if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
            Serilog.Log.Information("Live wallpaper removed");
        _settings.UserSettings.LiveWallpaperPath = null;
        _settings.Save();
        //
        if (_engine is not null)
        {
            _engine.Config.Assign("*", string.Empty);
            _engine.Disable();
        }
    }

    public void RecoverAfterShellRestart()
    {
        //Not needed anymore the engine handles it automatically 
        //_engine.Clear();
        //RebuildLayout();
        //ScheduleLayout();
    }

    public void Shutdown()
    {
        //Not needed anymore the engine handles it automatically 
        /* try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; } catch { }
         try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }*/
        //try { _layoutDebounce?.Stop(); } catch { }
        _engine?.Dispose();
    }

    #region Layout

    //private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleLayout();
    //Not needed anymore the engine handles it automatically 

    /* private void ScheduleLayout()
     {
         var app = Application.Current;
         if (app is null) return;
         if (!app.Dispatcher.CheckAccess())
         {
             app.Dispatcher.InvokeAsync(ScheduleLayout);
             return;
         }
         _layoutDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
         _layoutDebounce.Tick -= LayoutDebounceTick;
         _layoutDebounce.Tick += LayoutDebounceTick;
         _layoutDebounce.Stop();
         _layoutDebounce.Start();
     }

     private void LayoutDebounceTick(object? sender, EventArgs e)
     {
         if (_layoutDebounce != null)
         {
             _layoutDebounce.Stop();
             _layoutDebounce.Tick -= LayoutDebounceTick;
         }
         //RebuildLayout();
         if (ShouldShow)
         {
             DoWithEngine(eng =>
             {
                 eng.EnsureEnabled();
                 eng.RefreshWallpapers();
                 if (!_settings.UserSettings.LiveWallpaperPlaying)
                 {
                     eng.PlayPause();
                 }
             });
         }
     }*/
    /*
        private void RebuildLayout()
        {
            if (!ShouldShow)
            {
                _engine.Clear();
                return;
            }

            IReadOnlyList<Core.Models.MonitorInfo> monitors;
            try { monitors = _monitors.GetAllMonitors(); }
            catch { return; }

            var bounds = monitors
                .Where(m => !string.IsNullOrWhiteSpace(m.DeviceName))
                .Select(m => new MonitorBounds(
                    m.DeviceName,
                    (int)m.Bounds.X, (int)m.Bounds.Y,
                    Math.Max(1, (int)m.Bounds.Width), Math.Max(1, (int)m.Bounds.Height),
                    m.IsPrimary))
                .ToList();

            _engine.RebuildLayout(bounds, _settings.UserSettings.LiveWallpaperPath, IsPlaying);
        }*/

    #endregion

    #region Auto-pause (event-driven)

    /* private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
     {
         //Not needed anymore the engine handles it automatically 
         //if (e.Reason == SessionSwitchReason.SessionLock)
         //    _engine.SetAutoPaused(true);
         //else if (e.Reason == SessionSwitchReason.SessionUnlock)
         //    _engine.SetAutoPaused(false);
     }*/

    #endregion
}
