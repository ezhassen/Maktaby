using Maktaby.Controls;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Services;
using Maktaby.Helpers;
using Maktaby.Settings;
using Maktaby.Shared.Interfaces;
using Maktaby.Shared.Services;
using Maktaby.Shell.Services;
using Maktaby.ViewModels;
using Maktaby.Views;
using Maktaby.Views.Containers;
using Maktaby.Win32.NativeMethods;
using Maktaby.Win32.Services;
using Maktaby.Win32APIs.Services;
using Maktaby.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;

namespace Maktaby;

/// <summary>
/// Application entry point. Builds the composition root (dependency injection) so that all
/// platform-specific services are injected behind Core interfaces, then starts the desktop
/// Boxes via <see cref="DesktopManager"/> and lives in the system tray (see <see cref="Controls.TrayIconUI"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class App : Application
{
    #region Service provider and shared state
    public static IServiceProvider Services { get; private set; } = null!;

    // Tray infrastructure: the NotifyIcon must live in a visual tree, so it is hosted in a hidden
    // always-on window. When Explorer restarts the shell broadcasts "TaskbarCreated"; we then swap in
    // a fresh tray icon and re-glue the desktop layer (see TrayHostHook).
    private Window? _trayHost;
    private Controls.TrayIconUI? _tray;
    private uint _taskbarCreatedMsg;
    private bool _shellRecoveryPending;
    private bool _isSecondInstanceExit;
    private bool _isExiting;
    #endregion

    #region Startup
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Installer post-install flag: handle startup enable/disable before the
        // single-instance guard so the flag is honored even when another instance
        // is already running (the running instance isn't the one that should write
        // the registry on the user's behalf after an install).
        if (Array.IndexOf(e.Args, "--enable-startup") >= 0)
        {
            StartupManager.Enable();
            Shutdown();
            return;
        }
        if (Array.IndexOf(e.Args, "--disable-startup") >= 0)
        {
            StartupManager.Disable();
            Shutdown();
            return;
        }

        // Single-instance guard must run before any UI or persistence is touched.
        // The first instance holds the mutex for its lifetime; any secondary instance
        // just exits silently without touching OnExit save/restore.
        if (!Helpers.ApplicationSingleInstance.TryAcquire())
        {
            _isSecondInstanceExit = true;
            // No main Window to SetForegroundWindow — Maktaby hosts WS_EX_TOOLWINDOW boxes
            // and a hidden tray host, so MainWindowHandle is always 0. Second instance just exits
            // silently; SwitchToCurrentInstance() is intentionally not called.
            Shutdown();
            return;
        }

#if !DEBUG
        Maktaby.Shared.Helpers.ExceptionHandler.Register(this, new Maktaby.Shared.Helpers.ExceptionHandler.Options
        {
            IsIgnorable = ex => IsIgnorableWebViewShutdownException(ex) || Win32Apis.SystemTeardown,
            LogError = (ex, msg) => Logging.Log.Error(ex, msg),
            LogFatal = (ex, msg) => Logging.Log.Fatal(ex, msg),
            LogWarning = (msg) => Logging.Log.Warning(msg),
            // OutOfMemory (usually the composition channel starving on native exhaustion):
            // stop the two native-memory burners (video decode + widget rendering) so the
            // fault storm can't feed itself. Allocation-light and fully guarded — under OOM
            // every allocation is suspect. The app stays suspended after Continue (tray can
            // resume playback/widgets); resuming blindly would re-trigger the storm.
            SuspendApp = static ex =>
            {
                if (!IsOutOfMemory(ex)) return;
                try
                {
                    Services.GetService<LiveWallpaperManager>()?.SetTransientPaused(true);
                }
                catch { }
                try
                {
                    var app = Application.Current;
                    if (app is null) return;
                    foreach (var w in app.Windows.OfType<Views.Containers.WebWidgetWindow>())
                    {
                        try { w.WidgetControl?.Suspend(); } catch { }
                    }
                    foreach (var w in app.Windows.OfType<Views.Containers.NativeWidgetWindow>())
                    {
                        try { w.Widget?.Suspend(); } catch { }
                    }
                }
                catch { }
            },
        });
#endif
        //
        AppJSettings.Reload();
        Logging.InitializeDefaultLogger();
        //

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        BuildTrayAndMenuItems(Services);

        Services.GetRequiredService<ISettingsService>().Load();
        // Subscribe BEFORE the first ApplyTheme. ApplicationThemeManager.Changed is the single
        // place box appearance is applied, so the handler must already be attached when the
        // initial theme lands — otherwise the boxes come up unstyled for the whole session.
        ApplicationThemeManager.Changed += (_, _) => ApplyBoxAppearance();
        ApplyTheme(Services.GetRequiredService<ISettingsService>().UserSettings.SelectedTheme);
        // Ensure WebWidget storage roots exist (UserWidgets + EBWebView)
        try { Services.GetRequiredService<IWebWidgetService>().EnsureUserWidgetsRoot(); } catch { }
        // Gallery previews get their own environment under the managed tree (not %TEMP%), so the
        // startup prune below covers it. Must precede any preview control init.
        try { Maktaby.Shared.Controls.WebWidgetControl.UserDataFolder = Path.Combine(SettingsService.AppDataDir, "EBWebView.Previews"); } catch { }
        // Prune regenerable Chromium payload caches (GPU/shader/crash/logs, oversized HTTP cache)
        // off the UI thread: single-instance guard above guarantees no other process holds them.
        _ = Task.Run(() => WebViewUserDataMaintenance.TryPrune(SettingsService.AppDataDir));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Display scale/resolution changes can leave the tray popup with stale DPI/placement
        // state (small, unclickable menu). Re-create the icon once the layout settles.
        // DesktopManager.LayoutRefreshed fires for both resolution and DPI-only passes.
        Services.GetRequiredService<DesktopManager>().LayoutRefreshed += ScheduleTrayRefresh;

        // Windows shutdown / user logoff: disarm the minimize-prevention hooks BEFORE the session
        // starts collapsing our owned window chains, so teardown is not fought.
        SessionEnding += (_, _) => Win32Apis.SystemTeardown = true;

        // GPU-fallback forensics: a post-resume driver degradation can drop WPF to software
        // rendering (tier 0) — GDI churn in the thousands, GBs of native memory, idle CPU,
        // flat managed heap. Tier changes are rare and event-driven; log them so the next
        // post-resume blowup is attributable (see Performance Monitor "Render" line).
        // Tier 0 also auto-pauses playback + widgets (software rasterization of live content
        // is the likely burner and pointless eye candy); recovery resumes only what this
        // paused, never user/supervisor-paused content.
        try
        {
            System.Windows.Media.RenderCapability.TierChanged += (_, _) =>
            {
                try
                {
                    int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                    Log.Information("WPF render tier changed to {Tier} ({Mode})", tier, tier > 0 ? "hardware" : "SOFTWARE fallback");
                    try { Dispatcher.BeginInvoke(new Action(() => OnRenderTierChanged(tier))); } catch { }
                }
                catch { }
            };
        }
        catch { }

        // Power-resume render quiesce (see _resumeSuspendedWidgets): SystemEvents surfaces
        // the same resume broadcast the wallpaper engine converges on. Handler marshals to
        // the UI thread; unsubscribed in OnExit.
        try
        {
            _powerModeHandler = (_, e) =>
            {
                try
                {
                    if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
                    Log.Information("Power resume broadcast received — quiescing widget render stack");
                    try { Dispatcher.BeginInvoke(new Action(OnPowerResumeQuiesce)); } catch { }
                }
                catch { }
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += _powerModeHandler;
        }
        catch { }

        // Watch the live desktop icon size ONLY while it is actually used (DefaultBoxIconSize is
        // Auto/null). With an explicit value pinned, monitoring is suspended.
        SyncDesktopIconSizeWatcher();
        var iconSizes = Services.GetRequiredService<Core.Interfaces.IDesktopIconSizeService>();
        iconSizes.Changed += size =>
        {
            Log.Information("Desktop icon size changed to {Size}px", size);
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var w in Windows.OfType<BoxContainerWindow>())
                {
                    w.BoxContent?.RefreshIconSize();
                }
            });
        };

        // The global loading dialog drives initialization itself once first shown (it pumps
        // a nested message loop, so the Box windows are created under a fully-rendered WPF context).
        var loading = Services.GetRequiredService<ILoadingDialogService>();
        loading.StateChanged += (_, _) =>
        {
            // A loading dialog blocks all input, including the tray menu: the menu is not shown
            // at all while one is up (SetMenuSuppressed also closes one that was already open
            // when the dialog appeared). Read _tray per event, not captured, so this keeps
            // working across the tray icon being rebuilt on an Explorer restart.
            try
            {
                bool busy = loading.IsBusy;
                _tray?.SetMenuSuppressed(busy);
                // Loading dialogs are rare (startup and explicit user actions), so this cannot
                // spam — and it is the only way to explain "the tray menu did not open".
                Serilog.Log.Information("Loading dialog {State}: tray context menu {Menu}",
                    busy ? "shown" : "dismissed", busy ? "suppressed" : "available");
            }
            catch { }
        };
        await loading.ShowAsync(async report =>
        {
            report("Loading Maktaby…");
            // Let the splash paint and the dispatcher settle before the Shell enumeration /
            // Box creation work begins.
            await Task.Delay(800);
            var manager = Services.GetRequiredService<DesktopManager>();
            await manager.InitializeAsync();

            // Standalone live wallpaper (own windows, own lifecycle — independent from DesktopManager).
            try { await Services.GetRequiredService<LiveWallpaperManager>().InitializeAsync(); } catch { }
            return null;
        }, pausePlayback: false);

        // Update checks start only after the loading dialog is gone: a release check must
        // never overlap initialisation, and the prompt window is useless before the desktop
        // surface exists. OnStartupComplete also reports a version transition left behind by
        // a previous self-update.
        try
        {
            var updater = Services.GetRequiredService<WPFServices.UpdateManager>();
            updater.OnStartupComplete();
            updater.Start();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start the update checker");
        }

        //StartHealthSnapshots, when logging
        if (GlobalFeaturesSwitches.EnableHealthSnapshots) StartHealthSnapshots();
    }

    #endregion

    #region Shutdown
    protected override void OnExit(ExitEventArgs e)
    {
        // Secondary instance (single-instance guard) never initialized Services/DesktopManager;
        // skip all persistence and shell teardown — it just activated the first instance and exited.
        if (_isSecondInstanceExit || Services is null)
        {
            Helpers.ApplicationSingleInstance.Release();
            base.OnExit(e);
            return;
        }

        // Stop the minimize-prevention hooks from fighting window teardown (owned chains collapsing
        // fire WM_SHOWWINDOW hides that the hooks would otherwise counter, delaying shutdown).
        Win32Apis.SystemTeardown = true;
        _isExiting = true;
        // Detach the system-theme hook here. It is an HwndSource hook on the tray host, so
        // leaving it attached keeps this app (and its whole visual tree) rooted for the life
        // of the message pump. Done next to the power-resume teardown for the same reason.
        try { _systemThemeFollower?.Dispose(); } catch { }
        _systemThemeFollower = null;
        // Stop the periodic release check and cancel any in-flight request, so a background
        // timer cannot resurrect a dead App instance during teardown.
        try { Services.GetService<WPFServices.UpdateManager>()?.Dispose(); } catch { }
        // The power-resume quiesce owns suspended widgets + a one-shot timer: stop the timer
        // (no recovery on the way out — the process is dying) and drop the SystemEvents
        // subscription, whose hidden window would otherwise linger past shutdown.
        try { _resumeRecoverTimer?.Stop(); } catch { }
        _resumeRecoverTimer = null;
        _resumeSuspendedWidgets.Clear();
        try
        {
            if (_powerModeHandler is not null) Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerModeHandler;
            _powerModeHandler = null;
        }
        catch { }
        // Drop the shell icon before the UI thread blocks in teardown (ghost-icon prevention
        // on every exit path; the Exit-menu path already did this — dispose is idempotent).
        try
        {
            if (_trayHost != null) _trayHost.Content = null;
            (_tray as IDisposable)?.Dispose();
            _tray = null;
        }
        catch { }
        try { Services.GetRequiredService<LiveWallpaperManager>().Shutdown(); } catch { }


        var mang = Services.GetRequiredService<DesktopManager>();
        if (!mang.IsDisabled)
        {
            //RestoreIcons first. Guarded: Explorer may already be terminating during a system shutdown.
            try
            {
                mang.RestoreIcons();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RestoreIcons during exit failed (shell likely gone)");
            }

            Services.GetRequiredService<IMouseMonitor>().Stop();
            //async is not ok in app exit
            //await mang.SaveAsync();
            mang.SaveSync();
        }
        //
        Logging.DisposeAllDefaultLoggers();
        Helpers.ApplicationSingleInstance.Release();
        base.OnExit(e);
    }
    #endregion

    #region Dependency injection
    [SupportedOSPlatform("windows10.0.14393")]
    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILogger, Logger>((serv) => Logging.Log);
        //

        // Core (pure .NET, no platform dependencies)
        services.AddSingleton<IBoxService, BoxService>();
        services.AddSingleton<IContainerService, ContainerService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPersistenceService, JsonSnapshotPersistenceService>();
        services.AddSingleton<IRuleService, RuleService>();
        services.AddSingleton<IFileRuleCoordinator, FileRuleCoordinator>((serv) => new FileRuleCoordinator(
            serv.GetRequiredService<IShellWatcherService>(),
            serv.GetRequiredService<IRuleService>(),
            serv.GetRequiredService<IBoxService>(),
            serv.GetRequiredService<IDispatcher>(),
            serv.GetRequiredService<IFileOperationService>(),
            () => serv.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget()));

        services.AddSingleton<IFileOperationService, FileOperationService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();

        // Win32 platform services
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IDpiService, DpiService>();
        services.AddSingleton<IWindowSnappingService, WindowSnappingService>();
        services.AddSingleton<IWindowPositioningService, WindowPositioningService>();
        services.AddSingleton<IZOrderService, ZOrderService>();
        services.AddSingleton<IDesktopWindowService, DesktopWindowService>();
        services.AddSingleton<IExplorerDesktopService, ExplorerDesktopService>();
        services.AddSingleton<IShellWatcherService, ShellDesktopWatcher>();
        //services.AddSingleton<IFileWatcherService, DesktopFileWatcher>();
        services.AddSingleton<IDispatcher, WpfDispatcher>();
        services.AddSingleton<DesktopManager, DesktopManager>((serv) => new DesktopManager(serv));

        // Shell platform services
        services.AddSingleton<IShellItemService, ShellItemService>();
        services.AddSingleton<IShellIconService, ShellIconService>();
        services.AddSingleton<IDesktopService, DesktopService>();

        // UI services and view models
        services.AddSingleton<LiveWallpaperManager>();
        services.AddSingleton<IconImageService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ILoadingDialogService, LoadingDialogService>();
        services.AddSingleton<IWebWidgetService, WebWidgetService>();
        services.AddSingleton<INativeWidgetService, NativeWidgetService>();
        services.AddSingleton<INativeWidgetSettingsService, NativeWidgetSettingsService>();

        // Win32 watchers
        services.AddSingleton<IMouseMonitor, MouseMonitor>();
        services.AddSingleton<Core.Interfaces.IDesktopIconSizeService, Win32.Services.DesktopIconSizeService>();

        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IAppUpdateUi, WPFServices.WpfUpdateUi>();

        // Singleton: the constructor claims this process's AppUserModelID (writes the HKCU
        // key and calls SetCurrentProcessExplicitAppUserModelID), which must happen exactly
        // once and before any toast is shown. Windows 10/11 drops notifications from a process
        // that has not claimed an id, so this is what makes the update toast appear at all.
        services.AddSingleton<IToastNotificationService, Win32.Services.ToastNotificationService>();
        services.AddSingleton<WPFServices.UpdateManager>();

        // Debug overlay (single instance; toggled from the tray "Debug Desktop Tree" menu).
        services.AddSingleton<DesktopTreeDebugOverlay>();
    }

    #endregion

    #region Render tier and power-resume quiesce
    private System.Windows.Threading.DispatcherTimer? _healthTimer;

    private bool _tierPauseActive;
    private bool _tierWasWallpaperPlaying;
    private readonly List<Views.Containers.WidgetWindow> _tierSuspendedWidgets = new();

    // ---- post-resume render-stack quiesce (hibernate/sleep, tier sensor blind) ----
    // After a long hibernation the driver can report tier 2 while stranding WPF
    // render-target memory per frame (2026-09-28: DUCE.Channel.SyncFlush OOM x23,
    // +2.9 GB private, +2100 GDI, flat managed heap — and zero tier lines in the log,
    // so the tier-0 pass above never engaged). The only continuously-invalidating
    // visual is an animated native widget (NativeClock at 10 Hz): hidden, nothing
    // invalidates and nothing leaks; re-shown, every tick allocates through the
    // degraded channel. This pass is therefore keyed on the power-resume broadcast
    // (the same PBT_APMRESUMEAUTOMATIC the wallpaper engine converges on), not on
    // the tier: suspend widgets + release their render resources + force the whole
    // composition pipeline onto software up front, then hand everything back after
    // one settle delay. Mirrors the tier-0 pass shape (suspend only what isn't,
    // resume only what this pass suspended) so the two compose — see RecoverPowerResume.
    private readonly List<Views.Containers.WidgetWindow> _resumeSuspendedWidgets = new();
    private System.Windows.Threading.DispatcherTimer? _resumeRecoverTimer;
    private Microsoft.Win32.PowerModeChangedEventHandler? _powerModeHandler;

    // System-theme watcher, live ONLY while the user theme is "System". Static because it is
    // driven by the static ApplyTheme funnel, which every theme change passes through.
    // Disposed in OnExit: an HwndSource hook left attached would keep this app (and its whole
    // visual tree) rooted for the life of the message host.
    private static Helpers.SystemThemeFollower? _systemThemeFollower;

    // The hidden _trayHost window, registered as the hook's host once it exists. Null until
    // BuildTrayAndMenuItems has shown it, which is why SyncSystemThemeFollower is a no-op
    // until then (it re-runs on the next theme change, and ApplyTheme at startup happens
    // after the tray host is built).
    private static Window? _themeFollowHost;

    /// <summary>Render-tier fallback response (UI thread): tier 0 pauses playback and widgets,
    /// recovery resumes only what this paused. Fully guarded — tier events can arrive during
    /// teardown, and every call here must survive a dying process.</summary>
    private void OnRenderTierChanged(int tier)
    {
        try
        {
            if (_isExiting || Services is null) return;
            if (tier == 0 && !_tierPauseActive)
            {
                _tierPauseActive = true;
                _tierWasWallpaperPlaying = false;
                try
                {
                    var lw = Services.GetService<LiveWallpaperManager>();
                    _tierWasWallpaperPlaying = lw?.IsPlaying == true;
                    if (_tierWasWallpaperPlaying) lw!.SetTransientPaused(true);
                }
                catch { }
                _tierSuspendedWidgets.Clear();
                // Census must be taken BEFORE the drop loop below, or it would report the
                // post-drop state and the before/after pair in the log would be meaningless.
                string censusBefore = BuildWidgetCensus();
                try
                {
                    var wins = Application.Current?.Windows;
                    if (wins is not null)
                    {
                        foreach (var w in wins)
                        {
                            if (w is not Views.Containers.WebWidgetWindow
                                && w is not Views.Containers.NativeWidgetWindow) continue;
                            try
                            {
                                var ww = (Views.Containers.WidgetWindow)w;
                                if (!ww.IsSuspended)
                                {
                                    ww.Suspend();
                                    _tierSuspendedWidgets.Add(ww);
                                }
                                // Releases cached render-target bitmaps and freezes the
                                // chrome overlay — distinct from Suspend(), which only
                                // stops the plugin's own timers/animations.
                                ww.OnRenderTierChanged(true);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Render tier 0: paused live wallpaper + {Widgets} widgets until hardware recovers | {Memory} | census BEFORE drop: {Census}",
                     _tierSuspendedWidgets.Count, MemoryFragment(), censusBefore);
            }
            else if (tier > 0 && _tierPauseActive)
            {
                _tierPauseActive = false;
                try
                {
                    foreach (var w in _tierSuspendedWidgets)
                    {
                        try { w.Resume(); } catch { }
                    }
                }
                catch { }
                finally
                {
                    // Drop the references either way: a window that died while tier-paused
                    // must not be resurrected by this list, and holding it would retain its
                    // whole visual tree plus the plugin's collectible ALC for the entire
                    // tier-0 window.
                    _tierSuspendedWidgets.Clear();
                }
                try
                {
                    // Recovery hand-back runs even if the process is mid-teardown, so the
                    // cache modes are never left dropped on a live widget.
                    var wins = Application.Current?.Windows;
                    if (wins is not null)
                    {
                        foreach (var w in wins)
                        {
                            if (w is not Views.Containers.WidgetWindow ww) continue;
                            try { ww.OnRenderTierChanged(false); } catch { }
                        }
                    }
                }
                catch { }
                try
                {
                    if (_tierWasWallpaperPlaying)
                        Services.GetService<LiveWallpaperManager>()?.SetTransientPaused(false);
                }
                catch { }
                finally
                {
                    _tierWasWallpaperPlaying = false;
                }
                if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Render tier recovered to {Tier}: resumed tier-paused playback + widgets | {Memory} | census AFTER restore: {Census}",
                    tier, MemoryFragment(), BuildWidgetCensus());
            }
        }
        catch { }
    }

    /// <summary>Power-resume render-stack quiesce (UI thread). Suspends live widget windows,
    /// releases their cached render resources, and forces the whole WPF composition pipeline
    /// onto software rendering until <see cref="RecoverPowerResume"/> runs after the settle
    /// delay. Skips the widget half while the tier-0 pass is active (it owns suspend/resume
    /// then); the pipeline override is still applied, it is a no-op under tier 0.
    /// A resume landing inside the settle window recovers the earlier pass first, so its
    /// suspended widgets are never orphaned, then quiesces fresh.</summary>
    private void OnPowerResumeQuiesce()
    {
        try
        {
            if (_isExiting || Services is null) return;
            RecoverPowerResume();

            // Force software NOW, before any post-resume invalidation runs: the degraded
            // channel strands per frame, and the first frames after resume are the storm
            // (DPI remaps, layer re-glue, overlay re-syncs).
            try { System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; } catch { }

            if (!_tierPauseActive)
            {
                _resumeSuspendedWidgets.Clear();
                // Census BEFORE the drop loop, same as the tier-0 pass.
                string censusBefore = BuildWidgetCensus();
                int released = 0;
                try
                {
                    var wins = Application.Current?.Windows;
                    if (wins is not null)
                    {
                        foreach (var w in wins)
                        {
                            if (w is not Views.Containers.WebWidgetWindow
                                && w is not Views.Containers.NativeWidgetWindow) continue;
                            try
                            {
                                var ww = (Views.Containers.WidgetWindow)w;
                                if (!ww.IsSuspended)
                                {
                                    ww.Suspend();
                                    _resumeSuspendedWidgets.Add(ww);
                                }
                                // Same release the tier-0 pass does: cached render-target
                                // bitmaps dropped, chrome overlay frozen hidden.
                                ww.OnRenderTierChanged(true);
                                released++;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Power resume: quiesced {Widgets} widgets ({Released} tier-released) on software pipeline until settle | {Memory} | census BEFORE drop: {Census}",
                    _resumeSuspendedWidgets.Count, released, MemoryFragment(), censusBefore);
            }
            else if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Power resume: tier-0 pass active, it owns widget suspend — software pipeline forced only | {Memory}",
                MemoryFragment());

            // One-shot recovery, not a watchdog: a single trailing pass after the
            // driver/shell handshake settles (same 10 s the engine converges on).
            try { _resumeRecoverTimer?.Stop(); } catch { }
            _resumeRecoverTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _resumeRecoverTimer.Tick += (_, _) => RecoverPowerResume();
            _resumeRecoverTimer.Start();
        }
        catch { }
    }

    /// <summary>One-shot recovery for <see cref="OnPowerResumeQuiesce"/>: hands the pipeline
    /// decision back to the tier (safe at any tier — Default under tier 0 is still software)
    /// and resumes exactly the widgets this pass suspended. Idempotent.
    /// When the tier-0 pass is active it owns the widgets: our suspended list is adopted into
    /// its list instead of resuming behind its back (those windows were already suspended when
    /// tier-0 arrived, so the tier pass never listed them — adopting is the only way they get
    /// resumed on tier recovery).</summary>
    private void RecoverPowerResume()
    {
        try
        {
            try { _resumeRecoverTimer?.Stop(); } catch { }
            _resumeRecoverTimer = null;
            if (_isExiting || Services is null)
            {
                _resumeSuspendedWidgets.Clear();
                return;
            }
            try { System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default; } catch { }
            if (_resumeSuspendedWidgets.Count == 0) return;

            if (_tierPauseActive)
            {
                int adopted = 0;
                foreach (var w in _resumeSuspendedWidgets)
                {
                    try
                    {
                        if (w.IsClosed || w.IsClosing) continue;
                        if (!_tierSuspendedWidgets.Contains(w)) { _tierSuspendedWidgets.Add(w); adopted++; }
                    }
                    catch { }
                }
                _resumeSuspendedWidgets.Clear();
                if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Power resume recovery: tier-0 active, adopted {Adopted} suspended widgets into tier ownership | {Memory}",
                    adopted, MemoryFragment());
                return;
            }
            try
            {
                foreach (var w in _resumeSuspendedWidgets)
                {
                    try
                    {
                        // Dead windows stay dead: resuming one would resurrect its visual
                        // tree and pin its plugin's collectible ALC (same rule as tier recovery).
                        if (!w.IsClosed && !w.IsClosing) w.Resume();
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                _resumeSuspendedWidgets.Clear();
            }
            try
            {
                // Recovery hand-back runs over live windows only, so dropped cache modes are
                // never written into a torn-down plugin visual (same rule as tier recovery).
                var wins = Application.Current?.Windows;
                if (wins is not null)
                {
                    foreach (var w in wins)
                    {
                        if (w is not Views.Containers.WidgetWindow ww) continue;
                        try { ww.OnRenderTierChanged(false); } catch { }
                    }
                }
            }
            catch { }
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Power resume recovery: pipeline back to tier, widgets resumed | {Memory} | census AFTER restore: {Census}",
                MemoryFragment(), BuildWidgetCensus());
        }
        catch { }
    }

    #endregion

    #region Diagnostics
    /// <summary>Per-widget render-resource census for the tier-transition and health log lines.
    /// Cached render-target bitmaps and effects are the resources that strand native memory
    /// when the tier flips underneath them, so they are the attribution signal: a native-memory
    /// climb with the cache count pinned at 0 after a tier-0 pass rules the BitmapCache
    /// hypothesis out and points at the window surfaces instead.
    /// <para>
    /// Roots are the PLUGIN visuals (<c>WidgetHost.Content</c>), not <c>Window.Content</c>: the
    /// latter is the whole window chrome and would count host-owned elements that are never
    /// dropped, burying the per-widget signal.
    /// </para></summary>
    private static string BuildWidgetCensus()
    {
        try
        {
            var roots = new List<(string, System.Windows.FrameworkElement)>();
            var wins = Application.Current?.Windows;
            if (wins is not null)
            {
                foreach (var w in wins)
                {
                    try
                    {
                        if (w is Views.Containers.NativeWidgetWindow nw)
                        {
                            // PluginVisual is the plugin's own visual tree — the thing whose
                            // CacheMode we drop on tier 0. The host chrome around it is
                            // never dropped, so counting it would bury the per-widget signal.
                            var content = nw.PluginVisual;
                            var title = string.IsNullOrWhiteSpace(nw.Title) ? "native" : nw.Title;
                            if (content is not null) roots.Add((title, content));
                        }
                        else if (w is Views.Containers.WebWidgetWindow ww2)
                        {
                            var title = string.IsNullOrWhiteSpace(ww2.Title) ? "web" : ww2.Title;
                            var content = ww2.WidgetVisual;
                            if (content is not null) roots.Add((title, content));
                        }
                    }
                    catch { }
                }
            }
            var entries = new List<Helpers.WidgetRenderCensus.Entry>();
            Helpers.WidgetRenderCensus.WalkAll(roots, entries);
            return Helpers.WidgetRenderCensus.Describe(entries);
        }
        catch (Exception ex) { return $"census failed: {ex.Message}"; }
    }
    /// <summary>Black-box forensics: one guarded line a minute (private bytes, managed heap,
    /// GDI/USER handles, render tier, wallpaper state, engine rebuilds, window counts) so the
    /// next native-memory incident is attributable from the log alone — no UI needed, since
    /// an OOMing process can no longer open windows.</summary>
    private void StartHealthSnapshots()
    {
        try
        {
            WriteHealthSnapshot();
            _healthTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1)
            };
            _healthTimer.Tick += (_, _) => WriteHealthSnapshot();
            _healthTimer.Start();
        }
        catch { }
    }

    /// <summary>Memory counters, formatted as a log fragment. Split out of
    /// <see cref="WriteHealthSnapshot"/> so the render-tier transition lines can carry the same
    /// numbers ungated: the periodic health line is behind
    /// <see cref="GlobalFeaturesSwitches.EnableHealthSnapshots"/> (deliberately off in release —
    /// it's an investigation tool, not a production feature), but the tier transition is the one
    /// correlation point for a native-memory incident and must report private bytes whether or not
    /// anything else is switched on. A private-bytes climb visible only on the gated line would
    /// leave a released build with no evidence at all.
    /// <para>
    /// Cheap by construction: a couple of P/Invocations and two reads, and only on tier
    /// transitions and (when enabled) once a minute. Never call it from a per-frame path.
    /// </para></summary>
    private static string MemoryFragment()
    {
        long privMB = -1, managedMB = -1;
        uint gdi = 0, user = 0;
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); privMB = p.PrivateMemorySize64 / 1024 / 1024; } catch { }
        try { managedMB = System.GC.GetTotalMemory(false) / 1024 / 1024; } catch { }
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            gdi = Win32Apis.GetGuiHandleCount(p.Handle, false);
            user = Win32Apis.GetGuiHandleCount(p.Handle, true);
        }
        catch { }
        return $"private={privMB}MB managed={managedMB}MB gdi={gdi} user={user}";
    }

    private static void WriteHealthSnapshot()
    {
        try
        {
            int tier = -1;
            try { tier = System.Windows.Media.RenderCapability.Tier >> 16; } catch { }
            string reapply = "?";
            bool playing = false, live = false;
            try
            {
                var lw = Services.GetService<LiveWallpaperManager>();
                if (lw is not null) { reapply = lw.GetReapplyInfo(); playing = lw.IsPlaying; live = lw.EngineIsLive; }
            }
            catch { }
            int boxes = 0, widgets = 0;
            try
            {
                var wins = Application.Current?.Windows;
                if (wins is not null)
                {
                    foreach (var w in wins)
                    {
                        if (w is BoxContainerWindow) boxes++;
                        else if (w is Views.Containers.WebWidgetWindow || w is Views.Containers.NativeWidgetWindow) widgets++;
                    }
                }
            }
            catch { }
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Information)) Log.Information("Health: {Memory} tier={Tier} wallpaper={Playing}/{Live} reapply={Reapply} boxes={Boxes} widgets={Widgets} | {Census}",
                MemoryFragment(), tier, playing, live, reapply, boxes, widgets, BuildWidgetCensus());
        }
        catch { }
    }

    #endregion

    #region Exception filtering
    /// <summary>True when <paramref name="ex"/> or any inner exception is an
    /// <see cref="OutOfMemoryException"/> (managed or composition-channel starvation).</summary>
    private static bool IsOutOfMemory(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OutOfMemoryException) return true;
        }
        return false;
    }

    private static bool IsIgnorableWebViewShutdownException(Exception? ex)
    {
        if (ex == null) return false;
        var msg = ex.ToString();
        // WebView2's internal IsVisible setter throws ArgumentException/InvalidOperationException
        // with "CoreWebView2Controller" and "IsVisible" when the control is torn down after dispose
        if (msg.Contains("IsVisible", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("CoreWebView2", StringComparison.OrdinalIgnoreCase))
            return true;
        if (msg.Contains("CoreWebView2Controller", StringComparison.OrdinalIgnoreCase))
            return true;
        // Unwrap aggregate/inner
        if (ex.InnerException != null && IsIgnorableWebViewShutdownException(ex.InnerException))
            return true;
        return false;
    }

    #endregion

    #region Theming
    /// <summary>
    /// Applies the application theme (Dark / Light / System) to the whole app via WPF-UI's
    /// <see cref="ApplicationThemeManager"/>. <paramref name="selectedTheme"/> uses the same
    /// string convention as <see cref="Settings.UserSettings.SelectedTheme"/> ("dark", "light",
    /// or <c>null</c>/anything else for the system theme).
    /// </summary>
    [SupportedOSPlatform("windows10.0.14393")]
    public static void ApplyTheme(string? selectedTheme)
    {
        switch (selectedTheme?.Trim().ToLowerInvariant())
        {
            case "light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light);//, Wpf.Ui.Controls.WindowBackdropType.Mica
                break;
            case "dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);//, Wpf.Ui.Controls.WindowBackdropType.Mica
                break;
            default:
                // NOT ApplicationThemeManager.ApplySystemTheme(): that resolves the wrong
                // value on Win10/11 (it reads the visual-style file, not the light/dark
                // preference), which is why "System" used to resolve to Dark on a Light
                // machine. See Helpers/SystemThemeReader for the full diagnosis.
                // ReadPreferredAppTheme returns null for high contrast, where Wpf.Ui's own
                // style-to-dictionary mapping is still correct — so it stays the fallback.
                var systemTheme = Helpers.SystemThemeReader.ReadPreferredAppTheme();
                if (systemTheme.HasValue)
                {
                    ApplicationThemeManager.Apply(systemTheme.Value);
                }
                else
                {
                    ApplicationThemeManager.ApplySystemTheme();
                }
                break;
        }

        // No ApplyBoxAppearance() here on purpose: ApplicationThemeManager.Changed fires on
        // EVERY Apply(), including a redundant one that swaps in the theme already loaded
        // (verified against Wpf.Ui 4.3.0), and that handler is what repaints the boxes. Calling
        // both would run the full box repaint twice per theme change. The handler is
        // subscribed in OnStartup BEFORE the first ApplyTheme, which is what makes relying on
        // it safe for the initial theme.
        //
        // Keep the system-theme watcher in step with the preference, for the same "one place
        // owns it" reason.
        SyncSystemThemeFollower(selectedTheme);
    }

    /// <summary>Attaches the system-theme watcher only while the user theme is "System"
    /// (<paramref name="selectedTheme"/> null/blank), and detaches it as soon as an explicit
    /// Dark/Light is chosen. Attaching unconditionally would mean a message hook firing on
    /// every Windows preference change for the whole session, in a setting where the answer is
    /// always "ignore it" — the callback would only ever early-return.</summary>
    private static void SyncSystemThemeFollower(string? selectedTheme)
    {
        try
        {
            bool follow = string.IsNullOrWhiteSpace(selectedTheme);

            if (!follow)
            {
                _systemThemeFollower?.Dispose();
                _systemThemeFollower = null;
                return;
            }

            if (_systemThemeFollower is not null || _themeFollowHost is null) { return; }

            var follower = new Helpers.SystemThemeFollower(OnSystemThemeChanged);
            follower.Attach(_themeFollowHost);
            _systemThemeFollower = follower;
        }
        catch (Exception ex)
        {
            // A failure to observe the OS theme must never break applying the theme itself.
            Log.Warning(ex, "Could not sync the system theme follower (theme will not follow Windows)");
        }
    }

    /// <summary>Re-applies the app theme after a Windows light/dark change. Only ever reached
    /// while the preference is "System", because the watcher is detached otherwise — but the
    /// setting is re-read anyway so a stale or in-flight message can never override an
    /// explicit Dark/Light pick.</summary>
    private static void OnSystemThemeChanged()
    {
        try
        {
            if (Services is null) { return; }

            var selected = Services.GetRequiredService<ISettingsService>().UserSettings.SelectedTheme;
            if (!string.IsNullOrWhiteSpace(selected)) { return; }

            // ApplySystemTheme() is one-shot, so this is exactly the call that has to be
            // re-run. It re-merges the Wpf.Ui resource dictionaries and raises
            // ApplicationThemeManager.Changed, which repaints the boxes and widgets.
            ApplyTheme(null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to follow the system theme change");
        }
    }

    /// <summary>
    /// Applies the configured box appearance (theme + default box colors, with per-container
    /// transparency overrides) to the shared resources and every open <see cref="BoxContainerWindow"/>.
    /// </summary>
    [SupportedOSPlatform("windows10.0.14393")]
    public static void ApplyBoxAppearance()
    {
        var settings = Services.GetRequiredService<ISettingsService>().UserSettings;
        var palette = BoxAppearance.Resolve(settings);

        // Shared resources (consumed via DynamicResource by BoxControl item styling, etc.).
        var app = Application.Current;
        if (app != null)
        {
            app.Resources["BoxBackground"] = palette.Back;
            app.Resources["BoxForeground"] = palette.Fore;
            app.Resources["BoxBorder"] = palette.Border;
            app.Resources["BoxHeaderBackground"] = palette.HeaderBack;
            app.Resources["BoxHeaderForeground"] = palette.HeaderFore;
            app.Resources["BoxBorderThickness"] = palette.Thickness;

            foreach (var window in app.Windows.OfType<BoxContainerWindow>())
            {
                window.ApplyAppearance();
            }
        }
    }

    #endregion

    #region Tray icon and shell recovery
    private void BuildTrayAndMenuItems(IServiceProvider services)
    {
        // WPF-UI's NotifyIcon must live inside a visual tree, so host it in an always-on
        // window that stays loaded for the app's lifetime. It must never minimize: a 0x0
        // minimized window with no taskbar button cannot go to the taskbar, so the shell
        // parks it as a tiny floating window (bottom-left on startup). Normal state far
        // off-screen instead: Show() still fires Loaded for the icon, nothing can paint.
        _trayHost = new Window
        {
            Width = 0,
            Height = 0,
            Left = -10000,
            Top = -10000,
            WindowState = WindowState.Normal,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            // No AllowsTransparency: this host is never visibly shown, and a layered
            // redirect for an invisible 0x0 window is pure DWM overhead.
            Visibility = Visibility.Visible,
        };

        ReplaceTrayIcon(services);
        // Hide once loaded: a hidden host appears nowhere (screen, Alt+Tab, Task View)
        // while the shell icon, context menu and TaskbarCreated hook — independent HWNDs —
        // keep working. Attached before Show so the event cannot be missed.
        _trayHost.Loaded += TrayHost_HideOnce;
        _trayHost.Show();

        // This hidden window lives for the whole session and already owns a message pump
        // (the TaskbarCreated hook below), so it is the right host for the system-theme
        // watcher — no extra native handle just to observe theme broadcasts. Show() has run,
        // so its HWND exists.
        //
        // Registration only: the watcher is NOT attached from here, because this runs BEFORE
        // ISettingsService.Load() (see OnStartup), so UserSettings.SelectedTheme is still
        // unpopulated and would read as "System" for a user who actually picked Dark. The
        // ApplyTheme call later in OnStartup runs after the load and attaches correctly.
        _themeFollowHost = _trayHost;

        // Double-clicking empty desktop area toggles the same hide-all state (icon double-clicks still open).
        Services.GetRequiredService<IMouseMonitor>().DesktopDoubleClick += OnDesktopDoubleClick;

        // Explorer restarts broadcast "TaskbarCreated" (registered message). React by re-registering
        // the tray icon and re-gluing surface/boxes to the new desktop layer.
        _taskbarCreatedMsg = Win32Apis.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMsg != 0)
        {
            HwndSource.FromHwnd(new WindowInteropHelper(_trayHost).Handle)?.AddHook(TrayHostHook);
        }
    }

    private void TrayHost_HideOnce(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_trayHost != null)
            {
                _trayHost.Loaded -= TrayHost_HideOnce;
                _trayHost.Visibility = Visibility.Hidden;
            }
        }
        catch { }
    }

    private IntPtr TrayHostHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // While disabled, only re-register the tray icon — do NOT recover the desktop layer.
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg && !_shellRecoveryPending)
        {
            _shellRecoveryPending = true;
            var isDisabled = Services.GetRequiredService<DesktopManager>().IsDisabled;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ReplaceTrayIcon(Services);
                    if (!isDisabled)
                        Services.GetRequiredService<DesktopManager>().RecoverAfterShellRestart();
                    // Wallpaper is independent from boxes: always re-glue it.
                    Services.GetRequiredService<LiveWallpaperManager>().RecoverAfterShellRestart();
                    Log.Information("Shell restarted — tray icon recovered{Recovery}", isDisabled ? " (layer suspended)" : " + desktop layer");
                }
                catch (System.Exception ex)
                {
                    Log.Error(ex, "Shell-restart recovery failed");
                }
                finally
                {
                    _shellRecoveryPending = false;
                }
            });
        }

        return IntPtr.Zero;
    }

    /// <summary>Creates a fresh TrayIconUI with all event wiring and swaps it into the host window.
    /// Called once at startup and again after every Explorer restart.</summary>
    private void ReplaceTrayIcon(IServiceProvider services)
    {
        if (_tray is not null && ReferenceEquals(_trayHost?.Content, _tray))
        {
            _trayHost!.Content = null;
        }

        // The old icon must be disposed: merely dropping it leaves its shell registration alive
        // and each refresh adds a visible duplicate. (Wpf.Ui NotifyIcon implements IDisposable.)
        if (_tray is not null)
        {
            try { (_tray as IDisposable)?.Dispose(); } catch { }
            _tray = null;
        }

        var tray = new TrayIconUI();
        tray.NewBoxRequested += (_, _) => Services.GetRequiredService<DesktopManager>().NewBox();
        tray.NewBoxFolderPortalRequested += (_, _) => Services.GetRequiredService<DesktopManager>().NewFolderPortal();
        tray.NewWidgetRequested += (_, _) => ShowWidgetsList(selectMode: true);
        tray.ManageWidgetsRequested += (_, _) => ShowWidgetsList(selectMode: false);
        tray.LiveWallpaperToggleEnable += async (_, _) =>
        {
            var lw = Services.GetRequiredService<LiveWallpaperManager>();
            try { await lw.SetEnabledAsync(!lw.IsEnabled); } catch { }
        };
        tray.LiveWallpaperTogglePlayPause += async (_, _) =>
        {
            var lw = Services.GetRequiredService<LiveWallpaperManager>();
            try { await lw.SetPlayingAsync(!lw.IsPlaying); } catch { }
        };
        tray.LiveWallpaperChangeRequested += async (_, _) =>
        {
            // Filter comes from the engine itself (WallPaperFilePicker builds it from
            // WallpaperPath.ImageExtensions + CodecSupport.VideoExtensions), so it never drifts
            // from what the renderers can actually play. No owner HWND here (tray menu),
            // so the parameterless overload passes IntPtr.Zero — never a process handle.
            var newFile = Maktaby.LiveWallpaper.WallpaperFilePicker.PickMedia();
            if (string.IsNullOrEmpty(newFile) || !File.Exists(newFile)) return;
            try { await Services.GetRequiredService<LiveWallpaperManager>().SetWallpaperAsync(newFile); } catch { }
        };
        tray.LiveWallpaperRemoveRequested += async (_, _) =>
        {
            try { await Services.GetRequiredService<LiveWallpaperManager>().RemoveAsync(); } catch { }
        };
        tray.ResetRequested += async (_, _) =>
        {
            var confirmed = await Services.GetRequiredService<IDialogService>().ShowConfirmAsync(
                "This will delete all the boxes and create default one, continue?",
                new DialogOptions { Title = "Confirm delete", PrimaryButtonText = "Reset" });
            if (confirmed) await Services.GetRequiredService<DesktopManager>().ResetAsync();
        };
        tray.SettingsRequested += (_, _) =>
        {
            var vm = Services.GetRequiredService<SettingsViewModel>();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // Only one settings window at a time.
                foreach (var w in Application.Current.Windows)
                {
                    if (w is SettingsView existing)
                    {
                        existing.Activate();
                        return;
                    }
                }

                new SettingsView(vm).Show();
            });
        };
        // Debug: the same check as above but NON-interactive, so it takes the background path
        // and raises the notification instead of opening the prompt. There is no other way to
        // trigger that path by hand - startup and the 6-hour timer are the only real callers.
        tray.BackgroundUpdateCheckRequested += (_, _) =>
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await Services.GetRequiredService<WPFServices.UpdateManager>()
                        .CheckAsync(interactive: false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Background update check (debug) failed");
                }
            });
        };

        // Manual "Check for updates…": interactive, so the user always gets an answer — an
        // up-to-date result, a skipped version, or a clear failure — instead of silence.
        tray.CheckForUpdatesRequested += (_, _) =>
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await Services.GetRequiredService<WPFServices.UpdateManager>()
                        .CheckAsync(interactive: true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Manual update check failed");
                }
            });
        };

        tray.AboutRequested += (_, _) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // Only one About window at a time.
                foreach (var w in Application.Current.Windows)
                {
                    if (w is Views.AboutView existing)
                    {
                        existing.Activate();
                        return;
                    }
                }

                new Views.AboutView().Show();
            });
        };
        tray.ThemeRequested += (_, theme) =>
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            settings.UserSettings.SelectedTheme = theme;
            settings.Save();
            // ApplyTheme applies the theme and registers the system-theme watcher; the boxes
            // are repainted by the ApplicationThemeManager.Changed handler.
            ApplyTheme(theme);
        };
        tray.ExitRequested += async (_, _) =>
        {
            if (_isExiting) return;
            _isExiting = true;
            // Disarm first: windows close inside Shutdown() and the minimize-prevention
            // hooks would otherwise fight each hide (re-show), stalling teardown window
            // by window. Previously this was only set in OnExit — after windows closed.
            Win32Apis.SystemTeardown = true;
            // Unregister the shell icon NOW (fast, never blocks): instant feedback, and no
            // ghost icon lingers while the UI thread is buried in teardown below.
            try { tray.contextMenu.IsOpen = false; } catch { }
            try
            {
                if (ReferenceEquals(_trayHost?.Content, tray))
                    _trayHost!.Content = null;
                (tray as IDisposable)?.Dispose();
                if (ReferenceEquals(_tray, tray))
                    _tray = null;
            }
            catch { }
            await Task.Delay(200);
            // Defer the blocking teardown one dispatcher pass so the menu unpaints and
            // releases mouse capture first: Render outranks Background, so by the time this
            // runs the menu is visibly gone instead of frozen on screen for the whole
            // teardown (engine dispose waits, WebView2 disposal, shell round-trips).
            // Setting IsOpen=false alone can never work — the close needs a render pass
            // the synchronous Shutdown() never yields.
            try
            {
                await Dispatcher.BeginInvoke(new Action(Shutdown), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
                // Dispatcher already draining (e.g. Exit during logoff): fall back to direct.
                try { Shutdown(); } catch { }
            }
        };
        tray.ToggleHideAllRequested += (_, _) =>
        {
            Services.GetRequiredService<DesktopManager>().ToggleHideAllBoxes();
        };

        tray.MenuToggleDisableClick += async (_, _) =>
        {
            await Services.GetRequiredService<DesktopManager>().ToggleDisableAsync();
        };

        tray.DebugTreeRequested += (_, _) =>
        {
            var overlay = Services.GetRequiredService<DesktopTreeDebugOverlay>();
            if (overlay.IsVisible)
            {
                overlay.Stop();
                overlay.Hide();
            }
            else
            {
                overlay.Show();
                overlay.Start();
            }
        };

        _trayHost!.Content = tray;
        _tray = tray;

        // Apply the current loading-dialog state. A tray rebuilt mid-dialog (Explorer restart,
        // DPI layout pass) must come up already suppressed; StateChanged only fires on a change,
        // so it would otherwise leave this fresh icon's menu openable until the next toggle.
        try { tray.SetMenuSuppressed(Services.GetRequiredService<ILoadingDialogService>().IsBusy); } catch { }
    }

    private System.Windows.Threading.DispatcherTimer? _trayRefreshDebounce;

    /// <summary>Re-creates the tray icon after a display/DPI layout pass (debounced through bursts).
    /// A scale change can strand the popup with stale DPI/placement state; a fresh icon re-anchors it.</summary>
    private void ScheduleTrayRefresh()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _trayRefreshDebounce ??= new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.5)
                    };
                    _trayRefreshDebounce.Tick -= TrayRefreshDebounceTick;
                    _trayRefreshDebounce.Tick += TrayRefreshDebounceTick;
                    _trayRefreshDebounce.Stop();
                    _trayRefreshDebounce.Start();
                }
                catch { }
            }));
        }
        catch { }
    }

    private void TrayRefreshDebounceTick(object? sender, EventArgs e)
    {
        try
        {
            if (_trayRefreshDebounce is not null)
            {
                _trayRefreshDebounce.Stop();
                _trayRefreshDebounce.Tick -= TrayRefreshDebounceTick;
            }
            if (Services is not null && _trayHost is not null)
            {
                ReplaceTrayIcon(Services);
            }
        }
        catch { }
    }

    #endregion

    #region Desktop and widget commands
    private void ShowWidgetsList(bool selectMode)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var w in Application.Current.Windows.OfType<Views.WidgetsListWindow>())
            {
                w.Activate();
                return;
            }
            var win = new Views.WidgetsListWindow(selectMode);
            if (win.ShowDialog() == true)
            {
                if (win.SelectedNativeInfo is not null)
                    Services.GetRequiredService<DesktopManager>().NewNativeWidget(win.SelectedNativeInfo.Slug);
                else if (win.SelectedInfo is not null)
                    Services.GetRequiredService<DesktopManager>().NewWebWidget(win.SelectedInfo.Slug, win.SelectedInfo.Source);
            }
        });
    }

    private void OnDesktopDoubleClick(object? sender, EventArgs e)
    {
        // A loading dialog owns all input: ignore the hide-all toggle while busy.
        try { if (Services.GetRequiredService<ILoadingDialogService>().IsBusy) return; } catch { }
        Services.GetRequiredService<DesktopManager>().ToggleHideAllBoxes();
    }

    /// <summary>Starts or suspends the desktop-icon-size watcher to match the current
    /// DefaultBoxIconSize setting (Auto/null = watch; explicit value = suspend).</summary>
    [SupportedOSPlatform("windows10.0.14393")]
    public static void SyncDesktopIconSizeWatcher()
    {
        var svc = Services.GetRequiredService<Core.Interfaces.IDesktopIconSizeService>();
        var isAuto = Services.GetRequiredService<ISettingsService>().UserSettings.DefaultBoxIconSize is null;
        if (isAuto)
        {
            svc.Start();
        }
        else
        {
            svc.Stop();
        }
    }
    #endregion
}
