using Maktaby.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace Maktaby.Views;

/// <summary>
/// Non-blocking window offering an available update. Shows the installed and available versions
/// side by side, and offers exactly three actions.
/// </summary>
/// <remarks>
/// Deliberately its own <c>AppFluentWindow</c> rather than a <c>ContentDialog</c>: the app lives
/// in the tray and has no main window, and a modal dialog would steal focus from whatever the
/// user is doing on a desktop-organisation app. This is shown modeless and can be dismissed
/// with the X button, which is treated as "Update later".
/// </remarks>
public partial class UpdatePromptView : AppWindows.AppFluentWindow
{
    public UpdatePromptView()
    {
        InitializeComponent();
    }

    private UpdatePromptViewModel? Vm => DataContext as UpdatePromptViewModel;

    /// <summary>
    /// Blocks closing while an update is in flight.
    /// </summary>
    /// <remarks>
    /// Closing resolves the prompt as "Update later", so during a download that would abandon
    /// the transfer behind a progress bar nobody can see, and during the install it would
    /// leave the app without the window that owns its progress. The X button stays enabled
    /// visually but does nothing, which is honest: the operation cannot be interrupted. Use
    /// the Cancel button beside the bar to abandon a download.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Vm is { IsBusy: true })
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void Install_Click(object sender, RoutedEventArgs e) =>
        Vm?.Choose(ViewModels.UpdatePromptChoice.Install);

    private void Later_Click(object sender, RoutedEventArgs e) =>
        Vm?.Choose(ViewModels.UpdatePromptChoice.Later);

    private void Skip_Click(object sender, RoutedEventArgs e) =>
        Vm?.Choose(ViewModels.UpdatePromptChoice.Skip);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Vm?.Cancel();

    private void Retry_Click(object sender, RoutedEventArgs e) => Vm?.Retry();
}
