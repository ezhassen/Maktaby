using DesktopBoxesUI.ViewModels;
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
            Closed += (_, _) => _vm.Dispose();
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
                    if (item.WindowRef is Views.Containers.CssWidgetWindow cww)
                    {
                        var field = typeof(Views.Containers.CssWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(cww) is Controls.ContainersControls.CssWidgetControl ctrl) ctrl.Suspend();
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
                    if (item.WindowRef is Views.Containers.CssWidgetWindow cww)
                    {
                        var field = typeof(Views.Containers.CssWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(cww) is Controls.ContainersControls.CssWidgetControl ctrl) ctrl.Resume();
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

        private void SuspendIdle_Click(object sender, RoutedEventArgs e)
        {
            foreach (var win in Application.Current.Windows.OfType<Views.Containers.CssWidgetWindow>())
            {
                try
                {
                    var field = typeof(Views.Containers.CssWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(win) is Controls.ContainersControls.CssWidgetControl ctrl) ctrl.Suspend();
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
            foreach (var win in Application.Current.Windows.OfType<Views.Containers.CssWidgetWindow>())
            {
                try
                {
                    var field = typeof(Views.Containers.CssWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(win) is Controls.ContainersControls.CssWidgetControl ctrl) ctrl.Resume();
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
