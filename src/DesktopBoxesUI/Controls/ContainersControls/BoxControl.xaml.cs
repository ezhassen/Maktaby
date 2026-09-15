using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Shell.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views.Containers;
using DesktopBoxesUI.Views.HelpersViews;
using DesktopBoxesUI.Win32.NativeMethods;
using WindowsNative;
using DesktopBoxesUI.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopBoxesUI.Controls.ContainersControls;

/// <summary>
/// Renders the items inside a Box and handles dragging an item out (to another Box) and dropping
/// an item in (from another Box). Also opens an item when activated, matching the user's Explorer
/// single/double-click preference. The cross-box move and persistence are coordinated through the
/// shared <see cref="MainViewModel"/> and the <see cref="RequestSave"/> callback.
/// </summary>
public partial class BoxControl : UserControl
{
    private Point _dragStart;
    private bool _dragging;
    private bool _moved;
    private Border? _dragOrigin;
    private long _dragEpoch;
    private bool _suppressDragUntilMouseUp;
    public bool IsDragging => _dragging || _suppressDragUntilMouseUp;
    private static bool _suppressNextContextMenu;
    private DropIndicatorAdorner? _dropAdorner;
    private int _insertIndex = -1;
    private DragGhostWindow? _ghost;

    // Drag visual gap - collapsed source items and placeholder in target
    private static readonly List<UIElement> _collapsedForDrag = new();
    private static BoxControl? _dragSourceControl;
    private Border? _gapPlaceholder;
    private WrapPanel? _wrapPanelCache;

    // Selection / marquee state.
    private int _anchorIndex = -1;
    private int _focusedIndex = -1;
    private bool _marqueeActive;
    private Point _marqueeStart;
    private HashSet<BoxItemViewModel>? _marqueeBase;

    private readonly ItemRenameService _renameService = new();

    private static readonly bool _singleClick = ShellSettings.IsSingleClickToOpen();
    private static DateTime _lastOpenUtc;

    public BoxControl()
    {
        InitializeComponent();
        // Tab switches re-assign DataContext (UpdateBody) — re-resolve the icon size with it.
        DataContextChanged += (_, _) => RefreshIconSize();
    }

    /// <summary>The Box whose items are rendered by this control.</summary>
    private BoxViewModel? Box => DataContext as BoxViewModel;

    /// <summary>Resolved icon pixel size for this box's tiles: Box.IconSize when set, otherwise
    /// UserSettings.DefaultBoxIconSize, otherwise 32. Drives tile + image sizing via bindings.</summary>
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(BoxControl), new PropertyMetadata(32.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        private set => SetValue(IconSizeProperty, value);
    }
    public void UpdateChrome(bool show)
    {
        if (this.DataContext is null) return;
        VerticalScrollBarVisibility = show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }
    /// <summary>Re-resolves the icon size from the current Box model and user defaults. Null default
    /// falls back to the LIVE desktop icon size (watched by DesktopIconSizeService).</summary>
    public void RefreshIconSize()
    {
        int resolved = Box?.Model.IconSize
            ?? App.Services.GetRequiredService<Core.Interfaces.ISettingsService>().UserSettings.DefaultBoxIconSize
            ?? App.Services.GetRequiredService<Core.Interfaces.IDesktopIconSizeService>().Current;

        IconSize = System.Math.Clamp(resolved, 16, 128);
    }

    public MainViewModel? Host { get; set; }

    public IPersistenceService? Persistence { get; set; }

    public System.Action? RequestSave { get; set; }

