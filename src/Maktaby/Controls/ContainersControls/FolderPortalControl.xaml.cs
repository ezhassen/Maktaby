using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Helpers;
using Maktaby.Localization;
using Maktaby.Shell.Services;
using Maktaby.ViewModels;
using Maktaby.Views.Containers;
using Maktaby.Views.HelpersViews;
using Maktaby.Win32.NativeMethods;
using Maktaby.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Maktaby.Controls.ContainersControls;

public partial class FolderPortalControl : WidgetControlBase
{
    private BoxViewModel? Box => DataContext as BoxViewModel;
    private int _anchorIndex = -1;
    private int _focusedIndex = -1;
    private readonly ItemRenameService _renameService = new();

    public FolderPortalControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Loaded/Unloaded/visibility lifecycle comes from WidgetControlBase.
        IsKeyboardFocusWithinChanged += Control_IsKeyboardFocusWithinChanged;
    }

    public MainViewModel? Host { get; set; }
    public Action? RequestSave { get; set; }

    // Copying indicator, mirroring BoxControl: a paste into a folder can move real files, and a
    // cross-volume copy is slow enough to need feedback. Dependency properties rather than plain
    // CLR properties because the pill binds to the control as a RelativeSource ancestor, which
    // only updates on a change notification.
    public static readonly DependencyProperty IsCopyingFilesProperty = DependencyProperty.Register(
        nameof(IsCopyingFiles), typeof(bool), typeof(FolderPortalControl), new PropertyMetadata(false));

    public bool IsCopyingFiles
    {
        get => (bool)GetValue(IsCopyingFilesProperty);
        private set => SetValue(IsCopyingFilesProperty, value);
    }

    public static readonly DependencyProperty CopyStatusTextProperty = DependencyProperty.Register(
        nameof(CopyStatusText), typeof(string), typeof(FolderPortalControl), new PropertyMetadata(string.Empty));

    public string CopyStatusText
    {
        get => (string)GetValue(CopyStatusTextProperty);
        private set => SetValue(CopyStatusTextProperty, value);
    }

    // Generation guard for the pill: Progress<T> posts asynchronously, so a late callback from a
    // finished operation would otherwise leave the indicator stuck on. Shared with the drop path
    // for the same reason BoxControl shares one counter.
    private int _copyingSeq;

    // Type-ahead state: the character being repeated and when the run started. The cycle position is
    // derived from the selection each time, so it cannot drift onto a non-matching item.
    private char _typeAheadChar;
    private DateTime _typeAheadAt;

    /// <summary>Gap after which a keystroke starts a new type-ahead search instead of extending one.</summary>
    private const int TypeAheadResetMs = 800;

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FolderPortalControl), new PropertyMetadata(32.0));
    public double IconSize { get => (double)GetValue(IconSizeProperty); private set => SetValue(IconSizeProperty, value); }

    /// <summary>
    /// The icon size the Details view uses. Fixed: a list row is a text row, and a large icon in it
    /// is decoration that also costs a bigger bitmap per file.
    /// </summary>
    private const int _detailsViewIconSize = BoxIconSize.DetailsViewIconSize;

    /// <summary>
    /// The icon size in effect: the fixed one in the Details view, the user's size in the Icons
    /// view.
    /// </summary>
    /// <remarks>
    /// This is the *display* size — the Details view draws its rows at 32 whatever the user picked.
    /// The user's size is resolved on demand rather than cached, so switching back to the Icons view
    /// picks up whatever it is now — including a change made while the Details view was up, which is
    /// ignored there on purpose.
    /// </remarks>
    private int EffectiveIconSize =>
        Box is { } box
            ? Helpers.BoxIconSize.ResolveForView(box.Model, box.FolderPortalViewMode)
            : _detailsViewIconSize;

    public void RefreshIconSize()
    {
        int resolved = EffectiveIconSize;

        IconSize = Math.Clamp(resolved, 16, 128);
        PushIconSizeToRows();
    }

    /// <summary>
    /// Pushes the size to the rows so their icons are resolved sharp enough for it.
    /// </summary>
    /// <remarks>
    /// The rows are created when the folder loads, which can be after the size was set, so this runs
    /// both on a size change and whenever the folder contents are replaced.
    /// <para>
    /// In the Details view the rows are left alone on purpose. The view already draws them at
    /// <see cref="_detailsViewIconSize"/> (the control's IconSize), and a bitmap that is larger than
    /// that is simply scaled down - sharp, and no re-resolve. Re-resolving every row of a large
    /// folder just to make them smaller would blank the list and then re-fetch every icon.
    /// </para>
    /// </remarks>
    private void PushIconSizeToRows()
    {
        if (Box is not { } box)
        {
            return;
        }

        if (box.FolderPortalViewMode == Core.Models.FolderPortalViewMode.Details)
        {
            return;
        }

        int size = Helpers.BoxIconSize.Resolve(box.Model);//EffectiveIconSize
        foreach (var row in box.FolderItems)
        {
            row.IconSize = size;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is BoxViewModel oldVm)
        {
            SaveFolderPortalColumnLayout(oldVm);
            oldVm.PropertyChanged -= OnBoxPropertyChanged;
            oldVm.FolderItems.CollectionChanged -= OnFolderItemsChanged;
        }
        BoxViewModel? newVm = e.NewValue as BoxViewModel;
        if (newVm != null)
        {
            newVm.PropertyChanged += OnBoxPropertyChanged;
            // The rows are created when the folder loads, which can be after the icon size was
            // resolved - so new rows are told the size as they arrive.
            newVm.FolderItems.CollectionChanged += OnFolderItemsChanged;
        }
        RefreshIconSize();
        UpdateView();
        CaptureDefaultFolderPortalColumnLayout();
        ApplyFolderPortalColumnLayout();
        if (newVm != null && newVm.IsFolderPortal && newVm.HasFolder)
        {
            _ = newVm.EnsureFolderLoadedAsync();
        }
    }

    private void OnFolderItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            PushIconSizeToRows();
            return;
        }

        if (Box is null || e.NewItems is null)
        {
            return;
        }

        int size = EffectiveIconSize;
        foreach (var row in e.NewItems)
        {
            if (row is ViewModels.FolderItemViewModel item)
            {
                item.IconSize = size;
            }
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

    private bool _detailsHandlersHooked;

    protected override void InitializeCtrlsCore()
    {
        // One-time work: hook the handled-events-too handlers once, otherwise every
        // reload would stack another duplicate invocation.
        if (_detailsHandlersHooked) return;
        _detailsHandlersHooked = true;
        // DataGridRow events are often marked handled by DataGrid's internal selection logic.
        // Register with handledEventsToo so dragging / double-click / right-click / middle-click still fire.
        DetailsGrid.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(DetailsGrid_OnPreviewMouseDown), true);
        DetailsGrid.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(DetailsGrid_OnMouseMove), true);
        DetailsGrid.AddHandler(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(DetailsGrid_OnDoubleClick), true);
        DetailsGrid.AddHandler(UIElement.MouseRightButtonUpEvent, new MouseButtonEventHandler(DetailsGrid_OnRightButtonUp), true);
        DetailsGrid.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(DetailsGrid_OnRightButtonUp), true);
        DetailsGrid.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(DetailsGrid_OnLeftButtonUp), true);
        DetailsGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(DetailsGrid_OnKeyDown), true);
        // ENTER in Details must be intercepted on Preview (tunnel) before DataGrid's class handler moves CurrentCell/Selection to next row
        this.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(DetailsGrid_OnKeyDown), true);
        DetailsGrid.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(DetailsGrid_OnKeyDown), true);
        DetailsGrid.AddHandler(UIElement.GotFocusEvent, new RoutedEventHandler(DetailsGrid_OnGotFocus), true);
        // WPF keyboard focus uses a separate routed event (Keyboard.GotKeyboardFocusEvent /
        // UIElement.GotKeyboardFocusEvent). DataGrid cells/rows typically raise keyboard focus
        // without raising logical GotFocus, so the handler above never fires. Listen for both.
        DetailsGrid.AddHandler(UIElement.GotKeyboardFocusEvent, new RoutedEventHandler(DetailsGrid_OnGotFocus), true);
        DetailsGrid.AddHandler(Keyboard.GotKeyboardFocusEvent, new RoutedEventHandler(DetailsGrid_OnGotFocus), true);
        DetailsGrid.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(DetailsGrid_OnContextMenuOpening), true);
        DetailsGrid.ColumnReordered += (_, _) => SaveFolderPortalColumnLayout();
        DetailsGrid.PreviewMouseLeftButtonUp += (_, _) => SaveFolderPortalColumnLayout();
    }

    protected override void OnReloadedCore()
    {
        UpdateView();
        // Ensure resizing cannot be blocked by style/asset defaults - force at runtime
        DetailsGrid.CanUserResizeColumns = true;
        foreach (var col in DetailsGrid.Columns)
            col.CanUserResize = true;
        CaptureDefaultFolderPortalColumnLayout();
        ApplyFolderPortalColumnLayout();
        // The folder VM outlives this control (tab switches re-parent it): (re)attach
        // idempotently — Loaded refires on hide/show without a DataContext change.
        if (Box is BoxViewModel loadedVm)
        {
            loadedVm.PropertyChanged -= OnBoxPropertyChanged;
            loadedVm.PropertyChanged += OnBoxPropertyChanged;
        }
    }

    private string? _defaultColumnLayout;

    private void CaptureDefaultFolderPortalColumnLayout()
    {
        if (_defaultColumnLayout != null) return;
        if (DetailsGrid.Columns.Count == 0) return;
        var ordered = DetailsGrid.Columns.OrderBy(c => c.DisplayIndex).ToList();
        _defaultColumnLayout = string.Join("|", ordered.Select(c =>
        {
            int origIndex = DetailsGrid.Columns.IndexOf(c);
            double w = c.ActualWidth > 0 ? c.ActualWidth : (c.Width.UnitType == DataGridLengthUnitType.Pixel ? c.Width.Value : c.MinWidth);
            return $"{origIndex}:{w.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }));
    }

    private void ApplyFolderPortalColumnLayout()
    {
        if (Box is not BoxViewModel vm) return;
        var layout = vm.FolderPortalColumns;
        if (string.IsNullOrWhiteSpace(layout))
        {
            layout = _defaultColumnLayout;
        }
        if (string.IsNullOrWhiteSpace(layout)) return;
        var columns = DetailsGrid.Columns.ToList();
        int displayIndex = 0;
        foreach (var part in layout.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2) continue;
            if (!int.TryParse(kv[0], out var origIndex)) continue;
            if (origIndex < 0 || origIndex >= columns.Count) continue;
            if (!double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)) continue;
            var col = columns[origIndex];
            col.Width = new DataGridLength(width);
            col.DisplayIndex = displayIndex++;
        }
        foreach (var col in columns)
        {
            if (col.DisplayIndex < displayIndex)
            {
                continue;
            }
            col.DisplayIndex = displayIndex++;
        }
    }

    private void SaveFolderPortalColumnLayout(BoxViewModel? vm = null)
    {
        vm ??= Box;
        if (vm is not BoxViewModel target) return;
        vm = target;
        if (DetailsGrid.Columns.Count == 0) return;
        var ordered = DetailsGrid.Columns.OrderBy(c => c.DisplayIndex).ToList();
        var parts = ordered.Select(c =>
        {
            int origIndex = DetailsGrid.Columns.IndexOf(c);
            double w = c.ActualWidth > 0 ? c.ActualWidth : (c.Width.UnitType == DataGridLengthUnitType.Pixel ? c.Width.Value : c.MinWidth);
            return $"{origIndex}:{w.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        });
        vm.FolderPortalColumns = string.Join("|", parts);
    }

    protected override void OnUnloadedCore()
    {
        if (_renameService.IsEditing) _renameService.Dismiss();
        // Detach from the long-lived folder VM so it can't pin this unloaded control
        // (re-attached in OnReloadedCore / OnDataContextChanged).
        if (Box is BoxViewModel vm)
        {
            SaveFolderPortalColumnLayout();
            vm.PropertyChanged -= OnBoxPropertyChanged;
        }
    }

    public override bool IsSuspended { get; protected set; }

    protected override bool SuspendCore()
    {
        // Rename dismissal also runs in OnUnloadedCore on Hide; repeated guarded here
        // since visibility can flip without an unload seam (order-independent).
        try { if (_renameService.IsEditing) _renameService.Dismiss(); } catch { }
        return true;
    }

    protected override bool ResumeCore()
    {
        // Reconcile what changed while suspended; the VM no-ops when already loaded.
        try { UpdateView(); } catch { }
        try
        {
            var box = Box;
            if (box != null && box.IsFolderPortal) _ = box.EnsureFolderLoadedAsync();
        }
        catch { }
        return true;
    }

    protected override void ApplyTheme()
    {
        // No theme-aware visuals in this control today (base subscription is harmless).
    }

    protected override void CleanupForShutdownCore()
    {
        // Nothing to dispose: VM watchers belong to BoxViewModel (window-managed),
        // handlers detach in OnUnloadedCore / base Unloaded.
    }

    private void DetailsGrid_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? FindDataGridRow(d) : null;
        if (row == null)
        {
            // Don't clear origin here — Root preview already set it for left clicks; only clear for non-row
            return;
        }
        // Middle / XButton handled here (Root only handles Left)
        if (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.XButton1 || e.ChangedButton == MouseButton.XButton2)
        {
            ItemBorder_PreviewMouseDown(row, e);
            return;
        }
        // Left button selection + drag origin is handled in Root_PreviewMouseLeftButtonDown (higher tunnel) before DataGrid's Selector.
        // Nothing to do here for Left — Root already set _gridDragRow/_gridDragVm/_dragStart and marked Handled.
    }

    private void DetailsGrid_OnMouseMove(object sender, MouseEventArgs e)
    {
        // Use origin row (where mousedown happened) for drag, not current hover row
        var row = _gridDragRow;
        var vm = _gridDragVm;
        if (row == null || vm == null) return;
        if (_gridDragEpoch != WindowDragController.CurrentInputEpoch)
        {
            _gridDragRow = null;
            _gridDragVm = null;
            return;
        }
        // Still verify left button pressed and window active inside ItemBorder_MouseMove
        ItemBorder_MouseMove(row, e);
    }

    private void DetailsGrid_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? FindDataGridRow(d) : null;
        if (row == null) return;
        DetailsRow_DoubleClick(row, e);
    }

    private void DetailsGrid_OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? FindDataGridRow(d) : null;
        if (row == null) return;
        DisarmDrag();
        ItemBorder_MouseRightButtonUp(row, e);
        DisarmDrag();
        // Suppress DataGrid's default context menu
        e.Handled = true;
    }

    private void DetailsGrid_OnKeyDown(object sender, KeyEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? FindDataGridRow(d) : null;
        if (row == null)
        {
            // Also handle when focus is on DataGrid itself (e.g., Ctrl+A without row)
            if (sender is DataGrid dg && dg.SelectedItems.Count > 0) { /* let ItemBorder_KeyDown handle via focused row */ }
            return;
        }
        ItemBorder_KeyDown(row, e);
    }

    private void DetailsGrid_OnGotFocus(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        // KeyboardFocusChangedEventArgs.OriginalSource may be the focused element itself;
        // also try NewFocus/OldFocus when available.
        if (d == null && e is KeyboardFocusChangedEventArgs kf)
            d = kf.NewFocus as DependencyObject;
        var row = d != null ? FindDataGridRow(d) : null;
        // Fallback: when focus lands directly on the DataGrid (e.g. after programmatic
        // Keyboard.Focus(row) where row is not yet realized), use keyboard focus.
        if (row == null && Keyboard.FocusedElement is DependencyObject kbd)
            row = FindDataGridRow(kbd);
        // Final fallback for virtualization: use selected item's container.
        if (row == null && DetailsGrid.SelectedItem is FolderItemViewModel selVm)
        {
            row = DetailsGrid.ItemContainerGenerator.ContainerFromItem(selVm) as DataGridRow;
            if (row != null) { ItemBorder_GotFocus(row, e); return; }
        }
        if (row == null) return;
        ItemBorder_GotFocus(row, e);
    }

    // Direct per-row handler used by DataGrid.RowStyle EventSetters (reliable even when
    // bubbling focus events are handled by DataGrid internals).
    private void DetailsRow_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridRow row) ItemBorder_GotFocus(row, e);
    }

    private void DetailsGrid_OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? FindDataGridRow(d) : null;
        if (row != null)
            ItemBorder_MouseLeftButtonUp(row, e);
        // Clear drag origin on mouse up (also cleared after successful DoDragDrop via _suppress)
        if (e.LeftButton == MouseButtonState.Released || !_dragging)
        {
            _gridDragRow = null;
            _gridDragVm = null;
        }
        _dragOrigin = null;
    }

    private void DetailsGrid_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DisarmDrag();
        var row = e.OriginalSource is DependencyObject d ? FindDataGridRow(d) : null;
        if (row == null) return;
        ItemBorder_ContextMenuOpening(row, e);
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
        if (IconsScroll is not null) IconsScroll.Visibility = isIcons ? Visibility.Visible : Visibility.Collapsed;
        IconsSortBar.Visibility = isIcons ? Visibility.Visible : Visibility.Collapsed;
        DetailsGrid.Visibility = isIcons ? Visibility.Collapsed : Visibility.Visible;
        BackButton.IsEnabled = !box.IsAtRoot;
        if (ViewToggleIcon != null)
        {
            ViewToggleIcon.Symbol = isIcons ? Wpf.Ui.Controls.SymbolRegular.AppsListDetail24 : Wpf.Ui.Controls.SymbolRegular.Grid24;
            ViewToggleButton.ToolTip = isIcons ? "Switch to Details" : "Switch to Icons";
        }
        RefreshIconSize();
        UpdateSortArrows();
    }

    public void UpdateChrome(bool show)
    {
        if (this.DataContext is null) return;
        if (IconsScroll is not null) IconsScroll.VerticalScrollBarVisibility = show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        DetailsGrid.VerticalScrollBarVisibility = show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        DetailsGrid.HorizontalScrollBarVisibility = show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    private static readonly string[] OriginalHeaders = ["Name", "Size", "Type", "Date modified"];
    private bool _isUpdatingSortUi;

    private void UpdateSortArrows()
    {
        if (Box == null) return;
        // DetailsGrid headers
        if (DetailsGrid.Columns.Count > 0)
        {
            string expected = Box.FolderSortBy switch
            {
                FolderSortMode.Size => "Size",
                FolderSortMode.Type => "Type",
                FolderSortMode.DateModified => "Date modified",
                _ => "Name",
            };
            string arrow = Box.FolderSortAscending ? " ▲" : " ▼";
            foreach (var col in DetailsGrid.Columns)
            {
                string raw = (col.Header as string) ?? string.Empty;
                string baseHeader = raw.TrimEnd(' ', '▲', '▼', '◄', '►');
                if (string.IsNullOrWhiteSpace(baseHeader)) continue;
                bool isSorted = string.Equals(baseHeader, expected, StringComparison.Ordinal);
                string normalized = baseHeader + (isSorted ? arrow : string.Empty);
                col.Header = normalized;
                if (isSorted) col.SortDirection = Box.FolderSortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
                else col.SortDirection = null;
            }
        }
        // IconsSortBar sync
        if (IconsSortCombo != null && IconsSortArrow != null)
        {
            _isUpdatingSortUi = true;
            try
            {
                string tag = Box.FolderSortBy switch
                {
                    FolderSortMode.Size => "Size",
                    FolderSortMode.Type => "Type",
                    FolderSortMode.DateModified => "Date modified",
                    _ => "Name",
                };
                foreach (ComboBoxItem cbi in IconsSortCombo.Items)
                {
                    if ((cbi.Tag as string) == tag) { IconsSortCombo.SelectedItem = cbi; break; }
                }
                IconsSortArrow.Text = Box.FolderSortAscending ? "▲" : "▼";
                IconsSortDirectionButton.ToolTip = Box.FolderSortAscending ? "Ascending" : "Descending";
            }
            finally { _isUpdatingSortUi = false; }
        }
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Localization.Loc.Get("FolderPortal.SelectFolder") };
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
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            }
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Box == null) return;
        Box.NavigateUp();
        RequestSave?.Invoke();
        UpdateView();
        if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
    }

    private ScrollViewer? IconsScroll => IconsList?.ScrollViewerHost;

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        if (Box == null) return;
        Box.FolderPortalViewMode = Box.FolderPortalViewMode == Core.Models.FolderPortalViewMode.Icons
            ? Core.Models.FolderPortalViewMode.Details
            : Core.Models.FolderPortalViewMode.Icons;
        RequestSave?.Invoke();
        UpdateView();
    }

    private void DetailsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string raw = (e.Column.Header as string) ?? string.Empty;
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

    /// <summary>True while a row's inline rename editor is open.</summary>
    public bool HasRenamingItem => Box?.FolderItems.Any(i => i.IsEditing) == true;

    /// <summary>Focuses the first row the filter lets through.</summary>
    public void FocusFirstVisibleItem()
    {
        if (Box is not { } box) { return; }

        var first = box.FolderItems.FirstOrDefault(i => !i.IsFilteredOut);
        if (first is null) { return; }

        SelectOnly(first);
        int index = box.FolderItems.IndexOf(first);
        _focusedIndex = index;
        _anchorIndex = index;
        ScrollToItem(index);
        FocusItem(index);
    }

    /// <summary>Re-reads the folder from disk - the F5 path for a portal.</summary>
    public Task RefreshFolderAsync() => Box?.RefreshFolderAsync() ?? Task.CompletedTask;

    public void ClearSelection()
    {
        if (Box == null) return;
        foreach (var it in Box.FolderItems) it.IsSelected = false;
        _anchorIndex = -1;
    }

    public void ClearSelectionOnDeactivate()
    {
        if (_renameService.IsEditing) return;
        if (_dragging || _suppressDragUntilMouseUp) return;
        if ((DateTime.UtcNow - _lastOpenUtc).TotalMilliseconds < 1200) return;
        ClearSelection();
    }

    /// <summary>True when the cursor (drop point) is over a window owned by another process —
    /// i.e. the OLE drop landed outside the app. Call only after the drag ghost is closed so it
    /// can never be mistaken for the target.</summary>
    private static bool IsDropTargetExternal()
    {
        try
        {
            if (!Maktaby.Win32.NativeMethods.Win32Apis.GetCursorPos(out var pt)) return false;
            var hwnd = Maktaby.Win32.NativeMethods.Win32Apis.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            Maktaby.Win32.NativeMethods.Win32Apis.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid != (uint)System.Environment.ProcessId;
        }
        catch { return false; }
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

    private void IconsHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindItemBorder(src) != null) return;
        // Press began on empty space — must never start an item drag from here.
        _dragOrigin = null;
        if (_renameService.IsEditing) return;
        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!additive) ClearSelection();
    }

    private static Border? FindItemBorder(DependencyObject src)
    {
        DependencyObject? cur = src;
        while (cur != null)
        {
            if (cur is Border { DataContext: FolderItemViewModel }) return (Border)cur;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    // ---- Icons view handlers ----

    private void DisarmDrag()
    {
        _dragOrigin = null;
        _dragging = false;
        _moved = false;
        _gridDragRow = null;
        _gridDragVm = null;
        WindowDragController.InvalidateItemDrags();
    }

    private void ItemBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: FolderItemViewModel vm } border) return;
        border.Focus();
        if (vm.IsEditing) { _dragOrigin = null; e.Handled = true; return; }

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
            if (!vm.IsSelected)
                SelectOnly(vm);
            else
                _anchorIndex = Box?.FolderItems.IndexOf(vm) ?? -1;
        }

        _dragStart = e.GetPosition(null);
        _dragOrigin = WindowDragController.IsFreshPress(e) ? border : null;
        _dragEpoch = WindowDragController.CurrentInputEpoch;
        _dragging = false;
        _moved = false;

        if (e.ClickCount == 2)
        {
            // Double-click opens — never arms a drag from here.
            _dragOrigin = null;
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
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
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

    private Point _dragStart;
    private bool _dragging;
    private bool _moved;
    private bool _suppressDragUntilMouseUp;
    public bool IsDragging => _dragging || _suppressDragUntilMouseUp;
    private static DateTime _lastOpenUtc;
    private FrameworkElement? _dragOrigin;
    private long _dragEpoch;
    private DragGhostWindow? _dragGhost;
    private DataGridRow? _gridDragRow;
    private FolderItemViewModel? _gridDragVm;
    private long _gridDragEpoch;

    private void ItemBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_suppressDragUntilMouseUp)
        {
            if (e.LeftButton == MouseButtonState.Released) _suppressDragUntilMouseUp = false;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed || _dragging) return;
        // Only start a drag when the press began on this same item; a native shell menu
        // eats presses in every window, so also require a matching input epoch.
        if (_dragOrigin is null || !ReferenceEquals(sender, _dragOrigin))
        {
            return;
        }
        if (_dragEpoch != WindowDragController.CurrentInputEpoch)
        {
            _dragOrigin = null;
            return;
        }
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm } fe) return;
        // Only drag from active window
        if (Window.GetWindow(fe) is Window w && !w.IsActive) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance) return;

        // Native throttle (DragDetect, same as Explorer): captures the mouse and decides
        // press-vs-drag authoritatively. A menu-dismiss click releases inside the system
        // drag rect -> FALSE -> treated as a click, never a random drag, no matter which
        // window's menu ate the original press.
        var throttleWin = Window.GetWindow(fe);
        var throttleHwnd = throttleWin != null ? new WindowInteropHelper(throttleWin).Handle : IntPtr.Zero;
        if (!Win32Apis.ConfirmDrag(throttleHwnd))
        {
            _dragOrigin = null;
            _gridDragRow = null;
            _gridDragVm = null;
            return;
        }

        _moved = true;

        // Determine dragged set: all selected in current view, or just the item under cursor.
        // Do NOT mutate selection here — starting a drag should never clear/change the selection.
        // Plain clicks on already-selected items keep multi-selection for drag; collapsing to
        // single happens on MouseUp if no drag occurred (see ItemBorder_MouseLeftButtonUp).
        List<FolderItemViewModel> dragged;
        if (Box != null && Box.FolderItems.Any(i => i.IsSelected) && vm.IsSelected)
            dragged = Box.FolderItems.Where(i => i.IsSelected).ToList();
        else
        {
            dragged = new List<FolderItemViewModel> { vm };
        }

        var paths = dragged.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))).ToArray();
        if (paths.Length == 0) return;

        _dragging = true;
        WindowDragController.DraggingSourceWindow = Window.GetWindow(fe);
        var dragEffect = DragDropEffects.None;
        try
        {
            _dragGhost = new DragGhostWindow();
            if (dragged.Count == 1)
                _dragGhost.SetItem(dragged[0].Icon, dragged[0].DisplayName);
            else
                _dragGhost.SetItems(dragged[0].Icon, dragged.Count);
            _dragGhost.Show();
            PositionDragGhost(fe);
            DragDrop.AddGiveFeedbackHandler(fe, OnGiveFeedback);
            var data = new DataObject(DataFormats.FileDrop, paths);
            // Also set as Shell IDList for virtual items if needed, but FileDrop covers most
            dragEffect = DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally
        {
            DragDrop.RemoveGiveFeedbackHandler(fe, OnGiveFeedback);
            _dragGhost?.Close();
            _dragGhost = null;
            _dragging = false;
            _dragOrigin = null;
            if (WindowDragController.DraggingSourceWindow == Window.GetWindow(fe))
                WindowDragController.DraggingSourceWindow = null;
            // The release that ends the drag may land in another app, which then never delivers
            // a mouse-up to us. If the button is already up, the "until mouse up" window is over:
            // leaving suppress set would defeat deactivation-clearing forever (stale selection
            // in an inactive container). Only an Esc-cancel with the button still held keeps it
            // until the real mouse-up arrives.
            _suppressDragUntilMouseUp = Mouse.LeftButton != MouseButtonState.Released;
            if (dragEffect == DragDropEffects.None || IsDropTargetExternal())
            {
                // Canceled, refused, or dropped outside our windows (another app took it):
                // don't leave a stale selection behind in the source.
                ClearSelection();
            }
            var srcWin = WindowDragController.DraggingSourceWindow;
            if (WindowDragController.DraggingSourceWindow == Window.GetWindow(fe))
                WindowDragController.DraggingSourceWindow = null;
            Dispatcher.BeginInvoke(() =>
            {
                (srcWin as WidgetWindow)?.UpdateChrome();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void OnGiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        if (_dragGhost != null && sender is UIElement src)
            PositionDragGhost(src);
    }

    private void PositionDragGhost(UIElement src)
    {
        if (_dragGhost == null) return;
        if (!Maktaby.Win32.NativeMethods.Win32Apis.GetCursorPos(out var pt)) return;
        var dip = new Point(pt.X, pt.Y);
        if (PresentationSource.FromVisual(_dragGhost)?.CompositionTarget is { } ct)
            dip = ct.TransformFromDevice.Transform(dip);
        _dragGhost.Left = dip.X;
        _dragGhost.Top = dip.Y;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Box?.CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })) { }
        }
        catch { }
    }

    private void ItemBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Deferred single-selection: plain click on already-selected item with multi-selection keeps it for drag on MouseDown,
        // collapse to single on MouseUp only if no drag occurred and no modifier is held (Explorer-like).
        if (!_moved && !_dragging && sender is FrameworkElement { DataContext: FolderItemViewModel vmUp } && !vmUp.IsEditing)
        {
            bool ctrlUp = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shiftUp = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool altUp = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            if (!ctrlUp && !shiftUp && !altUp && vmUp.IsSelected && Box is { } boxUp && boxUp.FolderItems.Count(i => i.IsSelected) > 1)
            {
                SelectOnly(vmUp);
            }
        }
        _dragOrigin = null;
    }

    /// <summary>
    /// Copies (Ctrl+C) or cuts (Ctrl+X) the selected folder entries.
    /// </summary>
    /// <remarks>
    /// A cut changes nothing here. These are real files, so the originals can only be removed once
    /// a paste has produced their replacements - the same rule a box item follows. Falls back to
    /// the entry the key was pressed on, then to the focused one, matching Delete and the arrow
    /// keys. Copying "nothing" silently is worse.
    /// <para>
    /// Called from <c>BoxContainerWindow.Window_PreviewKeyDown</c>, which owns these shortcuts for
    /// every body type. Kept here so the box and the portal share one implementation of "put the
    /// selection on the clipboard" rather than one per key handler.
    /// </para>
    /// </remarks>
    public bool CopySelectedItems(bool cut) => CopySelectedItems(cut, fallback: null);

    private bool CopySelectedItems(bool cut, FolderItemViewModel? fallback)
    {
        if (Box is not { } box) { return false; }

        var selected = box.FolderItems.Where(i => i.IsSelected).ToList();

        // Nothing selected: fall back to the entry the key was pressed on, then to the focused one,
        // matching what Delete and the arrow keys do. Copying "nothing" silently is worse.
        if (selected.Count == 0)
        {
            if (fallback is not null && box.FolderItems.Contains(fallback))
            {
                selected.Add(fallback);
            }
            else if (_focusedIndex >= 0 && _focusedIndex < box.FolderItems.Count)
            {
                selected.Add(box.FolderItems[_focusedIndex]);
            }
        }

        if (selected.Count == 0) { return false; }

        return BoxItemClipboardOps.SetClipboard(selected.Select(ToTransfer), cut);
    }

    private static BoxItemTransfer ToTransfer(FolderItemViewModel item) => new()
    {
        Path = item.Path,
        DisplayName = item.DisplayName,
        ItemType = item.IsDirectory ? BoxItemType.Folder : BoxItemType.File,
    };

    /// <summary>
    /// Explorer-style type-ahead: a printable key jumps to the next item whose name starts with
    /// that character, and typing the same letter again cycles through the matches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The character comes from the active keyboard layout via <c>ToUnicodeEx</c>, not from a fixed
    /// table, so an Arabic or German layout searches for the letter the user actually typed.
    /// </para>
    /// <para>
    /// The search always runs DOWN the list and wraps at the end, whichever way the folder is
    /// sorted. "As the current sort" decides which items are neighbours, not which way the search
    /// travels - reversing it on a descending sort made the next match the one above, which is not
    /// what a typed prefix is for.
    /// </para>
    /// <para>
    /// The run resets when the character changes or after <see cref="TypeAheadResetMs"/>, so typing
    /// "ab" fast searches "ab" while typing "a", waiting, then "b" searches for "b".
    /// </para>
    /// </remarks>
    private bool TryTypeAhead(Key key)
    {
        if (Box is not { } box || box.FolderItems.Count == 0) { return false; }
        if (Keyboard.Modifiers != ModifierKeys.None) { return false; }

        char c = Win32Apis.TranslateKeyToChar(key);
        if (char.IsWhiteSpace(c) || char.IsControl(c)) { return false; }

        var now = DateTime.UtcNow;
        if (c != _typeAheadChar || (now - _typeAheadAt).TotalMilliseconds > TypeAheadResetMs)
        {
            // Different character, or the run went stale: this press starts a fresh search.
            _typeAheadChar = c;
            _typeAheadAt = now;
        }
        else
        {
            _typeAheadAt = now;
        }

        var items = box.FolderItems;

        // Collect the matches and step among THEM. Walking the list and skipping non-matches looks
        // equivalent but is not: with one match the next press landed on the following item, which
        // by definition cannot start with the typed character.
        var initial = c.ToString();
        var matches = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            var name = items[i].DisplayName;
            if (name.StartsWith(initial, StringComparison.CurrentCultureIgnoreCase) ||
                name.StartsWith(initial, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            // Nothing starts with that character: leave the selection alone, the way Explorer does.
            return true;
        }

        // The first match BELOW the current item, wrapping to the first. Strictly greater, so
        // holding a key cycles through the matches instead of sticking to the one under the cursor.
        int position = matches.FindIndex(m => m > _focusedIndex);
        if (position < 0) { position = 0; }

        int target = matches[position];
        SelectOnly(items[target]);
        _focusedIndex = target;
        _anchorIndex = target;
        ScrollToItem(target);
        FocusItem(target);
        return true;
    }

    /// <summary>
    /// Brings an index into view in whichever view is active.
    /// </summary>
    /// <remarks>
    /// <c>FocusItem</c> only knows the icons list, so in the Details view a selection could land
    /// entirely off-screen - which reads as "the key did nothing". Selection that cannot be seen is
    /// not feedback.
    /// </remarks>
    private void ScrollToItem(int index)
    {
        if (Box is not { } box || index < 0 || index >= box.FolderItems.Count) { return; }

        if (box.FolderPortalViewMode == FolderPortalViewMode.Details)
        {
            if (box.FolderItems[index] is { } row)
            {
                DetailsGrid.ScrollIntoView(row);
                DetailsGrid.SelectedItem = row;
            }
        }
    }

    private void ItemBorder_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderItemViewModel vm } && Box != null)
            _focusedIndex = Box.FolderItems.IndexOf(vm);
    }

    private void DetailsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Click on empty area (not on a DataGridRow) — clear selection unless Ctrl/Shift held (like IconsHost)
        if (e.OriginalSource is DependencyObject src)
        {
            if (FindDataGridRow(src) != null) return;
            DependencyObject? cur = src;
            while (cur != null)
            {
                if (cur is DataGridColumnHeader) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }
        // Press began on empty space — must never start a row drag from here.
        _gridDragRow = null;
        _gridDragVm = null;
        _dragOrigin = null;
        if (_renameService.IsEditing) return;
        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!additive) ClearSelection();
    }

    private static DataGridRow? FindDataGridRow(DependencyObject src)
    {
        DependencyObject? cur = src;
        while (cur != null)
        {
            if (cur is DataGridRow row) return row;
            // VisualTreeHelper can return null at template boundaries (e.g. inside
            // DataGridCell / ContentPresenter); fall back to logical parent.
            var visualParent = VisualTreeHelper.GetParent(cur);
            if (visualParent != null) cur = visualParent;
            else if (cur is FrameworkElement fe && fe.Parent is DependencyObject logical) cur = logical;
            else cur = null;
        }
        return null;
    }

    private static DataGridCell? FindDataGridCell(DependencyObject src)
    {
        DependencyObject? cur = src;
        while (cur != null)
        {
            if (cur is DataGridCell cell) return cell;
            var visualParent = VisualTreeHelper.GetParent(cur);
            if (visualParent != null) cur = visualParent;
            else if (cur is FrameworkElement fe && fe.Parent is DependencyObject logical) cur = logical;
            else cur = null;
        }
        return null;
    }

    private void DetailsItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Handled at Root_PreviewMouseLeftButtonDown (ancestor TUNNEL) to suppress ListView Selector before it runs.
        // Keep this no-op so per-item Preview doesn't interfere.
    }

    private void Root_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Intercept DataGrid left clicks BEFORE DataGrid's class handler clears multi-selection.
        // Tunnel is UserControl -> DataGrid -> Row, so handling here (UserControl preview) runs before DataGrid's Selector logic.
        if (Box == null) return;
        if (Box.FolderPortalViewMode != FolderPortalViewMode.Details) return; // Icons handled per-Border
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is not DependencyObject src) return;
        var row = FindDataGridRow(src);
        if (row == null) return; // empty area handled by DetailsGrid_PreviewMouseLeftButtonDown
        if (row.DataContext is not FolderItemViewModel vm) return;
        if (vm.IsEditing) { e.Handled = true; return; }

        // Double-click in Details must be handled here (Preview tunnel) before drag/selection.
        // MouseDoubleClick bubbling is suppressed when Preview is marked Handled, so handle ClickCount==2 directly.
        if (e.ClickCount == 2)
        {
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            if (alt)
            {
                ShowProperties(vm);
            }
            else if (vm.IsDirectory)
            {
                Box?.TryNavigateInto(vm);
                RequestSave?.Invoke();
                UpdateView();
                if (Window.GetWindow(this) is BoxContainerWindow bcwDbl) bcwDbl.ManageFolderWatcher();
            }
            else
            {
                OpenItem(vm);
            }
            e.Handled = true;
            return;
        }

        // Remember origin for drag (used by DetailsGrid_OnMouseMove). ItemBorder_MouseMove
        // also requires the shared icons-origin to match, so arm it here too — the Details
        // template has no per-item MouseDown to do it.
        _gridDragRow = row;
        _gridDragVm = vm;
        _gridDragEpoch = WindowDragController.CurrentInputEpoch;
        _dragOrigin = WindowDragController.IsFreshPress(e) ? row : null;
        _dragEpoch = _gridDragEpoch;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (shift && _anchorIndex >= 0)
        {
            SelectRange(_anchorIndex, Box.FolderItems.IndexOf(vm), false);
            e.Handled = true;
        }
        else if (ctrl)
        {
            vm.IsSelected = !vm.IsSelected;
            _anchorIndex = Box?.FolderItems.IndexOf(vm) ?? -1;
            e.Handled = true;
        }
        else
        {
            if (!vm.IsSelected)
            {
                SelectOnly(vm);
                e.Handled = true;
            }
            else
            {
                // Already selected multi — keep for drag, collapse on MouseUp if no drag
                _anchorIndex = Box.FolderItems.IndexOf(vm);
                e.Handled = true;
            }
        }

        // Move dotted focus rect to the clicked cell (not just the row). Keyboard navigation
        // already moves CurrentCell; mouse must do the same otherwise dotted rect only follows arrows.
        var cell = FindDataGridCell(src);
        if (cell != null)
        {
            // Sync DataGrid.CurrentCell so arrow keys continue from the clicked column.
            try { DetailsGrid.CurrentCell = new DataGridCellInfo(cell); } catch { }
            if (!cell.IsKeyboardFocusWithin)
            {
                Keyboard.Focus(cell);
                cell.Focus();
            }
            cell.BringIntoView();
        }
        else if (!row.IsKeyboardFocusWithin)
        {
            // Fallback: focus first realized cell so dotted rect still appears (row focus alone shows no cell rect)
            var firstCell = FindVisualChild<DataGridCell>(row);
            if (firstCell != null)
            {
                try { DetailsGrid.CurrentCell = new DataGridCellInfo(firstCell); } catch { }
                Keyboard.Focus(firstCell);
                firstCell.Focus();
                firstCell.BringIntoView();
            }
            else
            {
                Keyboard.Focus(row);
                row.Focus();
            }
        }
        _dragStart = e.GetPosition(null);
        _dragging = false;
        _moved = false;
    }

    private void DetailsItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Selection for Details is handled in Root_PreviewMouseLeftButtonDown (tunneling, before ListView Selector).
        // This bubbling handler is only a fallback / for drag bookkeeping if preview was missed.
        if (Box?.FolderPortalViewMode == FolderPortalViewMode.Details) return;

        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm } fe) return;
        if (!fe.IsKeyboardFocusWithin)
        {
            Keyboard.Focus(fe);
            fe.Focus();
        }
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
            if (!vm.IsSelected)
                SelectOnly(vm);
            else
                _anchorIndex = Box?.FolderItems.IndexOf(vm) ?? -1;
        }

        _dragStart = e.GetPosition(null);
        _dragging = false;
        _moved = false;
        e.Handled = true;
    }

    private void ItemBorder_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { DataContext: FolderItemViewModel vm }) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        // Auto-repeat is only meaningful for navigation; holding an action key (rename, delete,
        // open, navigate-up) must not re-fire it — especially the destructive ones.
        bool isNavigation = key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End;
        if (e.IsRepeat && !isNavigation)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.F2)
        {
            StartRename(vm, sender as FrameworkElement);
            e.Handled = true;
        }
        else if (key == Key.Delete)
        {
            e.Handled = true;
            bool permanent = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            DeleteSelected(vm, permanent);
        }
        else if (key == Key.Enter)
        {
            if (alt) ShowProperties(vm);
            else if (vm.IsDirectory)
            {
                Box?.TryNavigateInto(vm);
                RequestSave?.Invoke();
                UpdateView();
                if (Window.GetWindow(this) is BoxContainerWindow bcw2) bcw2.ManageFolderWatcher();
            }
            else
            {
                var selected = Box?.FolderItems.Where(i => i.IsSelected).ToList() ?? new List<FolderItemViewModel>();
                if (selected.Count > 0)
                {
                    foreach (var it in selected) OpenItem(it);
                }
                else
                {
                    OpenItem(vm);
                }
            }
            e.Handled = true;
        }
        else if (key == Key.Back)
        {
            Box?.NavigateUp();
            RequestSave?.Invoke();
            UpdateView();
            if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            e.Handled = true;
        }
        else if (key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down || key == Key.Home || key == Key.End)
        {
            if (Box?.FolderPortalViewMode == FolderPortalViewMode.Icons)
            {
                HandleArrowKey(key);
                e.Handled = true;
            }
        }
        else if (key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (Box != null)
            {
                foreach (var it in Box.FolderItems) it.IsSelected = true;
                e.Handled = true;
            }
        }
        else if (TryTypeAhead(key))
        {
            e.Handled = true;
        }
    }

    private void HandleArrowKey(Key key)
    {
        if (Box == null || Box.FolderItems.Count == 0) return;
        int current = _focusedIndex >= 0 ? _focusedIndex : (Box.FolderItems.FirstOrDefault(i => i.IsSelected) is { } sel ? Box.FolderItems.IndexOf(sel) : 0);
        int count = Box.FolderItems.Count;
        int cols = GetColumnCount();
        int target;
        switch (key)
        {
            case Key.Left: target = Math.Max(0, current - 1); break;
            case Key.Right: target = Math.Min(count - 1, current + 1); break;
            case Key.Up: target = Math.Max(0, current - cols); break;
            case Key.Down: target = Math.Min(count - 1, current + cols); break;
            case Key.Home: target = 0; break;
            case Key.End: target = count - 1; break;
            default: return;
        }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (shift)
        {
            int anchor = _anchorIndex >= 0 ? _anchorIndex : current;
            _anchorIndex = anchor;
            SelectRange(anchor, target, additive: false);
        }
        else if (!ctrl)
        {
            SelectOnly(Box.FolderItems[target]);
        }
        _focusedIndex = target;
        FocusItem(target);
    }

    private void FocusItem(int index)
    {
        if (Box == null || index < 0 || index >= Box.FolderItems.Count) return;
        // With virtualization the container may not exist: scroll it into view first
        // (two passes — the first establishes extent/viewport, the second lands the offset),
        // then resolve the now-realized container.
        if (FindVisualChild<Maktaby.Controls.VirtualizingIconPanel>(IconsList) is Maktaby.Controls.VirtualizingIconPanel panel)
        {
            panel.BringIndexIntoView(index);
            IconsList.UpdateLayout();
            panel.BringIndexIntoView(index);
            IconsList.UpdateLayout();
        }
        var cp = IconsList.ItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        var border = (cp?.FindName("ItemBorder") as Border) ?? FindVisualChild<Border>(cp);
        border?.Focus();
    }

    /// <summary>Outer tile cell edge: tile (IconSize+36 via TileWidth) + Border Margin 4.
    /// Must match the virtualizing panel's ItemWidth/ItemHeight (ConverterParameter=40).</summary>
    private const double TileOuterExtra = 40;

    private int GetColumnCount()
    {
        if (Box == null || Box.FolderItems.Count == 0) return 1;
        // Arithmetic, not container-walking: with virtualization unrealized containers return
        // null from ContainerFromIndex, which broke the old row scan whenever row 0 was scrolled out.
        double tile = IconSize + TileOuterExtra;
        double width = IconsList.ActualWidth;
        if (width <= 0 || tile <= 0) return 1;
        return Math.Max(1, (int)Math.Floor(width / tile));
    }

    private void Control_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue == false && !_renameService.IsEditing && !_dragging && !_suppressDragUntilMouseUp)
        {
            if ((DateTime.UtcNow - _lastOpenUtc).TotalMilliseconds < 1200) return;
            ClearSelection();
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
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            }
            else OpenItem(vm);
        }
    }

    private void DetailsRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { DataContext: FolderItemViewModel vm })
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
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
            }
            else OpenItem(vm);
            e.Handled = true;
        }
    }

    private void DeleteSelected(FolderItemViewModel clicked, bool permanent)
    {
        if (Box == null) return;
        // One batched operation (one shell dialog): cancelling aborts everything. The previous
        // per-item fan-out showed a modal per file, so cancelling one dialog never stopped the rest.
        var selected = Box.FolderItems.Where(i => i.IsSelected).ToList();
        var targets = selected.Count > 0 ? selected : [clicked];
        _ = Box.DeleteFolderItemsAsync(targets, permanent);
    }

    private void StartRename(FolderItemViewModel vm, FrameworkElement? element)
    {
        if (element == null) return;
        if (_renameService.IsEditing) _renameService.Dismiss();
        vm.IsEditing = true;
        double minW = Box?.FolderPortalViewMode == FolderPortalViewMode.Details ? 140 : 110;
        double maxW = Box?.FolderPortalViewMode == FolderPortalViewMode.Details ? 220 : 190;
        _renameService.StartEdit(vm.DisplayName, element, minW, maxW, 13,
            onCommit: newName => _ = HandleFolderRenameCommit(vm, newName, element),
            onDismiss: _ =>
            {
                vm.IsEditing = false;
                element.Focus();
            },
            commitOnDismiss: true);
    }

    private async Task HandleFolderRenameCommit(FolderItemViewModel vm, string newNameRaw, FrameworkElement? focusTarget)
    {
        string newName = newNameRaw.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            vm.IsEditing = false;
            focusTarget?.Focus();
            return;
        }
        string currentBase = System.IO.Path.GetFileNameWithoutExtension(vm.Path);
        if (newName.Equals(currentBase, StringComparison.OrdinalIgnoreCase))
        {
            vm.IsEditing = false;
            focusTarget?.Focus();
            return;
        }
        string ext = System.IO.Path.GetExtension(vm.Path);
        if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(newName)) newName += ext;
        bool ok = await (Box?.RenameFolderItemAsync(vm, newName) ?? Task.FromResult(false));
        vm.IsEditing = false;
        if (ok && focusTarget != null) focusTarget.Focus();
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
        _lastOpenUtc = DateTime.UtcNow;
        item.IsSelected = false;
        try
        {
            var path = item.Path;
            if (Directory.Exists(path))
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })) { }
            else if (File.Exists(path))
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })) { }
        }
        catch { }
    }

    private void ShowProperties(FolderItemViewModel vm)
    {
        var window = Window.GetWindow(this);
        IntPtr hwnd = window != null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
        Win32.NativeMethods.Win32Apis.ShowProperties(hwnd, vm.Path, null);
    }

    /// <summary>
    /// Right-click on bare portal surface opens the paste menu; a right-click on a row is left to
    /// the native shell menu.
    /// </summary>
    private void FolderPortal_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Box is not { } box || !box.HasFolder) { return; }
        if (IsOverFolderItem(e.OriginalSource as DependencyObject)) { return; }

        var menu = (ContextMenu?)Resources["FolderEmptySpaceMenu"];
        if (menu is null) { return; }

        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void FolderEmptySpaceMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) { return; }

        var item = menu.Items.OfType<MenuItem>().FirstOrDefault();
        if (item is null) { return; }

        // Set here rather than with the LocKey markup extension, which fails to compile in this
        // file (see the comment on the menu in the XAML). Same string, same live-switch behaviour.
        item.Header = Loc.Get("Box.PasteItems");

        try
        {
            var clipboard = App.Services.GetService<IBoxItemClipboard>();
            item.IsEnabled = clipboard is not null &&
                             (clipboard.Get() is { HasItems: true } || clipboard.GetFileDrop() is { HasFiles: true });
        }
        catch
        {
            item.IsEnabled = false;
        }
    }

    /// <summary>
    /// Pastes into the folder the portal is showing, with the shell's own copy/move. A folder
    /// portal shows real files, so this is a genuine file operation into the target folder rather
    /// than the Desktop materialisation a box needs. The portal's folder watcher brings the new
    /// files in.
    /// </summary>
    private void FolderEmptySpacePaste_Click(object sender, RoutedEventArgs e) => _ = PasteIntoFolderAsync();

    /// <summary>
    /// Pastes into the folder the portal is showing, with the shell's own copy/move. A folder
    /// portal shows real files, so this is a genuine file operation into the target folder rather
    /// than the Desktop materialisation a box needs.
    /// </summary>
    /// <remarks>
    /// Owned by <c>BoxContainerWindow.Window_PreviewKeyDown</c> together with copy and cut, so all
    /// three work whether or not an item happens to be focused. The empty-space menu routes here
    /// too.
    /// </remarks>
    /// <returns>True when files actually landed.</returns>
    public async Task<bool> PasteIntoFolderAsync()
    {
        var folder = Box?.CurrentFolderPath ?? Box?.FolderPath;
        if (string.IsNullOrWhiteSpace(folder)) { return false; }

        int seq = ++_copyingSeq;
        var progress = new Progress<DropHelper.DropProgress>(p =>
        {
            if (seq != _copyingSeq) { return; }
            IsCopyingFiles = true;
            CopyStatusText = p.Total > 1
                ? $"Pasting {p.Processed} of {p.Total} - {p.CurrentName}"
                : $"Pasting {p.CurrentName}";
        });

        bool any = false;

        try
        {
            var pasted = await BoxItemClipboardOps.PasteIntoFolderAsync(folder, RequestSave, progress);
            any = pasted.Count > 0;

            // Refresh explicitly rather than waiting for the folder watcher: the shell copy is one
            // operation against a folder the watcher may have already enumerated, so the new files
            // would otherwise not show until something else triggered a reload.
            if (any && Box is { } box)
            {
                await box.RefreshFolderAsync();

                // Same rule as a drop into a filtered box: keep the search and reveal what landed,
                // rather than clearing a search the user is still using to find something.
                if (box.IsFilterActive)
                {
                    box.RevealFolderRows(pasted);
                }

                SelectPasted(pasted);
            }
        }
        finally
        {
            if (seq == _copyingSeq)
            {
                _copyingSeq++;
                IsCopyingFiles = false;
            }
        }

        return any;
    }

    /// <summary>
    /// Selects what the paste delivered and brings the first of it into view.
    /// </summary>
    /// <remarks>
    /// Matching is by path, because the paste returns the destination paths it created (including
    /// any "(1)" suffix the uniqueness rule added) and the refreshed items are new view models.
    /// Scrolling matters as much as selecting here: a paste can land outside the viewport, and a
    /// selection the user cannot see reads as "nothing happened".
    /// </remarks>
    private void SelectPasted(IReadOnlyList<string> paths)
    {
        if (Box is not { } box || paths.Count == 0) { return; }

        var pasted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var first = -1;

        foreach (var item in box.FolderItems)
        {
            if (!pasted.Contains(item.Path)) { continue; }

            item.IsSelected = true;
            if (first < 0) { first = box.FolderItems.IndexOf(item); }
        }

        if (first < 0) { return; }

        _focusedIndex = first;
        _anchorIndex = first;

        // Scroll for both view modes: a paste can land far outside the viewport, and a selection
        // the user cannot see reads as "nothing happened".
        ScrollToItem(first);
        FocusItem(first);
    }

    /// <summary>
    /// True when the source sits inside a row of the portal's grid, i.e. the right-click belonged
    /// to an item rather than to the empty surface behind them.
    /// </summary>
    private static bool IsOverFolderItem(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: FolderItemViewModel }) { return true; }
            if (current is Visual or System.Windows.Media.Media3D.Visual3D) { continue; }
            if (current is not FrameworkElement) { break; }
        }

        return false;
    }

    private void ItemBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FolderItemViewModel vm) return;
        DisarmDrag();
        var window = Window.GetWindow(this);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        ShellContextMenu.ShowForPath(hwnd, vm.Path);
        DisarmDrag();
        WindowDragController.NoteMenuClosed();
        e.Handled = true;
    }


    private void ItemBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DisarmDrag();
        Helpers.ToolTipGuard.Close();
        e.Handled = true;
    }

    private void Control_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Box == null) return;
        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (Box.GoBack())
            {
                UpdateView();
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            if (Box.GoForward())
            {
                UpdateView();
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
    }

    private void ItemBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderItemViewModel vm } fe) return;
        if (e.ChangedButton == MouseButton.Left)
        {
            _dragStart = e.GetPosition(null);
            _dragOrigin = WindowDragController.IsFreshPress(e) ? fe : null;
            _dragEpoch = WindowDragController.CurrentInputEpoch;
            _dragging = false;
            _moved = false;
        }
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
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
                e.Handled = true;
            }
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            if (Box?.GoForward() == true)
            {
                UpdateView();
                if (Window.GetWindow(this) is BoxContainerWindow bcw) bcw.ManageFolderWatcher();
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
            using (System.Diagnostics.Process.Start(psi)) { }
        }
        catch { }
    }

    private void FolderPortal_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = null;
        if (_suppressDragUntilMouseUp) _suppressDragUntilMouseUp = false;
    }

    private void IconsSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSortUi) return;
        if (IconsSortCombo.SelectedItem is not ComboBoxItem cbi) return;
        string tag = cbi.Tag as string ?? "Name";
        FolderSortMode newMode = tag switch
        {
            "Size" => FolderSortMode.Size,
            "Type" => FolderSortMode.Type,
            "Date modified" => FolderSortMode.DateModified,
            _ => FolderSortMode.Name,
        };
        if (Box == null) return;
        if (Box.FolderSortBy == newMode) return;
        Box.FolderSortBy = newMode;
        // Box setter already calls ApplyFolderSort
        UpdateSortArrows();
        RequestSave?.Invoke();
    }

    private void IconsSortDirection_Click(object sender, RoutedEventArgs e)
    {
        if (Box == null) return;
        Box.FolderSortAscending = !Box.FolderSortAscending;
        UpdateSortArrows();
        RequestSave?.Invoke();
    }
}
