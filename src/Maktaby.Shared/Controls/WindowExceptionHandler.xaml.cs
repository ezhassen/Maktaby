using System.ComponentModel;
using System.Windows;
using Maktaby.Shared.ViewModels;

namespace Maktaby.Shared.Controls
{
    /// <summary>
    /// Modern fluent error dialog (light / dark via WPF-UI). Offers <c>Continue</c> (keep app alive)
    /// and <c>Exit Application</c> (the only path that shuts down). Closing via the X button,
    /// Alt+F4 or any other system close is treated as Continue — it just closes the dialog.
    /// </summary>
    public partial class WindowExceptionHandler : Wpf.Ui.Controls.FluentWindow
    {
        public WindowExceptionHandler()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Position at top-center so expanding "Exception details" grows downward and stays visible.
            // Manual startup required because WPF has no TopCenter enum (only CenterScreen/CenterOwner/Manual).
            try
            {
                var area = SystemParameters.WorkArea;
                double w = ActualWidth > 0 ? ActualWidth : Width;
                if (double.IsNaN(w) || w <= 0) w = 680;
                // Center horizontally
                Left = area.Left + (area.Width - w) / 2;
                // Anchor near top (40px margin) so the window is already at the top when it first appears;
                // subsequent SizeToContent growth expands downward.
                Top = area.Top + 32;

                // Clamp in case the window is larger than the work area (should not happen with MaxHeight 680).
                if (Left < area.Left) Left = area.Left;
                if (Top < area.Top) Top = area.Top;
            }
            catch
            {
                // Best-effort positioning only; fall back to default WPF placement.
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is WindowExceptionHandlerViewModel oldVm)
            {
                oldVm.RequestClose -= OnViewModelRequestClose;
            }

            if (e.NewValue is WindowExceptionHandlerViewModel vm)
            {
                vm.RequestClose += OnViewModelRequestClose;
            }
        }

        private void OnViewModelRequestClose(bool shouldContinue)
        {
            Close();

            if (!shouldContinue)
            {
                Application.Current.Shutdown();
            }
        }

        private void OnContinueClick(object sender, RoutedEventArgs e)
        {
            // ViewModel route keeps state consistent; fall back to direct close if no ViewModel.
            if (DataContext is WindowExceptionHandlerViewModel vm)
            {
                vm.ContinueCommand.Execute(null);
                return;
            }

            Close();
        }

        private void OnExitAppClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is WindowExceptionHandlerViewModel vm)
            {
                vm.ExitCommand.Execute(null);
                return;
            }

            Close();
            Application.Current.Shutdown();
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            // No shutdown here. Only Exit Application explicitly shuts down (via
            // OnViewModelRequestClose(false) / OnExitAppClick). All other closes
            // (X, Alt+F4, Continue) just dismiss the dialog and keep the app alive.
        }
    }
}