    private void CollapseDraggedItems(List<BoxItemViewModel> dragged)
    {
        RestoreCollapsedItems();
        _dragSourceControl = this;
        foreach (var vm in dragged)
        {
            if (Box is null) continue;
            int idx = Box.Items.IndexOf(vm);
            if (idx < 0) continue;
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(idx) is UIElement container)
            {
                // ContentPresenter that wraps the item; collapsing it creates a gap-free reflow
                container.Visibility = Visibility.Collapsed;
                _collapsedForDrag.Add(container);
            }
        }
    }

    private static void RestoreCollapsedItems()
    {
        foreach (var c in _collapsedForDrag)
        {
            c.Visibility = Visibility.Visible;
        }
        _collapsedForDrag.Clear();
        _dragSourceControl = null;
    }

    private WrapPanel? GetWrapPanel()
    {
        if (_wrapPanelCache != null) return _wrapPanelCache;
        _wrapPanelCache = FindVisualChild<WrapPanel>(ItemsList);
        return _wrapPanelCache;
    }

    private void ShowGapPlaceholder(int visibleIndex)
    {
        // Layout placeholder via WrapPanel.Children.Insert is not allowed for ItemsPanel (throws
        // InvalidOperationException). Visual gap is instead shown via DropIndicatorAdorner (small
        // 8px gap between items) which is an adorner overlay and does not affect layout.
        // Keep _gapPlaceholder as a marker so HideGapPlaceholder can clear state without touching panel.
        HideGapPlaceholder();
        _gapPlaceholder = new Border { Width = 8, Height = IconSize + 36, Margin = new Thickness(2) };
    }

    private void HideGapPlaceholder()
    {
        // No panel modification - just clear marker. Adorner gap is cleared via RemoveDropIndicator.
        _gapPlaceholder = null;
    }

    /// <summary>Controls the vertical scrollbar visibility of the item list.</summary>
    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => Scroll.VerticalScrollBarVisibility;
        set => Scroll.VerticalScrollBarVisibility = value;
    }

    private void ItemBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: BoxItemViewModel vm } border)
        {
            return;
        }

        border.Focus();

        if (vm.IsEditing)
        {
            _dragOrigin = null;
            e.Handled = true;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (shift && _anchorIndex >= 0 && Box is { } box)
        {
            SelectRange(_anchorIndex, box.Items.IndexOf(vm), additive: false);
        }
        else if (ctrl)
        {
            vm.IsSelected = !vm.IsSelected;
            _anchorIndex = Box?.Items.IndexOf(vm) ?? -1;
        }
        else
        {
            // Plain click: keep an existing multi-selection (so it can be dragged), otherwise select just this.
            if (!vm.IsSelected)
            {
                SelectOnly(vm);
            }
            else
            {
                _anchorIndex = Box?.Items.IndexOf(vm) ?? -1;
            }
        }

        // Double-click opens — never arms a drag from here (the opened app deactivates
        // our window, breaking the press sequence for any subsequent move).
        bool isDoubleOpen = !_singleClick && e.ClickCount == 2;
        if (isDoubleOpen)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                ShowProperties(vm);
            }
            else
            {
                OpenItem(vm);
            }
        }

        // Mark handled so the empty-area marquee handler on the UserControl does not also fire.
        e.Handled = true;

        _dragStart = e.GetPosition(null);
        // A press that dismissed a native menu is delivered after the menu closes but is
        // physically older (message timestamp) — it must select, but never arm a drag.
        _dragOrigin = (isDoubleOpen || !WindowDragController.IsFreshPress(e)) ? null : border;
        _dragEpoch = WindowDragController.CurrentInputEpoch;
        _dragging = false;
        _moved = false;
    }

    private void ItemBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_suppressDragUntilMouseUp)
        {
            if (e.LeftButton == MouseButtonState.Released)
                _suppressDragUntilMouseUp = false;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || _dragging)
        {
            return;
        }

        // Only start a drag when the press began on this same item. Prevents accidental
        // drags when the button was pressed on empty space and the cursor slides over items.
        if (_dragOrigin is null || !ReferenceEquals(sender, _dragOrigin))
        {
            return;
        }

        // The press sequence must still be valid: any native shell menu opened in any
        // window runs its own modal loop and eats the press, leaving a stale origin armed.
        if (_dragEpoch != WindowDragController.CurrentInputEpoch)
        {
            _dragOrigin = null;
            return;
        }

        // Native behavior: drag only from active window — check, don't activate.
        if (sender is Border b && Window.GetWindow(b) is Window w && !w.IsActive)
        {
            return;
        }

        // A native resize/move modal loop is running on the container: never morph it into an
        // OLE icon drag (this used to drag a shortcut along with the resize and could crash).
        if (WindowDragController.IsNativeSizing)
        {
            return;
        }

        if (sender is Border { DataContext: BoxItemViewModel editing } && editing.IsEditing)
        {
            return;
        }

        // Native WPF drag threshold — no time delay, purely distance based.
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Native throttle (DragDetect, same as Explorer): captures the mouse and decides
        // press-vs-drag authoritatively. A menu-dismiss click releases inside the system
        // drag rect -> FALSE -> treated as a click, never a random drag, no matter which
        // window's menu ate the original press.
        if (sender is Border throttleBorder)
        {
            var throttleHwnd = Window.GetWindow(throttleBorder) is Window throttleWin
                ? new WindowInteropHelper(throttleWin).Handle
                : IntPtr.Zero;
            if (!Win32Apis.ConfirmDrag(throttleHwnd))
            {
                _dragOrigin = null;
                return;
            }
        }

        _moved = true;
        if (sender is Border { DataContext: BoxItemViewModel item } border && Box is { } box)
        {
            // Drag the whole current selection if the clicked item is part of it; otherwise drag just it.
            List<BoxItemViewModel> dragged;
            if (item.IsSelected && box.Items.Any(i => i.IsSelected))
            {
                dragged = box.Items.Where(i => i.IsSelected).ToList();
            }
            else
            {
                SelectOnly(item);
                dragged = new List<BoxItemViewModel> { item };
            }

            if (dragged.Count == 0)
            {
                _dragging = false;
                return;
            }

            _dragging = true;
            WindowDragController.DraggingSourceWindow = Window.GetWindow(border);
            CollapseDraggedItems(dragged);
            _ghost = new DragGhostWindow();
            var dragEffect = DragDropEffects.None;
            try
            {
                if (dragged.Count == 1)
                {
                    _ghost.SetItem(dragged[0].Icon, dragged[0].DisplayName);
                }
                else
                {
                    _ghost.SetItems(dragged[0].Icon, dragged.Count);
                }

                _ghost.Show();
                PositionGhost(border);
                DragDrop.AddGiveFeedbackHandler(border, OnGiveFeedback);
                var data = new DataObject(DndFormats.BoxItems, dragged);
                // Also expose as FileDrop / Shell IDList so external apps (Explorer, etc.) can receive the files
                try
                {
                    var filePaths = dragged
                        .Select(vm => vm.Path)
                        .Where(p => !string.IsNullOrEmpty(p) && (File.Exists(p!) || Directory.Exists(p!)))
                        .Select(p => p!)
                        .ToArray();
                    if (filePaths.Length > 0)
                        data.SetData(DataFormats.FileDrop, filePaths);

                    // For virtual PIDL items, also provide Shell IDList if possible (handled via FileDrop fallback in DropHelper)
                    // No need to add explicit Shell IDList here; FileDrop covers most external drops.
                }
                catch { }
                dragEffect = DragDrop.DoDragDrop(border, data, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
            }
            finally
            {
                DragDrop.RemoveGiveFeedbackHandler(border, OnGiveFeedback);
                _ghost?.Close();
                _ghost = null;
                _dragging = false;
                if (WindowDragController.DraggingSourceWindow == Window.GetWindow(border))
                    WindowDragController.DraggingSourceWindow = null;
                RestoreCollapsedItems();
                RemoveDropIndicator();
                _insertIndex = -1;
                _dragOrigin = null;
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
                if (WindowDragController.DraggingSourceWindow == Window.GetWindow(border))
                    WindowDragController.DraggingSourceWindow = null;
                Dispatcher.BeginInvoke(() =>
                {
                    (srcWin as WidgetWindow)?.UpdateChrome();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }

    private void OnGiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        if (_ghost != null && sender is UIElement source)
        {
            PositionGhost(source);
        }
    }

    private void PositionGhost(UIElement source)
    {
        if (_ghost == null)
        {
            return;
        }

        // Use the real cursor position (physical screen pixels) rather than Mouse.GetPosition, which
        // returns (0,0) during a drag because the mouse is captured by the drag-drop modal loop.
        if (!Win32Apis.GetCursorPos(out POINT p))
        {
            return;
        }

        // Convert physical pixels to DIPs (what Window.Left/Top use) via the window's composition transform.
        Point dip = new(p.X, p.Y);
        if (PresentationSource.FromVisual(_ghost)?.CompositionTarget is { } target)
        {
            dip = target.TransformFromDevice.Transform(dip);
        }

        _ghost.Left = dip.X;// + 12;
        _ghost.Top = dip.Y;// + 12;
    }

    private void ItemBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Plain click on already-selected item with multi-selection: keep it for drag on MouseDown, select single on MouseUp if no drag.
        if (!_moved && !_dragging && sender is Border { DataContext: BoxItemViewModel vmUp } && !vmUp.IsEditing)
        {
            bool ctrlUp = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shiftUp = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (!ctrlUp && !shiftUp && vmUp.IsSelected && Box is { } boxUp && boxUp.Items.Count(i => i.IsSelected) > 1)
            {
                SelectOnly(vmUp);
            }
        }

        if (_singleClick && !_moved && !_dragging && sender is Border { DataContext: BoxItemViewModel item } && !item.IsEditing)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                ShowProperties(item);
            }
            else
            {
                OpenItem(item);
            }
        }

        _dragOrigin = null;
    }

    private async void ItemBorder_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: BoxItemViewModel vm } border)
        {
            return;
        }

        // When a modifier such as Alt is held, the key arrives as a system key: e.Key is Key.System and
        // the real key lives in e.SystemKey. Normalise so Alt+Enter (properties) and friends are recognised.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        if (key == Key.F2)
        {
            StartRename(vm, border);
            e.Handled = true;
        }
        else if (key == Key.Delete)
        {
            bool permanent = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (Box is { } box)
            {
                var selected = box.Items.Where(i => i.IsSelected).ToList();
                if (selected.Count > 0)
                {
                    //foreach (var it in selected)
                    //{
                    //    _ = Host?.DeleteItem(it, permanent);
                    //}
                    if (Host is not null) await Host.DeleteItems(selected, permanent);
                    e.Handled = true;
                    return;
                }
            }

            //_ = Host?.DeleteItem(vm, permanent);
            if (Host is not null) await Host.DeleteItem(vm, permanent);
            e.Handled = true;
        }
        else if (key == Key.Enter)
        {
            if (alt)
            {
                ShowProperties(vm);
            }
            else
            {
                OpenItem(vm);
            }

            e.Handled = true;
        }
        else if (key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down || key == Key.Home || key == Key.End)
        {
            HandleArrowKey(key);
            e.Handled = true;
        }
    }

    private void ItemBorder_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Border { DataContext: BoxItemViewModel vm } && Box is { } box)
        {
            _focusedIndex = box.Items.IndexOf(vm);
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox { DataContext: BoxItemViewModel vm } tb)
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
                FocusItem(GetItemBorderFromTextBox(tb));
            }
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Commit when focus leaves the rename box (e.g. click-away), but do not force focus back into
        // the box — let focus follow the click. Enter/Escape handle their own focus restore.
        if (sender is TextBox { DataContext: BoxItemViewModel vm } && vm.IsEditing)
        {
            _ = CommitRename(vm, null);
        }
    }

    private void StartRename(BoxItemViewModel vm, Border border)
    {
        if (_renameService.IsEditing) _renameService.Dismiss();
        vm.IsEditing = true;
        _renameService.StartEdit(vm.DisplayName, border, 110, 200, 13,
            onCommit: newName => _ = HandleRenameCommit(vm, newName, border),
            onDismiss: _ =>
            {
                vm.IsEditing = false;
                FocusItem(border);
            },
            commitOnDismiss: true);
    }

    private async Task HandleRenameCommit(BoxItemViewModel vm, string newNameRaw, Border? focusTarget)
    {
        string newName = newNameRaw.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            vm.IsEditing = false;
            FocusItem(focusTarget);
            return;
        }
        string currentFull = (!string.IsNullOrEmpty(vm.Path) && System.IO.Path.GetFileName(vm.Path) is { } c) ? c : vm.DisplayName;
        string currentBase = System.IO.Path.GetFileNameWithoutExtension(currentFull);
        if (newName.Equals(currentBase, System.StringComparison.OrdinalIgnoreCase))
        {
            vm.IsEditing = false;
            FocusItem(focusTarget);
            return;
        }
        string ext = System.IO.Path.GetExtension(currentFull);
        if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(newName))
            newName = newName + ext;
        bool ok = await (Host?.RenameItem(vm, newName) ?? Task.FromResult(false));
        vm.IsEditing = false;
        if (ok) FocusItem(focusTarget);
    }

    private async Task CommitRename(BoxItemViewModel vm, Border? focusTarget = null)
    {
        string newName = vm.RenameText.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            vm.IsEditing = false;
            FocusItem(focusTarget);
            return;
        }

        // Compare against the base name (extension hidden in the box), not the full filename.
        string currentFull = (!string.IsNullOrEmpty(vm.Path) && System.IO.Path.GetFileName(vm.Path) is { } c) ? c : vm.DisplayName;
        string currentBase = System.IO.Path.GetFileNameWithoutExtension(currentFull);
        if (newName.Equals(currentBase, System.StringComparison.OrdinalIgnoreCase))
        {
            vm.IsEditing = false;
            FocusItem(focusTarget);
            return;
        }

        // Re-attach the original extension unless the user explicitly typed one (matching Explorer when
        // extensions are hidden), so files/links keep their extension on disk.
        string ext = System.IO.Path.GetExtension(currentFull);
        if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(newName))
        {
            newName = newName + ext;
        }

        bool ok = await (Host?.RenameItem(vm, newName) ?? Task.FromResult(false));
        // On success MainViewModel updated the display name + icon; on failure we stay in edit mode so the
        // user can correct the name. Only restore focus to the item when we actually leave edit mode.
        if (ok)
        {
            vm.IsEditing = false;
        }
        FocusItem(focusTarget);
    }

    private static Border? GetItemBorderFromTextBox(TextBox tb)
    {
        DependencyObject? current = tb;
        while (current is not null)
        {
            if (current is Border { Name: "ItemBorder" } border)
            {
                return border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Border? FindItemBorder(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is Border { DataContext: BoxItemViewModel })
            {
                return (Border)current;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void FocusItem(Border? border)
    {
        if (border is null)
        {
            return;
        }

        // The rename (SHFileOperation) can momentarily hand focus to the desktop/explorer, so re-activate
        // the box window first, then drive keyboard focus back onto the item.
        var window = Window.GetWindow(border);
        window?.Activate();
        Keyboard.Focus(border);
        border.Focus();
    }

    private void ShowProperties(BoxItemViewModel vm)
    {
        var window = Window.GetWindow(this);
        IntPtr hwnd = window != null ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;

        string? pidl = vm.Model.Pidl;
        string? path = string.IsNullOrEmpty(pidl) ? vm.Path : null;
        Win32Apis.ShowProperties(hwnd, path, pidl);
    }

    private static void OpenItem(BoxItemViewModel item)
    {
        _lastOpenUtc = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(item.Model.Pidl))
        {
            try
            {
                if (Win32Apis.LaunchPidl(item.Model.Pidl))
                {
                    return;
                }
            }
            catch
            {
                // fall through to path-based attempt
            }
        }

        var path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (path.Contains('!') && path.Contains('_'))
            {
                // AppUserModelID fallback (UWP/Store app) when no PIDL was resolved.
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:appsFolder\\" + path) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
        }
        catch
        {
            // Best-effort; ignore failures (e.g. access denied).
        }
    }

    private void BoxControl_DragOver(object sender, DragEventArgs e)
    {
        // Let our own container/tab drag payloads bubble up to the owning window (which handles the
        // highlight and the merge/move). Everything else is an item/file drop handled here.
        if (e.Data.GetDataPresent(DndFormats.SourceContainer))
        {
            RemoveDropIndicator();
            e.Effects = DragDropEffects.Move;
            return;
        }

        if ((e.Data.GetDataPresent(DndFormats.BoxItems) || e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("Shell IDList Array")) && DataContext is BoxViewModel)
        {
            HideGapPlaceholder();
            GetWrapPanel()?.UpdateLayout();
            var pt = e.GetPosition(Scroll);
            int newIdx = GetInsertIndex(pt);
            if (newIdx != _insertIndex || _dropAdorner == null)
            {
                _insertIndex = newIdx;
                ShowDropIndicator(_insertIndex);
            }
            else
            {
                ShowDropIndicator(_insertIndex);
            }
            e.Effects = e.Data.GetDataPresent(DndFormats.BoxItems) ? DragDropEffects.Move : DropHelper.GetEffect(e);
            e.Handled = true;
            return;
        }

        RemoveDropIndicator();
        _insertIndex = -1;
        e.Effects = DropHelper.GetEffect(e);
        e.Handled = true;
    }

    private void BoxControl_DragLeave(object sender, DragEventArgs e)
    {
        RemoveDropIndicator();
        _insertIndex = -1;
    }

    private async void BoxControl_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DndFormats.SourceContainer))
        {
            // Handled by the owning BoxContainerWindow's Drop handler.
            RemoveDropIndicator();
            return;
        }

        if (DataContext is not BoxViewModel targetBox)
        {
            return;
        }

        var moved = await DropHelper.AddToBoxAsync(targetBox, Host, e, _insertIndex);
        RemoveDropIndicator();
        _insertIndex = -1;

        // Re-select the items that were just dropped into this box.
        if (moved != null && moved.Count > 0 && Box is { } box)
        {
            foreach (var m in moved)
            {
                var vm = box.Items.FirstOrDefault(i => i.Model == m);
                if (vm != null)
                {
                    vm.IsSelected = true;
                }
            }
            // Focus first dropped item so keyboard navigation and dotted focus rect resume there
            var firstVm = box.Items.FirstOrDefault(i => i.Model == moved[0]);
            if (firstVm != null)
            {
                _focusedIndex = box.Items.IndexOf(firstVm);
                _anchorIndex = _focusedIndex;
                _ = Dispatcher.BeginInvoke(() => FocusItem(_focusedIndex), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        if (e.Handled)
        {
            // Activate target window so selection is visible and next drag starts correctly.
            Window.GetWindow(this)?.Activate();
            RequestSave?.Invoke();
        }
    }

    private List<(UIElement container, int originalIndex)> GetVisibleContainers()
    {
        var list = new List<(UIElement, int)>();
        int count = ItemsList.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(i) is UIElement c && c.Visibility != Visibility.Collapsed)
            {
                list.Add((c, i));
            }
        }
        return list;
    }

    /// <summary>
    /// Computes the visible insert index (0..visibleCount) based on pointer position.
    /// Collapsed dragged items are skipped so the gap reflects the post-removal layout.
    /// For cross-box drops there are no collapsed items and visible==original.
    /// </summary>
    private int GetInsertIndex(Point pt)
    {
        var visible = GetVisibleContainers();
        int visCount = visible.Count;
        if (visCount == 0) return 0;

        const double rowTolerance = 4;
        const double deadZone = 3; // hysteresis around center to prevent flicker

        for (int vi = 0; vi < visCount; vi++)
        {
            var container = visible[vi].container;
            var topLeft = container.TransformToAncestor(Scroll).Transform(new Point(0, 0));
            double w = container.RenderSize.Width;
            double h = container.RenderSize.Height;

            if (pt.Y < topLeft.Y - rowTolerance) return vi;

            bool inRow = pt.Y >= topLeft.Y - rowTolerance && pt.Y <= topLeft.Y + h + rowTolerance;
            if (!inRow) continue;

            double centerX = topLeft.X + w / 2;
            double rightEdge = topLeft.X + w;
            double nextLeft = double.PositiveInfinity;
            if (vi + 1 < visCount)
            {
                var next = visible[vi + 1].container;
                var nextTl = next.TransformToAncestor(Scroll).Transform(new Point(0, 0));
                if (Math.Abs(nextTl.Y - topLeft.Y) < rowTolerance) nextLeft = nextTl.X;
            }
            double gapMid = double.IsPositiveInfinity(nextLeft) ? rightEdge + 8 : (rightEdge + nextLeft) / 2;

            // Dead zone around center - keep previous index to avoid flicker when hovering near edge
            if (Math.Abs(pt.X - centerX) <= deadZone && _insertIndex >= 0)
            {
                // If pointer is within dead zone and previous index was vi or vi+1, keep it
                if (_insertIndex == vi || _insertIndex == vi + 1) return _insertIndex;
            }

            if (pt.X < centerX - deadZone) return vi;
            if (pt.X < gapMid) return vi + 1;
        }
        return visCount;
    }

    private int VisibleIndexToOriginal(int visibleIndex)
    {
        var visible = GetVisibleContainers();
        if (visibleIndex >= visible.Count) return ItemsList.Items.Count;
        if (visibleIndex < 0) return 0;
        // Original index of the visible item at visibleIndex
        return visible[visibleIndex].originalIndex;
    }

    private void ShowDropIndicator(int visibleIndex)
    {
        if (_dropAdorner == null)
        {
            _dropAdorner = new DropIndicatorAdorner(Scroll);
            AdornerLayer.GetAdornerLayer(Scroll)?.Add(_dropAdorner);
        }

        var visible = GetVisibleContainers();
        int visCount = visible.Count;
        double tileH = IconSize + 36;
        if (visCount > 0)
        {
            var sample = visible[Math.Clamp(visibleIndex, 0, visCount - 1)].container;
            tileH = sample.RenderSize.Height;
            if (tileH < 10) tileH = IconSize + 36;
        }

        const double gapW = 8; // small gap between items, not a full tile

        double x, y, w, h;
        if (visCount == 0)
        {
            double tileW = IconSize + 36;
            x = 6;
            y = 6;
            w = tileW;
            h = tileH;
            if (Scroll.ActualWidth > w + 12) x = (Scroll.ActualWidth - w) / 2;
            if (Scroll.ActualHeight > h + 12) y = (Scroll.ActualHeight - h) / 2;
            _dropAdorner.UpdateGap(x, y, w, h);
            _dropAdorner.Update(x + w / 2, y, h);
            ShowGapPlaceholder(visibleIndex);
            return;
        }
        else if (visibleIndex < visCount)
        {
            var c = visible[visibleIndex].container;
            var tl = c.TransformToAncestor(Scroll).Transform(new Point(0, 0));
            x = tl.X - gapW / 2 - 1;
            if (x < 1) x = 1;
            y = tl.Y;
            w = gapW;
            h = tileH;
            _dropAdorner.UpdateGap(x, y, w, h);
            _dropAdorner.Update(x + gapW / 2, y, h);
            ShowGapPlaceholder(visibleIndex);
            return;
        }
        else
        {
            var c = visible[visCount - 1].container;
            var tl = c.TransformToAncestor(Scroll).Transform(new Point(0, 0));
            x = tl.X + c.RenderSize.Width - gapW / 2 + 1;
            double maxX = Scroll.ActualWidth - gapW - 1;
            if (x > maxX) x = maxX;
            if (x < 1) x = 1;
            y = tl.Y;
            w = gapW;
            h = tileH;
            _dropAdorner.UpdateGap(x, y, w, h);
            _dropAdorner.Update(x + gapW / 2, y, h);
            ShowGapPlaceholder(visibleIndex);
            return;
        }
    }

    private void RemoveDropIndicator()
    {
        HideGapPlaceholder();
        if (_dropAdorner != null)
        {
            AdornerLayer.GetAdornerLayer(Scroll)?.Remove(_dropAdorner);
            _dropAdorner = null;
        }
    }

    // ---- Selection / marquee -------------------------------------------------

    private void BoxControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Tunneling handler: it runs before the ScrollViewer/ItemsControl class handlers that would
        // otherwise swallow the bubbling MouseLeftButtonDown, so empty-area marquee selection works.
        // Clicks that land on an item are handled by ItemBorder_MouseLeftButtonDown, so skip those.
        if (e.OriginalSource is DependencyObject src && FindItemBorder(src) is not null)
        {
            return;
        }

        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

        if (!additive)
        {
            ClearSelection();
        }

        // Press began outside any item — it must never start an item drag even if the
        // cursor later slides over an item while held down (marquee owns this gesture).
        _dragOrigin = null;
        _marqueeBase = additive ? new HashSet<BoxItemViewModel>(Box?.Items.Where(i => i.IsSelected) ?? Enumerable.Empty<BoxItemViewModel>()) : null;
        _marqueeStart = e.GetPosition(ItemsList);
        if (!_marqueeActive)
        {
            //ignore scrollBar if visible
            //var horizontalScrollBar = scrollViewer.Template.FindName("PART_HorizontalScrollBar", scrollViewer) as ScrollBar;

            var verticalScrollBar = Scroll.Template.FindName("PART_VerticalScrollBar", Scroll) as System.Windows.Controls.Primitives.ScrollBar;
            double scrollBarWidth = verticalScrollBar?.ActualWidth ?? 0;
            bool hasVerticalScroller = verticalScrollBar is { ActualWidth: > 0, Visibility: Visibility.Visible };
            if (hasVerticalScroller)
            {
                if (_marqueeStart.X > this.RenderSize.Width - scrollBarWidth)
                {
                    return;
                }

            }

        }
        _marqueeActive = true;
        Marquee.Visibility = Visibility.Visible;
        UpdateMarquee(_marqueeStart);
        CaptureMouse();
        e.Handled = true;
    }

    private void BoxControl_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeActive)
        {
            return;
        }

        UpdateMarquee(e.GetPosition(ItemsList));
    }

    private void BoxControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = null;
        if (!_marqueeActive)
        {
            // Even when marquee not active, clear drag suppress on left up
            if (_suppressDragUntilMouseUp) _suppressDragUntilMouseUp = false;
            return;
        }

        _marqueeActive = false;
        Marquee.Visibility = Visibility.Collapsed;
        ReleaseMouseCapture();
        _marqueeBase = null;
    }

    private void BoxControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = null;
        if (_suppressDragUntilMouseUp) _suppressDragUntilMouseUp = false;
    }

    public void ClearSelection()
    {
        if (Box is { } box)
        {
            foreach (var it in box.Items)
            {
                it.IsSelected = false;
            }
        }

        _anchorIndex = -1;
    }

    /// <summary>Called by owning window on Deactivated to avoid stale multi-selections persisting.</summary>
    public void ClearSelectionOnDeactivate()
    {
        if (_marqueeActive || _editing || _dragging || _suppressDragUntilMouseUp) return;
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
            if (!Win32Apis.GetCursorPos(out POINT pt)) return false;
            var hwnd = Win32Apis.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            Win32Apis.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid != (uint)System.Environment.ProcessId;
        }
        catch { return false; }
    }

    private void SelectOnly(BoxItemViewModel vm)
    {
        ClearSelection();
        vm.IsSelected = true;
        _anchorIndex = Box?.Items.IndexOf(vm) ?? -1;
    }

    private void SelectRange(int anchor, int target, bool additive)
    {
        if (Box is not { } box)
        {
            return;
        }

        int lo = Math.Min(anchor, target);
        int hi = Math.Max(anchor, target);

        if (!additive)
        {
            foreach (var it in box.Items)
            {
                it.IsSelected = false;
            }
        }

        for (int i = lo; i <= hi; i++)
        {
            if (i >= 0 && i < box.Items.Count)
            {
                box.Items[i].IsSelected = true;
            }
        }
    }

    private void UpdateMarquee(Point current)
    {
        if (Box is not { } box)
        {
            return;
        }

        double x = Math.Min(_marqueeStart.X, current.X);
        double y = Math.Min(_marqueeStart.Y, current.Y);
        double w = Math.Abs(current.X - _marqueeStart.X);
        double h = Math.Abs(current.Y - _marqueeStart.Y);

        Canvas.SetLeft(Marquee, x);
        Canvas.SetTop(Marquee, y);
        Marquee.Width = w;
        Marquee.Height = h;

        var rect = new Rect(x, y, w, h);
        foreach (var it in box.Items)
        {
            int idx = box.Items.IndexOf(it);
            var container = ItemsList.ItemContainerGenerator.ContainerFromIndex(idx) as UIElement;
            if (container == null)
            {
                continue;
            }

            var tl = container.TransformToAncestor(ItemsList).Transform(new Point(0, 0));
            var itemRect = new Rect(tl, container.RenderSize);
            bool inside = rect.IntersectsWith(itemRect);
            it.IsSelected = inside || (_marqueeBase != null && _marqueeBase.Contains(it));
        }
    }

    private void BoxControl_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Clear the selection when focus leaves the whole control (e.g. another window/app takes focus),
        // but keep it while an inline rename is in progress (the TextBox is inside this control).
        // Do not clear while a drag is active or in the suppress window after DoDragDrop (Esc cancel, drop).
        if ((bool)e.NewValue == false && !_marqueeActive && !_editing && !_dragging && !_suppressDragUntilMouseUp)
        {
            if ((DateTime.UtcNow - _lastOpenUtc).TotalMilliseconds < 1200) return;
            ClearSelection();
        }
    }

    private bool _editing => Box is { } box && box.Items.Any(i => i.IsEditing);

    private void HandleArrowKey(Key key)
    {
        if (Box is not { } box || box.Items.Count == 0)
        {
            return;
        }

        int current = _focusedIndex >= 0 ? _focusedIndex
            : (box.Items.FirstOrDefault(i => i.IsSelected) is { } sel ? box.Items.IndexOf(sel) : 0);
        int count = box.Items.Count;
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
            SelectOnly(box.Items[target]);
        }
        // Ctrl (without Shift): move focus only, preserve existing selection.

        _focusedIndex = target;
        FocusItem(target);
    }

    private void FocusItem(int index)
    {
        if (Box is not { } box || index < 0 || index >= box.Items.Count)
        {
            return;
        }

        var cp = ItemsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        var border = (cp?.FindName("ItemBorder") as Border) ?? FindVisualChild<Border>(cp);
        border?.Focus();
    }

    /// <summary>Items flow in a horizontally-wrapped grid; the column count is the number of items sharing
    /// the first item's row.</summary>
    private int GetColumnCount()
    {
        if (Box is not { } box || box.Items.Count == 0)
        {
            return 1;
        }

        var first = ItemsList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement;
        if (first is null)
        {
            return 1;
        }

        double top0 = first.TransformToAncestor(Scroll).Transform(new Point(0, 0)).Y;
        int cols = 0;
        for (int i = 0; i < box.Items.Count; i++)
        {
            var c = ItemsList.ItemContainerGenerator.ContainerFromIndex(i) as UIElement;
            if (c is null)
            {
                break;
            }

            double top = c.TransformToAncestor(Scroll).Transform(new Point(0, 0)).Y;
            if (Math.Abs(top - top0) < 1)
            {
                cols++;
            }
            else
            {
                break;
            }
        }

        return Math.Max(1, cols);
    }

    private static T? FindVisualChild<T>(DependencyObject? obj) where T : DependencyObject
    {
        if (obj is null)
        {
            return null;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is T t)
            {
                return t;
            }

            var inner = FindVisualChild<T>(child);
            if (inner is not null)
            {
                return inner;
            }
        }

        return null;
    }

    private void DisarmDrag(string reason)
    {
        _dragOrigin = null;
        _dragging = false;
        _moved = false;
        WindowDragController.InvalidateItemDrags();
    }

    private void ItemBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressNextContextMenu)
        {
            _suppressNextContextMenu = false;
            DisarmDrag("suppress-menu");
            e.Handled = true;
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not BoxItemViewModel vm)
        {
            return;
        }

        // The native shell menu runs its own modal loop and eats mouse presses in every
        // window — disarm before and after so no stale origin can start a random drag.
        // NoteMenuClosed additionally records the close tick so the dismissing press
        // (delivered late, but with an older message timestamp) can't re-arm a drag.
        DisarmDrag("menu-open");
        var window = Window.GetWindow(this);
        var hwnd = new WindowInteropHelper(window).Handle;

        if (!string.IsNullOrEmpty(vm.Model.Pidl))
        {
            ShellContextMenu.ShowForPidl(hwnd, Convert.FromBase64String(vm.Model.Pidl));
        }
        else if (!string.IsNullOrEmpty(vm.Path))
        {
            ShellContextMenu.ShowForPath(hwnd, vm.Path);
        }

        _dragOrigin = null;
        _dragging = false;
        _moved = false;
        WindowDragController.NoteMenuClosed();
        e.Handled = true;
    }

    private void ItemBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DisarmDrag("context-menu");
        if (_suppressNextContextMenu)
        {
            _suppressNextContextMenu = false;
            e.Handled = true;
            return;
        }
        e.Handled = true;
    }
}
