using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Shell.Services;
using DesktopBoxesUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBoxesUI.Controls;

public partial class FolderPortalControl : UserControl
{
    private BoxViewModel? Box => DataContext as BoxViewModel;
    private int _anchorIndex = -1;
    private int _focusedIndex = -1;

    public FolderPortalControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MainViewModel? Host { get; set; }
    public Action? RequestSave { get; set; }

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FolderPortalControl), new PropertyMetadata(32.0));
    public double IconSize { get => (double)GetValue(IconSizeProperty); private set => SetValue(IconSizeProperty, value); }

    public void RefreshIconSize()
    {
        int resolved = Box?.Model.IconSize
            ?? App.Services.GetRequiredService<Core.Interfaces.ISettingsService>().UserSettings.DefaultBoxIconSize
            ?? App.Services.GetRequiredService<Core.Interfaces.IDesktopIconSizeService>().Current;
        IconSize = Math.Clamp(resolved, 16, 128);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is BoxViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnBoxPropertyChanged;
        }
        BoxViewModel? newVm = e.NewValue as BoxViewModel;
        if (newVm != null)
        {
            newVm.PropertyChanged += OnBoxPropertyChanged;
        }
        RefreshIconSize();
        UpdateView();
        if (newVm != null && newVm.IsFolderPortal && newVm.HasFolder)
        {
            _ = newVm.RefreshFolderAsync();
        }
    }

    private void OnBoxPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoxViewModel.FolderPath) || e.PropertyName == nameof(BoxViewModel.CurrentFolderPath) || e.PropertyName == nameof(BoxViewModel.FolderPortalViewMode) || e.PropertyName == nameof(BoxViewModel.HasFolder) || e.PropertyName == nameof(BoxViewModel.IsAtRoot))
        {
            Dispatcher.BeginInvoke(UpdateView);
        }
        if (e.PropertyName == nameof(BoxViewModel.FolderSortBy) || e.PropertyName == nameof(BoxViewModel.FolderSortAscending))
        {
            Dispatcher.BeginInvoke(UpdateSortArrows);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
    }

    public void UpdateView()
    {
        var box = Box;
        if (box == null || !box.IsFolderPortal)
        {
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        bool hasFolder = box.HasFolder;
        PlaceholderPanel.Visibility = hasFolder ? Visibility.Collapsed : Visibility.Visible;
        ContentPanel.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;

        if (!hasFolder) return;

        bool isIcons = box.FolderPortalViewMode == Core.Models.FolderPortalViewMode.Icons;
        IconsScroll.Visibility = isIcons ? Visibility.Visible : Visibility.Collapsed;
        DetailsList.Visibility = isIcons ? Visibility.Collapsed : Visibility.Visible;
        BackButton.IsEnabled = !box.IsAtRoot;
        if (ViewToggleIcon != null)
        {
            ViewToggleIcon.Symbol = isIcons ? Wpf.Ui.Controls.SymbolRegular.AppsListDetail24 : Wpf.Ui.Controls.SymbolRegular.Grid24;
            ViewToggleButton.ToolTip = isIcons ? "Switch to Details" : "Switch to Icons";
        }
        RefreshIconSize();
        UpdateSortArrows();
    }

    private static readonly string[] OriginalHeaders = ["Name", "Size", "Type", "Date modified"];

    private void UpdateSortArrows()
    {
        if (Box == null) return;
        if (DetailsList.View is not GridView gv) return;
        string expected = Box.FolderSortBy switch
        {
            FolderSortMode.Size => "Size",
            FolderSortMode.Type => "Type",
            FolderSortMode.DateModified => "Date modified",
            _ => "Name",
        };
        string arrow = Box.FolderSortAscending ? " ▲" : " ▼";
        foreach (var col in gv.Columns)
        {
            string raw = (col.Header as string) ?? string.Empty;
            string baseHeader = raw.TrimEnd(' ', '▲', '▼', '◄', '►');
            // Fallback: if header was somehow empty, keep original
            if (string.IsNullOrWhiteSpace(baseHeader)) continue;
            // Normalize trimmed
            // OriginalHeaders contains canonical names; use that for comparison
            bool isSorted = string.Equals(baseHeader, expected, StringComparison.Ordinal);
            // If baseHeader already contains arrow-trimmed, keep it
            col.Header = baseHeader + (isSorted ? arrow : string.Empty);
        }
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select Folder for Portal" };
        var win = Window.GetWindow(this);
        bool? result = win != null ? dlg.ShowDialog(win) : dlg.ShowDialog();
        if (result == true && !string.IsNullOrWhiteSpace(dlg.FolderName) && Directory.Exists(dlg.FolderName))
        {
            if (Box != null)
            {
                Box.SetFolderPath(dlg.FolderName);
                RequestSave?.Invoke();
                UpdateView();
                _ = Box.RefreshFolderAsync();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            }
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Box == null) return;
        Box.NavigateUp();
        RequestSave?.Invoke();
        UpdateView();
        if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
    }

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        if (Box == null) return;
        Box.FolderPortalViewMode = Box.FolderPortalViewMode == Core.Models.FolderPortalViewMode.Icons
            ? Core.Models.FolderPortalViewMode.Details
            : Core.Models.FolderPortalViewMode.Icons;
        RequestSave?.Invoke();
        UpdateView();
    }

    private void DetailsHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column == null) return;
        string raw = (header.Column.Header as string) ?? string.Empty;
        string baseHeader = raw.TrimEnd(' ', '▲', '▼', '◄', '►');
        FolderSortMode newMode = baseHeader switch
        {
            "Size" => FolderSortMode.Size,
            "Type" => FolderSortMode.Type,
            "Date modified" => FolderSortMode.DateModified,
            _ => FolderSortMode.Name,
        };
        if (Box == null) return;
        if (Box.FolderSortBy == newMode) Box.FolderSortAscending = !Box.FolderSortAscending;
        else { Box.FolderSortBy = newMode; Box.FolderSortAscending = true; }
        Box.ApplyFolderSort();
        UpdateSortArrows();
        RequestSave?.Invoke();
    }

    // ---- Selection helpers ----

    private void ClearSelection()
    {
        if (Box == null) return;
        foreach (var it in Box.FolderItems) it.IsSelected = false;
        _anchorIndex = -1;
    }

    private void SelectOnly(FolderItemViewModel vm)
    {
        ClearSelection();
        vm.IsSelected = true;
        _anchorIndex = Box?.FolderItems.IndexOf(vm) ?? -1;
    }

    private void SelectRange(int anchor, int target, bool additive)
    {
        if (Box == null) return;
        int lo = Math.Min(anchor, target), hi = Math.Max(anchor, target);
        if (!additive) foreach (var it in Box.FolderItems) it.IsSelected = false;
        for (int i = lo; i <= hi; i++)
            if (i >= 0 && i < Box.FolderItems.Count) Box.FolderItems[i].IsSelected = true;
    }

    // ---- Icons view handlers ----

    private void ItemBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: FolderItemViewModel vm } border) return;
        border.Focus();
        if (vm.IsEditing) { e.Handled = true; return; }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (shift && _anchorIndex >= 0 && Box != null)
            SelectRange(_anchorIndex, Box.FolderItems.IndexOf(vm), false);
        else if (ctrl)
        {
            vm.IsSelected = !vm.IsSelected;
            _anchorIndex = Box?.FolderItems.IndexOf(vm) ?? -1;
        }
        else
        {
            if (!vm.IsSelected) SelectOnly(vm);
            else _anchorIndex = Box?.FolderItems.IndexOf(vm) ?? -1;
        }

        if (e.ClickCount == 2)
        {
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            if (alt)
            {
                ShowProperties(vm);
                e.Handled = true;
                return;
            }
            if (vm.IsDirectory)
            {
                Box?.TryNavigateInto(vm);
                RequestSave?.Invoke();
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            }
            else
            {
                OpenItem(vm);
            }
            e.Handled = true;
            return;
        }
        e.Handled = true;
    }

    private void ItemBorder_MouseMove(object sender, MouseEventArgs e) { }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Box?.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private void ItemBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }

    private void ItemBorder_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: FolderItemViewModel vm } && Box != null)
            _focusedIndex = Box.FolderItems.IndexOf(vm);
    }

    private void ItemBorder_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm }) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        if (key == Key.F2)
        {
            StartRename(vm, sender as Border);
            e.Handled = true;
        }
        else if (key == Key.Delete)
        {
            bool permanent = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            DeleteSelected(vm, permanent);
            e.Handled = true;
        }
        else if (key == Key.Enter)
        {
            if (alt) ShowProperties(vm);
            else if (vm.IsDirectory)
            {
                Box?.TryNavigateInto(vm);
                RequestSave?.Invoke();
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw2) bcw2.ManageFolderWatcher();
            }
            else OpenItem(vm);
            e.Handled = true;
        }
        else if (key == Key.Back)
        {
            Box?.NavigateUp();
            RequestSave?.Invoke();
            UpdateView();
            if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            e.Handled = true;
        }
    }

    private void DetailsItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: FolderItemViewModel vm })
        {
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            if (alt)
            {
                ShowProperties(vm);
                return;
            }
            if (vm.IsDirectory)
            {
                Box?.TryNavigateInto(vm);
                RequestSave?.Invoke();
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            }
            else OpenItem(vm);
        }
    }

    private void DeleteSelected(FolderItemViewModel clicked, bool permanent)
    {
        if (Box == null) return;
        var selected = Box.FolderItems.Where(i => i.IsSelected).ToList();
        if (selected.Count > 0)
        {
            foreach (var it in selected.ToList())
                _ = Box.DeleteFolderItemAsync(it, permanent);
        }
        else
        {
            _ = Box.DeleteFolderItemAsync(clicked, permanent);
        }
    }

    private void StartRename(FolderItemViewModel vm, Border? border)
    {
        vm.RenameText = vm.DisplayName;
        vm.IsEditing = true;
        if (border != null)
        {
            border.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (border.FindName("RenameBox") is TextBox tb) { tb.Focus(); tb.SelectAll(); }
                // For Details view, the TextBox is not inside Border; find via visual tree
                var tb2 = FindVisualChild<TextBox>(border);
                if (tb2 != null) { tb2.Focus(); tb2.SelectAll(); }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox { DataContext: FolderItemViewModel vm } tb)
        {
            if (e.Key == Key.Enter)
            {
                _ = CommitRename(vm, GetItemBorderFromTextBox(tb));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.IsEditing = false;
                e.Handled = true;
            }
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FolderItemViewModel vm } && vm.IsEditing)
            _ = CommitRename(vm, null);
    }

    private async Task CommitRename(FolderItemViewModel vm, Border? focusTarget)
    {
        string newName = vm.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(newName)) { vm.IsEditing = false; return; }
        string currentBase = System.IO.Path.GetFileNameWithoutExtension(vm.Path);
        if (newName.Equals(currentBase, StringComparison.OrdinalIgnoreCase)) { vm.IsEditing = false; return; }
        string ext = System.IO.Path.GetExtension(vm.Path);
        if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(newName)) newName += ext;
        bool ok = await (Box?.RenameFolderItemAsync(vm, newName) ?? Task.FromResult(false));
        if (ok) vm.IsEditing = false;
        if (focusTarget != null) focusTarget.Focus();
    }

    private static Border? GetItemBorderFromTextBox(TextBox tb)
    {
        DependencyObject? cur = tb;
        while (cur != null)
        {
            if (cur is Border { Name: "ItemBorder" } b) return b;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? obj) where T : DependencyObject
    {
        if (obj == null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is T t) return t;
            var inner = FindVisualChild<T>(child);
            if (inner != null) return inner;
        }
        return null;
    }

    private static void OpenItem(FolderItemViewModel item)
    {
        try
        {
            var path = item.Path;
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private void ShowProperties(FolderItemViewModel vm)
    {
        var window = Window.GetWindow(this);
        IntPtr hwnd = window != null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
        Win32.NativeMethods.Win32Apis.ShowProperties(hwnd, vm.Path, null);
    }

    private void ItemBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FolderItemViewModel vm) return;
        var window = Window.GetWindow(this);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        ShellContextMenu.ShowForPath(hwnd, vm.Path);
        e.Handled = true;
    }

    private void ItemBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void Control_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Box == null) return;
        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (Box.GoBack())
            {
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            if (Box.GoForward())
            {
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
    }

    private void ItemBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm }) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            OpenItemLocation(vm);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton1)
        {
            if (Box?.GoBack() == true)
            {
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            if (Box?.GoForward() == true)
            {
                UpdateView();
                if (Window.GetWindow(this) is Views.BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
    }

    private static void OpenItemLocation(FolderItemViewModel vm)
    {
        try
        {
            string path = vm.Path;
            if (string.IsNullOrWhiteSpace(path)) return;
            // Explorer /select opens parent and selects the item
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}
