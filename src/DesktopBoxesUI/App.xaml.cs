using DesktopBoxesUI.Controls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Settings;
using DesktopBoxesUI.Shell.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Views.Containers;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32.Services;
using DesktopBoxesUI.Win32APIs.Services;
using DesktopBoxesUI.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using WPFShared.Interfaces;
using WPFShared.Services;

namespace DesktopBoxesUI;

/// <summary>
/// Application entry point. Builds the composition root (dependency injection) so that all
/// platform-specific services are injected behind Core interfaces, then starts the desktop
/// Boxes via <see cref="DesktopManager"/> and lives in the system tray (see <see cref="Controls.TrayIconUI"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    // Tray infrastructure: the NotifyIcon must live in a visual tree, so it is hosted in a hidden
    // always-on window. When Explorer restarts the shell broadcasts "TaskbarCreated"; we then swap in
    // a fresh tray icon and re-glue the desktop layer (see TrayHostHook).
    private Window? _trayHost;
    private Controls.TrayIconUI? _tray;
    private uint _taskbarCreatedMsg;
    private bool _shellRecoveryPending;
    private bool _isSecondInstanceExit;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Single-instance guard must run before any UI or persistence is touched.
        // The first instance holds the mutex for its lifetime; any secondary instance
        // just activates the first and exits without touching OnExit save/restore.
        if (!Helpers.ApplicationSingleInstance.TryAcquire())
        {
            _isSecondInstanceExit = true;
            // No main Window to SetForegroundWindow — DesktopBoxes hosts WS_EX_TOOLWINDOW boxes
            // and a hidden tray host, so MainWindowHandle is always 0. Second instance just exits
            // silently; SwitchToCurrentInstance() is intentionally not called.
            Shutdown();
            return;
        }
#if !DEBUG
        WPFShared.Helpers.ExceptionHandler.Register(this, new WPFShared.Helpers.ExceptionHandler.Options
        {
            IsIgnorable = ex => IsIgnorableWebViewShutdownException(ex) || Win32Apis.SystemTeardown,
            LogError = (ex, msg) => Logging.Log.Error(ex, msg),
            LogFatal = (ex, msg) => Logging.Log.Fatal(ex, msg),
            LogWarning = (msg) => Logging.Log.Warning(msg),
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
        ApplyTheme(Services.GetRequiredService<ISettingsService>().UserSettings.SelectedTheme);
        ApplyBoxAppearance();
        ApplicationThemeManager.Changed += (_, _) => ApplyBoxAppearance();
        // Ensure WebWidget storage roots exist (UserWidgets + EBWebView)
        try { Services.GetRequiredService<IWebWidgetService>().EnsureUserWidgetsRoot(); } catch { }
        // Gallery previews get their own environment under the managed tree (not %TEMP%), so the
        // startup prune below covers it. Must precede any preview control init.
        try { WPFShared.Controls.WebWidgetControl.UserDataFolder = Path.Combine(SettingsService.AppDataDir, "EBWebView.Previews"); } catch { }
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

        // The splash runs initialization itself once it is first shown (see LoadingWindow),
        // so the Box windows are created under a fully-rendered WPF context.
        var loading = new LoadingWindow(Services.GetRequiredService<DesktopManager>());
        loading.Show();
    }

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
        services.AddSingleton<IWebWidgetService, WebWidgetService>();
        services.AddSingleton<INativeWidgetService, NativeWidgetService>();
        services.AddSingleton<INativeWidgetSettingsService, NativeWidgetSettingsService>();

        // Win32 watchers
        services.AddSingleton<IMouseMonitor, MouseMonitor>();
        services.AddSingleton<Core.Interfaces.IDesktopIconSizeService, Win32.Services.DesktopIconSizeService>();

        // Debug overlay (single instance; toggled from the tray "Debug Desktop Tree" menu).
        services.AddSingleton<DesktopTreeDebugOverlay>();
    }

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
                ApplicationThemeManager.ApplySystemTheme();
                break;
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

    private void BuildTrayAndMenuItems(IServiceProvider services)
    {
        // WPF-UI's NotifyIcon must live inside a visual tree, so host it in a hidden, always-on window.
        // The window is never shown visibly (Visibility=Hidden) but stays loaded for the app's lifetime.
        _trayHost = new Window
        {
            Width = 0,
            Height = 0,
            WindowState = WindowState.Minimized,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            // No AllowsTransparency: this host is never visibly shown, and a layered
            // redirect for an invisible 0x0 window is pure DWM overhead.
            Visibility = Visibility.Hidden,
        };

        ReplaceTrayIcon(services);
        _trayHost.Show();

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
        tray.LiveWallpaperToggleEnable += (_, _) =>
        {
            var lw = Services.GetRequiredService<LiveWallpaperManager>();
            lw.SetEnabled(!lw.IsEnabled);
        };
        tray.LiveWallpaperTogglePlayPause += (_, _) =>
        {
            var lw = Services.GetRequiredService<LiveWallpaperManager>();
            lw.SetPlaying(!lw.IsPlaying);
        };
        tray.LiveWallpaperChangeRequested += (_, _) =>
        {
            // Filter comes from the engine itself (WallPaperFilePicker builds it from
            // Engine.ImageExtensions + CodecSupport.VideoExtensions), so it never drifts
            // from what the renderers can actually play. No owner HWND here (tray menu),
            // so the parameterless overload passes IntPtr.Zero — never a process handle.
            var newFile = DesktopLiveWallPaperEngine.WallpaperFilePicker.PickMedia();
            if (string.IsNullOrEmpty(newFile) || !File.Exists(newFile)) return;
            Services.GetRequiredService<LiveWallpaperManager>().SetWallpaper(newFile);
        };
        tray.LiveWallpaperRemoveRequested += (_, _) => Services.GetRequiredService<LiveWallpaperManager>().Remove();
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
            ApplyTheme(theme);
            ApplyBoxAppearance();
        };
        tray.ExitRequested += (_, _) =>
        {
            Shutdown();
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
}
