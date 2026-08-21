using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.Shell.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;
using System.Windows;

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

    private TrayIconService? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        Services.GetRequiredService<ISettingsService>().Load();
        Services.GetRequiredService<IMouseMonitor>().Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _tray = new TrayIconService();
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
        _tray.ExitRequested += (_, _) =>
        {
            _tray?.Dispose();
            Shutdown();
        };

        Exit += async (_, _) =>
        {
            Services.GetRequiredService<IMouseMonitor>().Stop();
            var mang = Services.GetRequiredService<DesktopManager>();
            await mang.SaveAsync();
            mang.RestoreIcons();
        };
        // The splash runs initialization itself once it is first shown (see LoadingWindow),
        // so the Box windows are created under a fully-rendered WPF context.
        var loading = new LoadingWindow(Services.GetRequiredService<DesktopManager>());
        loading.Show();
    }

    [SupportedOSPlatform("windows10.0.14393")]
    private static void ConfigureServices(IServiceCollection services)
    {
        // Core (pure .NET, no platform dependencies)
        services.AddSingleton<IBoxService, BoxService>();
        services.AddSingleton<IContainerService, ContainerService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IPersistenceService, JsonSnapshotPersistenceService>();

        // Win32 platform services
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IDpiService, DpiService>();
        services.AddSingleton<IWindowSnappingService, WindowSnappingService>();
        services.AddSingleton<IWindowPositioningService, WindowPositioningService>();
        services.AddSingleton<IZOrderService, ZOrderService>();
        services.AddSingleton<IDesktopWindowService, DesktopWindowService>();
        services.AddSingleton<IExplorerDesktopService, ExplorerDesktopService>();
        services.AddSingleton<DesktopManager, DesktopManager>((serv) => new DesktopManager(serv));

        // Shell platform services
        services.AddSingleton<IShellItemService, ShellItemService>();
        services.AddSingleton<IShellIconService, ShellIconService>();
        services.AddSingleton<IDesktopService, DesktopService>();

        // UI services and view models
        services.AddSingleton<IconImageService>();
        services.AddSingleton<MainViewModel>();

        // Win32 watchers
        services.AddSingleton<IMouseMonitor, MouseMonitor>();
    }
}
