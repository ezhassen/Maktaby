using DesktopBoxesUI.ViewModels;
using System.Windows;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Modal-less settings editor opened from the tray menu. Edits are applied/saved only when
/// "Save" is clicked; "Cancel" discards. Hosted in a <see cref="FluentWindow"/> so it follows
/// the WPF-UI application theme.
/// </summary>
public partial class SettingsView : FluentWindow
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Keep the preview in sync with every edit (the VM raises PropertyChanged per field).
        vm.PropertyChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var palette = BoxAppearance.Resolve(vm.ToUserSettings());

        PreviewBox.Background = palette.Back;
        PreviewBox.BorderBrush = palette.Border;
        PreviewBox.BorderThickness = palette.Thickness;
        PreviewHeader.Background = palette.HeaderBack;
        PreviewTitle.Foreground = palette.HeaderFore;
        PreviewItem1.Foreground = palette.Fore;
        PreviewItem2.Foreground = palette.Fore;
        PreviewItem3.Foreground = palette.Fore;
        PreviewIcon1.Fill = palette.Fore;
        PreviewIcon2.Fill = palette.Fore;
        PreviewIcon3.Fill = palette.Fore;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.Save();
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
