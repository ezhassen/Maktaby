using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.Settings;
using DesktopBoxesUI.Shell.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using System.Runtime.Versioning;
using System.Windows;
using Wpf.Ui.Appearance;

namespace DesktopBoxesUI;

/// <summary>
/// Application entry point. Builds the composition root (dependency injection) so that all
/// platform-specific services are injected behind Core interfaces, then starts the desktop
/// Boxes via <see cref="DesktopManager"/> and lives in the system tray (see <see cref="TrayIconService"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        //
        AppJSettings.Reload();
        //

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
        BuildTrayAndMenuItems(Services);

        Services.GetRequiredService<ISettingsService>().Load();
        ApplyTheme(Services.GetRequiredService<ISettingsService>().UserSettings.SelectedTheme);
        ApplyBoxAppearance();
        ApplicationThemeManager.Changed += (_, _) => ApplyBoxAppearance();
        Services.GetRequiredService<IMouseMonitor>().Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // The splash runs initialization itself once it is first shown (see LoadingWindow),
        // so the Box windows are created under a fully-rendered WPF context.
        var loading = new LoadingWindow(Services.GetRequiredService<DesktopManager>());
        loading.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var mang = Services.GetRequiredService<DesktopManager>();
        //RestoreIcons first.
        mang.RestoreIcons();
        Services.GetRequiredService<IMouseMonitor>().Stop();
        //async is not ok in app exit
        //await mang.SaveAsync();
        mang.SaveSync();
        //
        Logging.DisposeAllDefaultLoggers();
        base.OnExit(e);
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

        // Win32 platform services
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IDpiService, DpiService>();
        services.AddSingleton<IWindowSnappingService, WindowSnappingService>();
        services.AddSingleton<IWindowPositioningService, WindowPositioningService>();
        services.AddSingleton<IZOrderService, ZOrderService>();
        services.AddSingleton<IDesktopWindowService, DesktopWindowService>();
        services.AddSingleton<IExplorerDesktopService, ExplorerDesktopService>();
        services.AddSingleton<IShellWatcherService, ShellDesktopWatcher>();
        services.AddSingleton<IFileWatcherService, DesktopFileWatcher>();
        services.AddSingleton<IDispatcher, WpfDispatcher>();
        services.AddSingleton<DesktopManager, DesktopManager>((serv) => new DesktopManager(serv));

        // Shell platform services
        services.AddSingleton<IShellItemService, ShellItemService>();
        services.AddSingleton<IShellIconService, ShellIconService>();
        services.AddSingleton<IDesktopService, DesktopService>();

        // UI services and view models
        services.AddSingleton<IconImageService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Win32 watchers
        services.AddSingleton<IMouseMonitor, MouseMonitor>();
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
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case "dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
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
        TrayIconService _tray = new();
        _tray.NewBoxRequested += (_, _) => Services.GetRequiredService<DesktopManager>().NewBox();
        _tray.ResetRequested += async (_, _) =>
        {
            // try
            // {
            await Services.GetRequiredService<DesktopManager>().ResetAsync();
            // }
            // catch
            // {
            // }
        };
        _tray.SettingsRequested += (_, _) =>
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
        _tray.ThemeRequested += (_, theme) =>
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            settings.UserSettings.SelectedTheme = theme;
            settings.Save();
            ApplyTheme(theme);
            ApplyBoxAppearance();
        };
        _tray.ExitRequested += (_, _) =>
        {
            _tray?.Dispose();
            Shutdown();
        };
    }
}
