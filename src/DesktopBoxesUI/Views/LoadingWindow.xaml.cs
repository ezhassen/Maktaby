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
        // Let the splash paint and the dispatcher settle before the Shell enumeration / Box
        // creation work begins.
        await Task.Delay(800);
        try
        {
            await _manager.InitializeAsync();
        }
        finally
        {
            Close();
        }
    }
}
