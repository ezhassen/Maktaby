using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Transient splash shown at startup. Once it is first shown it kicks off
/// <see cref="DesktopManager.InitializeAsync"/> (after a short delay so the spinner actually paints)
/// and closes itself when initialization completes, revealing the Box windows underneath.
/// </summary>
public partial class LoadingWindow : Window
{
    private readonly DesktopManager _manager;

    public LoadingWindow(DesktopManager manager)
    {
        _manager = manager;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        //fire and forget
        Init();
    }

    private async void Init()
    {
        // Let the splash paint and the dispatcher settle before the Shell enumeration / Box
        // creation work begins.
        await Task.Delay(800);
        try
        {
            await _manager.InitializeAsync();

            // Standalone live wallpaper (own windows, own lifecycle — independent from DesktopManager).
            try { App.Services.GetRequiredService<LiveWallpaperManager>().Initialize(); } catch { }
        }
        finally
        {
            Close();
        }
    }
}
