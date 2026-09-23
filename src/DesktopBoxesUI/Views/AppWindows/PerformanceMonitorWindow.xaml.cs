using DesktopBoxesUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Views
{
    public partial class PerformanceMonitorWindow : FluentWindow
    {
        private readonly PerformanceMonitorViewModel _vm;

        public PerformanceMonitorWindow()
        {
            InitializeComponent();
            _vm = new PerformanceMonitorViewModel();
            DataContext = _vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            Closed += (_, _) =>
            {
                try { _vm.PropertyChanged -= OnViewModelPropertyChanged; } catch { }
                _vm.Dispose();
            };
        }

        // Grids are replaced (not overlaid) by the Disabled placeholders: local Visibility
        // values here, so the grids keep their implicit theme styling (a local Style would
        // replace it — WPF-UI defines no keyed DataGrid style to base on).
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            try
            {
                if (e.PropertyName == nameof(PerformanceMonitorViewModel.IsAppDisabled))
                {
                    WindowsGrid.Visibility = _vm.IsAppDisabled ? Visibility.Collapsed : Visibility.Visible;
                }
                else if (e.PropertyName == nameof(PerformanceMonitorViewModel.IsLiveWallpaperDisabled))
                {
                    LiveWallpaperGrid.Visibility = _vm.IsLiveWallpaperDisabled ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch { }
        }

        private void WindowsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var row = FindVisualParent<System.Windows.Controls.DataGridRow>(src);
                if (row != null && !row.IsSelected)
                {
                    row.IsSelected = true;
                    // Ensure DataGrid's SelectedItem is updated
                    var grid = (System.Windows.Controls.DataGrid)sender;
                    grid.SelectedItem = row.Item;
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? obj) where T : DependencyObject
        {
            while (obj != null)
            {
                if (obj is T t) return t;
                obj = System.Windows.Media.VisualTreeHelper.GetParent(obj);
            }
            return null;
        }

        private void HideSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = WindowsGrid.SelectedItems.Cast<ViewModels.PerformanceItem>().ToList();
            if (selected.Count == 0 && WindowsGrid.SelectedItem is ViewModels.PerformanceItem single) selected.Add(single);
            foreach (var item in selected)
            {
                try { item.WindowRef?.Hide(); } catch { }
            }
            _vm.Refresh();
        }

        private void ShowSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = WindowsGrid.SelectedItems.Cast<ViewModels.PerformanceItem>().ToList();
            if (selected.Count == 0 && WindowsGrid.SelectedItem is ViewModels.PerformanceItem single) selected.Add(single);
            foreach (var item in selected)
            {
                try
                {
                    if (item.WindowRef != null)
                    {
                        if (item.WindowRef.WindowState == WindowState.Minimized) item.WindowRef.WindowState = WindowState.Normal;
                        item.WindowRef.Show();
                        item.WindowRef.Activate();
                    }
                }
                catch { }
            }
            _vm.Refresh();
        }

        private void SuspendSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = WindowsGrid.SelectedItems.Cast<ViewModels.PerformanceItem>().ToList();
            if (selected.Count == 0 && WindowsGrid.SelectedItem is ViewModels.PerformanceItem single) selected.Add(single);
            foreach (var item in selected)
            {
                try
                {
                    if (item.WindowRef is Views.Containers.WebWidgetWindow cww)
                    {
                        var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(cww) is Controls.ContainersControls.WebWidgetControl ctrl) ctrl.Suspend();
                    }
                    else if (item.WindowRef is Views.Containers.NativeWidgetWindow nww)
                    {
                        try { nww.Widget?.Suspend(); } catch { }
                    }
                }
                catch { }
            }
            _vm.Refresh();
        }

        private void ResumeSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = WindowsGrid.SelectedItems.Cast<ViewModels.PerformanceItem>().ToList();
            if (selected.Count == 0 && WindowsGrid.SelectedItem is ViewModels.PerformanceItem single) selected.Add(single);
            foreach (var item in selected)
            {
                try
                {
                    if (item.WindowRef is Views.Containers.WebWidgetWindow cww)
                    {
                        var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(cww) is Controls.ContainersControls.WebWidgetControl ctrl) ctrl.Resume();
                    }
                    else if (item.WindowRef is Views.Containers.NativeWidgetWindow nww)
                    {
                        try { nww.Widget?.Resume(); } catch { }
                    }
                }
                catch { }
            }
            _vm.Refresh();
        }

        // Header click toggles the same flag the chevron ToggleButton binds to. Clicks on the
        // ToggleButton itself never reach here: ButtonBase marks MouseLeftButtonUp handled when
        // it raises Click, so there is no double-toggle.
        private void WebView2Header_Toggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { _vm.IsWebView2Expanded = !_vm.IsWebView2Expanded; } catch { }
        }

                private void WallpaperSuspend_Click(object sender, RoutedEventArgs e)
        {
            try { App.Services?.GetService<LiveWallpaperManager>()?.SetPlaying(false); } catch { }
            _vm.Refresh();
        }

        private void WallpaperResume_Click(object sender, RoutedEventArgs e)
        {
            try { App.Services?.GetService<LiveWallpaperManager>()?.SetPlaying(true); } catch { }
            _vm.Refresh();
        }

        private void SuspendIdle_Click(object sender, RoutedEventArgs e)        {
            foreach (var win in Application.Current.Windows.OfType<Views.Containers.WebWidgetWindow>())
            {
                try
                {
                    var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(win) is Controls.ContainersControls.WebWidgetControl ctrl) ctrl.Suspend();
                }
                catch { }
            }
            foreach (var win in Application.Current.Windows.OfType<Views.Containers.NativeWidgetWindow>())
            {
                try { win.Widget?.Suspend(); } catch { }
            }
            _vm.Refresh();
        }

        private void ResumeAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var win in Application.Current.Windows.OfType<Views.Containers.WebWidgetWindow>())
            {
                try
                {
                    var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(win) is Controls.ContainersControls.WebWidgetControl ctrl) ctrl.Resume();
                }
                catch { }
            }
            foreach (var win in Application.Current.Windows.OfType<Views.Containers.NativeWidgetWindow>())
            {
                try { win.Widget?.Resume(); } catch { }
            }
            _vm.Refresh();
        }

        protected override void OnClosed(EventArgs e)
        {
            _vm.Dispose();
            base.OnClosed(e);
        }
    }
}
