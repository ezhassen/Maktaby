using Maktaby.ViewModels;
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

    private void Install_Click(object sender, RoutedEventArgs e) =>
        Vm?.Choose(ViewModels.UpdatePromptChoice.Install);

    private void Later_Click(object sender, RoutedEventArgs e) =>
        Vm?.Choose(ViewModels.UpdatePromptChoice.Later);

    private void Skip_Click(object sender, RoutedEventArgs e) =>
        Vm?.Choose(ViewModels.UpdatePromptChoice.Skip);
}
