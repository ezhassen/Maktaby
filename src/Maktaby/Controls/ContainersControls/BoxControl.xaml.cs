using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Helpers;
using Maktaby.Native;
using Maktaby.Shell.Services;
using Maktaby.ViewModels;
using Maktaby.Views.Containers;
using Maktaby.Views.HelpersViews;
using Maktaby.Win32.NativeMethods;
using Maktaby.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Maktaby.Controls.ContainersControls;

/// <summary>
/// Renders the items inside a Box and handles dragging an item out (to another Box) and dropping
/// an item in (from another Box). Also opens an item when activated, matching the user's Explorer
/// single/double-click preference. The cross-box move and persistence are coordinated through the
/// shared <see cref="MainViewModel"/> and the <see cref="RequestSave"/> callback.
/// </summary>
public partial class BoxControl : WidgetControlBase
{
    private Point _dragStart;
    private bool _dragging;
    private bool _moved;
    private Border? _dragOrigin;
    private long _dragEpoch;
    private bool _suppressDragUntilMouseUp;
    public bool IsDragging => _dragging || _suppressDragUntilMouseUp;
    private DropIndicatorAdorner? _dropAdorner;
    private int _insertIndex = -1;
    private DragGhostWindow? _ghost;

    // Vertical edge auto-scroll while an item drag hovers: DragOver only fires while the
    // mouse moves, so a timer drives the scroll (and the slot refresh) while it is held
    // still in the top/bottom edge zone. Vertical only, by design.
    private DispatcherTimer? _dragScrollTimer;
    private bool _dragScrollActive;
    // Last DragOver point in Scroll space. The tick MUST use this, never Mouse.GetPosition:
    // live queries inside the OLE modal drag loop proved unreliable (phantom top-zone hits
    // scrolling up out of nowhere, bottom zone never firing), while the DragOver coordinates
    // demonstrably track the cursor (the slot indicator is computed from them).
    private Point _lastDragPoint;
    private bool _hasDragPoint;
    // Hover grace: consecutive ticks inside the same zone direction before scrolling starts,
    // so grabbing a top-row item (or sweeping through an edge) doesn't yank the view.
    private int _zoneTicks;
    private double _lastZoneDir;
    // Copy-pill generation: guards the async-posted progress callbacks against out-of-order
    // delivery (a stale "show" must not resurrect the pill after its drop hid it).
    private int _copyingSeq;

    // Drag visual gap - collapsed source items (target gap is adorner-only, no placeholder).
    // Instance-scoped: a static list would pin one control's live visuals from another drag
    // (or leak them if a drag aborts), and cross-instance collapse/restore races.
    private readonly List<UIElement> _collapsedForDrag = new();
    private WrapPanel? _wrapPanelCache;

    // Selection / marquee state.
    private int _anchorIndex = -1;
    private int _focusedIndex = -1;
    private bool _marqueeActive;
    private Point _marqueeStart;
    private HashSet<BoxItemViewModel>? _marqueeBase;
    private BoxItemViewModel? _openWithTarget;
    private BoxItemViewModel? _deleteTarget;
    private Border? _openWithBorder;
    private bool _deleteParticlesPlayed;

    private readonly ItemRenameService _renameService = new();

    private IShellWatcherService? _shellWatcher;

    private static readonly bool _singleClick = ShellSettings.IsSingleClickToOpen();
    private static DateTime _lastOpenUtc;

    public BoxControl()
    {
        InitializeComponent();
        // Tab switches re-assign DataContext (UpdateBody) — re-resolve the icon size with it.
        DataContextChanged += (_, _) => RefreshIconSize();
        // Drag timer + visibility lifecycle come from WidgetControlBase (Suspend/Unloaded stop it).
    }

    public override bool IsSuspended { get; protected set; }

    protected override void InitializeCtrlsCore()
    {
        // One subscription per CONTROL, not per item: item view models are created and discarded as
        // boxes reload, and a per-item subscription on a process-wide service would pin every one
        // of them for the life of the app.
        try
        {
            var watcher = App.Services.GetService<IShellWatcherService>();
            watcher?.ItemUpdated += OnItemUpdated;
            _shellWatcher = watcher;
        }
        catch
        {
            // Without the watcher the icon simply stays as cached - no reason to fail the control.
        }
    }

    protected override void OnUnloadedCore()
    {
        // Torn down mid-drag (tab switch/close while hovering): never leave the scroll timer running.
        StopDragAutoScroll();

        if (_shellWatcher is not null)
        {
            _shellWatcher.ItemUpdated -= OnItemUpdated;
            _shellWatcher = null;
        }
    }

    protected override bool SuspendCore()
    {
        // A hidden control cannot be drag-scrolled; stop the timer (idempotent).
        StopDragAutoScroll();
        return true;
    }

    protected override bool ResumeCore()
    {
        // Nothing to restart: scrolling resumes on the next drag-over.
        return true;
    }

    protected override void ApplyTheme()
    {
        // No theme-aware visuals in this control today (base subscription is harmless).
    }

    protected override void CleanupForShutdownCore()
    {
        // Nothing to dispose beyond the drag timer (stopped in Suspend/Unloaded).
        StopDragAutoScroll();
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

    /// <summary>True while an external file drop is being copied in (binds the copying pill).</summary>
    public static readonly DependencyProperty IsCopyingFilesProperty = DependencyProperty.Register(
        nameof(IsCopyingFiles), typeof(bool), typeof(BoxControl), new PropertyMetadata(false));

    public bool IsCopyingFiles
    {
        get => (bool)GetValue(IsCopyingFilesProperty);
        private set => SetValue(IsCopyingFilesProperty, value);
    }

    /// <summary>Human-readable copy status (e.g. "Copying 3 of 12 — big.zip").</summary>
    public static readonly DependencyProperty CopyStatusTextProperty = DependencyProperty.Register(
        nameof(CopyStatusText), typeof(string), typeof(BoxControl), new PropertyMetadata(string.Empty));

    public string CopyStatusText
    {
        get => (string)GetValue(CopyStatusTextProperty);
        private set => SetValue(CopyStatusTextProperty, value);
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
        int resolved = Helpers.BoxIconSize.Resolve(Box?.Model);

        IconSize = System.Math.Clamp(resolved, 16, 128);

        // Push the size to the items so their icons are resolved sharp enough for it. The shell
        // only hands back 32/48 px, so a tile at 96 px was an upscale - and after a size change
        // the cached icon is the wrong resolution, so the items have to re-resolve.
        PushIconSizeToItems();
    }

    private void PushIconSizeToItems()
    {
        if (Box is not { } box)
        {
            return;
        }

        int size = Helpers.BoxIconSize.Resolve(Box?.Model);
        foreach (var item in box.Items)
        {
            item.IconSize = size;
        }
    }

    public MainViewModel? Host { get; set; }

    public IPersistenceService? Persistence { get; set; }

    public System.Action? RequestSave { get; set; }

    private void CollapseDraggedItems(List<BoxItemViewModel> dragged)
    {
        RestoreCollapsedItems();
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

    private void RestoreCollapsedItems()
    {
        foreach (var c in _collapsedForDrag)
        {
            c.Visibility = Visibility.Visible;
        }
        _collapsedForDrag.Clear();
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
        // Intentionally allocation-free: the previous detached-Border marker was never added to
        // the tree nor read anywhere, so constructing one per DragOver event was pure garbage.
    }

    private void HideGapPlaceholder()
    {
        // No panel modification and no marker state: the adorner owns the gap visual and is
        // cleared via RemoveDropIndicator. Kept (with ShowGapPlaceholder) so call sites and any
        // future real placeholder stay untouched.
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

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (_dragging)
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
                DragDrop.AddQueryContinueDragHandler(border, OnQueryContinueDrag);
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
                DragDrop.RemoveQueryContinueDragHandler(border, OnQueryContinueDrag);
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
                // The right-click that cancelled this drag is delivered as its own button-up after
                // DoDragDrop returns — to whichever box sits under the cursor, not necessarily this
                // one. Arm the process-wide guard so that release can only close the drag.
                if (Mouse.RightButton == MouseButtonState.Pressed)
                {
                    WindowDragController.SuppressContextMenuUntilRelease();
                }
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

    /// <summary>
    /// A right-click during an item drag cancels the drag, the same gesture Explorer has. This is
    /// the only callback that runs for that press: the OLE drag loop owns every mouse message
    /// while it is active, so the box is never handed a right-button event of its own mid-drag.
    /// </summary>
    /// <remarks>
    /// WPF's own default (in <c>OleDragSource</c>) already cancels when a second button goes down,
    /// but it does so silently, and the release that follows then reaches the box as an ordinary
    /// right-click — which used to reopen the item's shell menu right where the drag was dropped.
    /// Arming the shared guard here is what marks that release as "interrupt only".
    /// </remarks>
    private void OnQueryContinueDrag(object? sender, QueryContinueDragEventArgs e)
    {
        if ((e.KeyStates & DragDropKeyStates.RightMouseButton) == 0)
        {
            return;
        }

        WindowDragController.SuppressContextMenuUntilRelease();
        e.Action = DragAction.Cancel;
        e.Handled = true;
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

        // Ctrl+C / Ctrl+X / Ctrl+V are NOT handled here: BoxContainerWindow.Window_PreviewKeyDown
        // owns them for every body type. A Window tunnels PreviewKeyDown ahead of every focused
        // element, so handling them in both places would just be two implementations of the same
        // shortcut waiting to disagree.

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
                if (Box is { } box)
                {
                    var selected = box.Items.Where(i => i.IsSelected).ToList();
                    if (selected.Count > 0)
                    {
                        foreach (var it in selected) OpenItem(it);
                    }
                    else
                    {
                        OpenItem(vm);
                    }
                }
                else
                {
                    OpenItem(vm);
                }
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

    private static bool IsRecycleBin(Maktaby.Core.Models.BoxItem item)
    {
        if (item is null) return false;
        if (!string.IsNullOrEmpty(item.Pidl) && item.Pidl.Contains("645FF040", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(item.Path) && item.Path.Contains("645FF040", StringComparison.OrdinalIgnoreCase)) return true;
        return (item.DisplayName ?? "").IndexOf("Recycle", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsOpenable(Maktaby.Core.Models.BoxItem item)
    {
        if (item is null) return false;
        if (!string.IsNullOrEmpty(item.Pidl)) return true;
        if (string.IsNullOrEmpty(item.Path)) return false;
        if (Directory.Exists(item.Path)) return true;
        if (File.Exists(item.Path)) return true;
        return false;
    }

    private static void SetOpenHighlight(Border border, bool on)
    {
        try
        {
            var transform = border.RenderTransform as ScaleTransform;
            if (transform is null)
            {
                transform = new ScaleTransform(1, 1, border.ActualWidth / 2, border.ActualHeight / 2);
                border.RenderTransform = transform;
            }
            var to = on ? 1.2 : 1.0;
            var animX = new DoubleAnimation(transform.ScaleX, to, TimeSpan.FromMilliseconds(150));
            var animY = new DoubleAnimation(transform.ScaleY, to, TimeSpan.FromMilliseconds(150));
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
        catch { }
    }

    private static void SpawnDeleteParticles(Border border)
    {
        try
        {
            var layer = AdornerLayer.GetAdornerLayer(border);
            if (layer is null) return;
            var adorner = new ParticleAdorner(border, Colors.Red);
            layer.Add(adorner);
            adorner.Burst();
        }
        catch { }
    }

    private static void SpawnParticles(Border border)
    {
        try
        {
            var layer = AdornerLayer.GetAdornerLayer(border);
            if (layer is null) return;
            var adorner = new ParticleAdorner(border);
            layer.Add(adorner);
            adorner.Burst();
        }
        catch { }
    }

    private static void PlayOpenBonus(Border border)
    {
        try
        {
            var transform = border.RenderTransform as ScaleTransform;
            if (transform is null)
            {
                transform = new ScaleTransform(1, 1, border.ActualWidth / 2, border.ActualHeight / 2);
                border.RenderTransform = transform;
            }
            var upX = new DoubleAnimation(transform.ScaleX, 1.35, TimeSpan.FromMilliseconds(120));
            var upY = new DoubleAnimation(transform.ScaleY, 1.35, TimeSpan.FromMilliseconds(120));
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, upX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, upY);
            var downX = new DoubleAnimation(1.35, 1.0, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(120) };
            var downY = new DoubleAnimation(1.35, 1.0, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(120) };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, downX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, downY);
        }
        catch { }
    }

    private sealed class ParticleAdorner : Adorner
    {
        private readonly Canvas _canvas = new();
        private readonly List<Ellipse> _particles = new();
        private readonly Color _color;

        public ParticleAdorner(UIElement adorned, Color color = default) : base(adorned)
        {
            _color = color == default ? Colors.Gold : color;
            IsHitTestVisible = false;
            AddVisualChild(_canvas);
            AddLogicalChild(_canvas);
        }

        public void Burst()
        {
            _canvas.Children.Clear();
            _particles.Clear();
            double w = AdornedElement.RenderSize.Width;
            double h = AdornedElement.RenderSize.Height;
            var center = new Point(w / 2, h / 2);
            for (int i = 0; i < 8; i++)
            {
                var e = new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(_color), Opacity = 1 };
                Canvas.SetLeft(e, center.X);
                Canvas.SetTop(e, center.Y);
                _canvas.Children.Add(e);
                _particles.Add(e);

                double angle = i * Math.PI * 2 / 8;
                double dx = Math.Cos(angle) * 28;
                double dy = Math.Sin(angle) * 28;
                var ax = new DoubleAnimation(0, dx, TimeSpan.FromMilliseconds(350));
                var ay = new DoubleAnimation(0, dy, TimeSpan.FromMilliseconds(350));
                var ao = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
                var t = new TranslateTransform();
                e.RenderTransform = t;
                t.BeginAnimation(TranslateTransform.XProperty, ax);
                t.BeginAnimation(TranslateTransform.YProperty, ay);
                e.BeginAnimation(UIElement.OpacityProperty, ao);
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var layer = AdornerLayer.GetAdornerLayer(AdornedElement);
                layer?.Remove(this);
            };
            timer.Start();
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _canvas;
        protected override Size ArrangeOverride(Size finalSize)
        {
            _canvas.Arrange(new Rect(finalSize));
            return finalSize;
        }
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

    private static void OpenWith(BoxItemViewModel target, IEnumerable<string>? sourcePaths = null)
    {
        var path = target?.Model?.Path;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (sourcePaths is not null)
            {
                var args = string.Join(" ", sourcePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => $"\"{p}\""));
                if (!string.IsNullOrWhiteSpace(args))
                {
                    psi.Arguments = args;
                }
            }
            using (Process.Start(psi)) { }
        }
        catch
        {
            // Best-effort; ignore failures.
        }
    }

    private static void OpenItem(BoxItemViewModel item)
    {
        _lastOpenUtc = DateTime.UtcNow;
        item.IsSelected = false;
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
            if (Directory.Exists(path))
            {
                using (Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })) { }
            }
            else if (File.Exists(path))
            {
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
            }
            else if (path.Contains('!') && path.Contains('_'))
            {
                // AppUserModelID fallback (UWP/Store app) when no PIDL was resolved.
                using (Process.Start(new ProcessStartInfo("explorer.exe", "shell:appsFolder\\" + path) { UseShellExecute = true })) { }
            }
            else
            {
                using (Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true })) { }
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
            StopDragAutoScroll();
            RemoveDropIndicator();
            e.Effects = DragDropEffects.Move;
            return;
        }

        if ((e.Data.GetDataPresent(DndFormats.BoxItems) || e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("Shell IDList Array")) && DataContext is BoxViewModel)
        {
            HideGapPlaceholder();
            GetWrapPanel()?.UpdateLayout();
            _lastDragPoint = e.GetPosition(Scroll);
            _hasDragPoint = true;
            RefreshDropIndicatorForPoint(_lastDragPoint);
            StartDragAutoScroll();

            // "Open with" hotspot: a centered square (half the icon) of an openable item.
            var previousBorder = _openWithBorder;
            var previousOpenWith = _openWithTarget;
            _openWithTarget = null;
            _openWithBorder = null;
            bool hotspotHit = false;
            if (e.OriginalSource is DependencyObject src)
            {
                var border = FindItemBorder(src);
                if (border?.DataContext is BoxItemViewModel vm && (IsOpenable(vm.Model) || IsRecycleBin(vm.Model)))
                {
                    var pos = e.GetPosition(border);
                    double w = border.ActualWidth;
                    double h = border.ActualHeight;
                    double size = Math.Min(w, h) / 2;
                    double left = (w - size) / 2;
                    double top = (h - size) / 2;
                    if (pos.X >= left && pos.X <= left + size && pos.Y >= top && pos.Y <= top + size)
                    {
                        hotspotHit = true;
                        RemoveDropIndicator();
                        if (IsRecycleBin(vm.Model))
                        {
                            _deleteTarget = vm;
                            _openWithTarget = null;
                            _openWithBorder = border;
                            if (!_deleteParticlesPlayed)
                            {
                                SetOpenHighlight(border, true);
                                SpawnDeleteParticles(border);
                                _deleteParticlesPlayed = true;
                            }
                        }
                        else
                        {
                            _openWithTarget = vm;
                            _openWithBorder = border;
                            if (!ReferenceEquals(previousOpenWith, vm))
                            {
                                SetOpenHighlight(border, true);
                                SpawnParticles(border);
                            }
                        }
                    }
                }
            }
            if (!hotspotHit && previousBorder is not null)
            {
                SetOpenHighlight(previousBorder, false);
                _deleteParticlesPlayed = false;
            }

            e.Effects = e.Data.GetDataPresent(DndFormats.BoxItems) ? DragDropEffects.Move : DropHelper.GetEffect(e);
            e.Handled = true;
            return;
        }

        StopDragAutoScroll();
        RemoveDropIndicator();
        _insertIndex = -1;
        e.Effects = DropHelper.GetEffect(e);
        e.Handled = true;
    }

    /// <summary>Recomputes the insert slot for a point in <see cref="Scroll"/> space and moves
    /// the indicator only when the slot (or its existence) changed. Shared by DragOver and
    /// the auto-scroll timer: with the timer scrolling content under a stationary cursor,
    /// the slot must refresh without waiting for the next mouse move.</summary>
    /// <summary>
    /// True when the dragged box items belong to <em>this</em> box - i.e. the drop would be a
    /// reorder of the list on screen rather than a move in from elsewhere.
    /// </summary>
    /// <remarks>
    /// The source has to be resolved through <see cref="MainViewModel.FindBoxContaining"/> rather than
    /// compared to the drop target: this control's <c>Box</c> <em>is</em> its DataContext, and so is
    /// <c>targetBox</c>, so comparing the two says "same box" about every drop that ever arrives.
    /// Only box-internal drags carry <see cref="DndFormats.BoxItems"/>; a file drop from elsewhere is
    /// never a reorder of this list and is always allowed to append.
    /// </remarks>
    private bool DragComesFromThisBox(DragEventArgs e)
    {
        if (Box is not { } box || !e.Data.GetDataPresent(DndFormats.BoxItems))
        {
            return false;
        }

        if (e.Data.GetData(DndFormats.BoxItems) is not List<BoxItemViewModel> items || items.Count == 0)
        {
            return false;
        }

        return ReferenceEquals(Host?.FindBoxContaining(items[0]), box);
    }

    private void RefreshDropIndicatorForPoint(Point pt)
    {
        if (Box?.IsFilterActive == true)
        {
            // No slot to promise. While a filter hides part of the list, the tile the pointer sits
            // between is not the item the computed index would land between, so a visible insertion
            // line would be a position we cannot honour. GetDropPlacement below decides what the
            // drop actually does (append cross-box, refuse in-box).
            if (_insertIndex != -1)
            {
                _insertIndex = -1;
                RemoveDropIndicator();
            }

            return;
        }

        int newIdx = GetInsertIndex(pt);
        // Same slot: geometry depends only on the index, so the existing visual is already
        // correct — rebuilding it per event only wastes layout/render passes.
        if (newIdx != _insertIndex || _dropAdorner == null)
        {
            _insertIndex = newIdx;
            ShowDropIndicator(_insertIndex);
        }
    }

    /// <summary>Starts the edge auto-scroll timer (idempotent). Ticks scroll while the cursor
    /// is held in the top/bottom edge zone, even with the mouse perfectly still.</summary>
    private void StartDragAutoScroll()
    {
        if (_dragScrollTimer is null)
        {
            _dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _dragScrollTimer.Tick += DragScrollTick;
        }
        _dragScrollActive = true;
        if (!_dragScrollTimer.IsEnabled) _dragScrollTimer.Start();
    }

    private void StopDragAutoScroll()
    {
        _dragScrollActive = false;
        _hasDragPoint = false;
        _zoneTicks = 0;
        _lastZoneDir = 0;
        try { _dragScrollTimer?.Stop(); } catch { }
    }

    /// <summary>Vertical edge auto-scroll tick. Runs on the cached DragOver point (see the
    /// field note) and requires a ~150 ms continuous hover before moving, then ramps speed
    /// with proximity to the viewport edge. The slot refreshes after every scroll so the
    /// indicator tracks the content sliding under the cursor.</summary>
    private void DragScrollTick(object? sender, EventArgs e)
    {
        if (!_dragScrollActive || !_hasDragPoint) return;
        double viewport = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : Scroll.ActualHeight;
        if (viewport <= 0 || Scroll.ScrollableHeight <= 0) return;

        Point pt = _lastDragPoint;
        const double zone = 32;
        double dir = 0;
        if (pt.Y < zone) dir = -1;
        else if (pt.Y > viewport - zone) dir = 1;
        if (dir == 0)
        {
            _zoneTicks = 0;
            _lastZoneDir = 0;
            return; // inside: nothing to scroll (slot already matches the stationary mouse)
        }
        if (dir != _lastZoneDir)
        {
            _lastZoneDir = dir;
            _zoneTicks = 0;
        }
        if (++_zoneTicks < 3) return; // hover grace: ~150 ms before the view moves

        // 0 at the viewport edge (mouse may even be slightly outside) → full speed.
        double edgeDist = dir < 0 ? pt.Y : viewport - pt.Y;
        double proximity = Math.Clamp(1 - edgeDist / zone, 0, 1);
        double step = 12 + 60 * proximity; // px per 50 ms tick: ~240/s gentle, ~1440/s fast
        double target = Math.Clamp(Scroll.VerticalOffset + dir * step, 0, Scroll.ScrollableHeight);
        if (target == Scroll.VerticalOffset) return; // already at the end
        Scroll.ScrollToVerticalOffset(target);
        // Flush pending layout before recomputing the slot: container transforms queried
        // against a stale tree place the indicator a step behind the slid content (i.e.
        // misplaced exactly while scrolling, correct when stopped). Same guarantee DragOver
        // relies on; a no-op when the tree is already clean.
        GetWrapPanel()?.UpdateLayout();
        RefreshDropIndicatorForPoint(_lastDragPoint);
    }

    private void BoxControl_DragLeave(object sender, DragEventArgs e)
    {
        StopDragAutoScroll();
        RemoveDropIndicator();
        _insertIndex = -1;
    }

    private async void BoxControl_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DndFormats.SourceContainer))
        {
            // Handled by the owning BoxContainerWindow's Drop handler.
            StopDragAutoScroll();
            RemoveDropIndicator();
            return;
        }

        if (DataContext is not BoxViewModel targetBox)
        {
            StopDragAutoScroll();
            return;
        }

        // Recycle-bin hotspot: delete (move to recycle bin) the dragged items.
        if (_deleteTarget is { } deleteTarget)
        {
            _deleteTarget = null;
            _openWithBorder = null;
            _deleteParticlesPlayed = false;

            var sources = new List<string>();
            if (e.Data.GetData(DndFormats.BoxItems) is List<BoxItemViewModel> boxItems)
            {
                sources.AddRange(boxItems.Select(i => i.Model?.Path).Where(p => !string.IsNullOrWhiteSpace(p))!);
            }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                sources.AddRange(files);
            }

            if (sources.Count > 0)
            {
                try
                {
                    var fileOps = App.Services.GetRequiredService<IFileOperationService>();
                    _ = fileOps.DeleteAsync(sources, permanent: false);
                }
                catch { }
            }

            if (_openWithBorder is { } b) PlayOpenBonus(b);
            StopDragAutoScroll();
            RemoveDropIndicator();
            _insertIndex = -1;
            e.Handled = true;
            return;
        }

        // "Open with" hotspot: open the target item instead of dropping/moving.
        if (_openWithTarget is { } openTarget)
        {
            _openWithTarget = null;

            var sources = new List<string>();
            if (e.Data.GetData(DndFormats.BoxItems) is List<BoxItemViewModel> boxItems)
            {
                sources.AddRange(boxItems.Select(i => i.Model?.Path).Where(p => !string.IsNullOrWhiteSpace(p))!);
            }
            else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                sources.AddRange(files);
            }

            OpenWith(openTarget, sources);
            if (_openWithBorder is { } b)
            {
                PlayOpenBonus(b);
            }
            StopDragAutoScroll();
            RemoveDropIndicator();
            _insertIndex = -1;
            e.Handled = true;
            return;
        }

        // Where the drop is allowed to land. Under a filter the visible tiles are a subset of the list,
        // so a visible-space slot maps to the wrong real position - the tiles the user is aiming
        // between are not neighbours in the collection. Rather than reorder by a lie:
        // a drop from another box appends (no slot claimed), while a drop of THIS box's own items is
        // refused outright, because "reorder" inside a filtered view has no meaning the user can
        // see. Refusing is safe precisely because it happens before AddToBoxAsync, which is what
        // would otherwise remove the dragged items from their source.
        bool filtered = targetBox.IsFilterActive;
        int insertIndex = filtered ? -1 : _insertIndex;

        if (filtered && DragComesFromThisBox(e))
        {
            StopDragAutoScroll();
            RemoveDropIndicator();
            _insertIndex = -1;
            return;
        }

        // External file drops copy on a background thread with no other feedback: report
        // per-file progress into the copying pill (first report shows it, completion hides it).
        // Internal Box-item moves are instant — no indicator, no flash.
        // Progress<T> posts callbacks asynchronously: for a fast drop (e.g. a Start Menu .lnk)
        // a "show" post can land AFTER the finally below hid the pill, sticking it on. The
        // sequence guard drops stale posts and lets only the owning drop hide.
        int copySeq = ++_copyingSeq;
        IProgress<DropHelper.DropProgress>? progress = null;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("Shell IDList Array"))
        {
            progress = new Progress<DropHelper.DropProgress>(p =>
            {
                if (copySeq != _copyingSeq) return;
                IsCopyingFiles = true;
                CopyStatusText = p.Total > 1
                    ? $"Copying {p.Processed} of {p.Total} — {p.CurrentName}"
                    : $"Copying {p.CurrentName}…";
            });
        }

        List<Maktaby.Core.Models.BoxItem>? moved;
        try
        {
            moved = await DropHelper.AddToBoxAsync(targetBox, Host, e, insertIndex, progress);
        }
        finally
        {
            if (copySeq == _copyingSeq)
            {
                _copyingSeq++;
                IsCopyingFiles = false;
            }
        }
        StopDragAutoScroll();
        RemoveDropIndicator();
        _insertIndex = -1;

        // Re-select the items that were just dropped into this box.
        if (moved != null && moved.Count > 0 && Box is { } box)
        {
            // The arrivals are new item instances whose IsFilteredOut is default, so an active
            // filter must be re-applied or they would show regardless of it.
            box.SetFilter(box.FilterText);

            // And if the filter would hide any of them, the drop reads as "nothing arrived".
            // Keep the search the user is in and reveal just the arrivals - they are recognisable
            // as what was just added, and a cleared search box is worse feedback than a few extra
            // rows. The next keystroke drops the exemption.
            if (filtered)
            {
                box.RevealItems(moved);
            }

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
            if (firstVm is { IsFilteredOut: false })
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
        // Clicking the scrollbar must not clear the selection.
        if (e.OriginalSource is DependencyObject s && IsDescendantOfScrollBar(s))
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

    /// <summary>
    /// Copies (Ctrl+C) or cuts (Ctrl+X) the selected items onto the clipboard.
    /// </summary>
    /// <remarks>
    /// Nothing is moved or removed here. A cut is a real file operation and its originals can only
    /// be deleted once the paste has produced the copies, so the clipboard is the whole of the
    /// cut until then - see <see cref="BoxItemClipboardOps"/>.
    /// </remarks>
    public bool CopySelectedItems(bool cut)
    {
        if (Box is not { } box) { return false; }

        var selected = box.Items.Where(i => i.IsSelected).Select(i => i.Model).ToList();
        if (selected.Count == 0) { return false; }

        return BoxItemClipboardOps.SetClipboard(selected, cut);
    }

    /// <summary>
    /// Pastes box items from the clipboard into this box (Ctrl+V). Returns how many landed.
    /// </summary>
    /// <remarks>
    /// Uses the same "copying n of m" pill as a file drop, and the same generation counter: pasting
    /// files is as slow as dropping them, and the sequence guard is what stops a late callback
    /// from a previous operation leaving the pill stuck on.
    /// </remarks>
    public async Task<int> PasteFromClipboard()
    {
        if (Box is not { } box || Host is not { } host) { return 0; }

        int pasteSeq = ++_copyingSeq;
        var progress = new Progress<DropHelper.DropProgress>(p =>
        {
            if (pasteSeq != _copyingSeq) { return; }
            IsCopyingFiles = true;
            CopyStatusText = p.Total > 1
                ? $"Pasting {p.Processed} of {p.Total} - {p.CurrentName}"
                : $"Pasting {p.CurrentName}";
        });

        IReadOnlyList<Maktaby.Core.Models.BoxItem> added;
        try
        {
            added = await BoxItemClipboardOps.PasteAsync(box, host, RequestSave, progress);
        }
        finally
        {
            if (pasteSeq == _copyingSeq)
            {
                _copyingSeq++;
                IsCopyingFiles = false;
            }
        }

        if (added.Count == 0) { return 0; }

        SelectPastedItems(added);
        return added.Count;
    }

    /// <summary>
    /// Selects and focuses the items a paste just delivered, so the keyboard carries on from them
    /// and the result is visible instead of appearing silently at the end of the flow.
    /// </summary>
    private void SelectPastedItems(IReadOnlyList<Maktaby.Core.Models.BoxItem> added)
    {
        if (Box is not { } box || added.Count == 0) { return; }

        foreach (var vm in box.Items)
        {
            if (added.Any(a => a == vm.Model))
            {
                vm.IsSelected = true;
            }
        }

        var first = box.Items.FirstOrDefault(i => i.Model == added[0]);
        if (first is null) { return; }

        _focusedIndex = box.Items.IndexOf(first);
        _anchorIndex = _focusedIndex;
        _ = Dispatcher.BeginInvoke(
            () => FocusItem(_focusedIndex),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>True while an item's inline rename editor is open.</summary>
    public bool HasRenamingItem => Box?.Items.Any(i => i.IsEditing) == true;

    /// <summary>
    /// Focuses the first item the filter lets through, for "type, press Enter, land on a result".
    /// </summary>
    public void FocusFirstVisibleItem()
    {
        if (Box is not { } box) { return; }

        var first = box.Items.FirstOrDefault(i => !i.IsFilteredOut);
        if (first is null) { return; }

        SelectOnly(first);
        _focusedIndex = box.Items.IndexOf(first);
        _anchorIndex = _focusedIndex;
        FocusItem(_focusedIndex);
    }

    /// <summary>
    /// Re-reads every icon from the shell, bypassing the cache - the F5 path.
    /// </summary>
    /// <remarks>
    /// Goes through the cache-invalidating path rather than <see cref="BoxItemViewModel.ReloadIcon"/>,
    /// which would re-resolve and get the same cached bitmap back. Useful when the shell's icon theme
    /// or a file's association changed underneath us.
    /// </remarks>
    public void ReloadAllIcons()
    {
        if (Box is not { } box) { return; }

        foreach (var item in box.Items)
        {
            item.ReloadIconBypassingCache();
        }
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

    private static bool IsDescendantOfScrollBar(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is ScrollBar) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void DisarmDrag(string reason)
    {
        _dragOrigin = null;
        _dragging = false;
        _moved = false;
        // Consumed by the gesture that disarms: this is a one-shot "the click that follows a drag
        // may only interrupt" marker, and every right-click path calls through here. Clearing it
        // here also guarantees it can never wedge, e.g. when the release that should have cleared
        // it was delivered to another application.
        _suppressDragUntilMouseUp = false;
        WindowDragController.InvalidateItemDrags();
    }

    /// <summary>
    /// Press half of the right-click: while a drag owns the gesture - or while the release that
    /// ended one is still pending - swallow it as well, so the click cannot arm a menu at all.
    /// </summary>
    private void BoxControl_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _marqueeActive || _suppressDragUntilMouseUp || WindowDragController.IsContextMenuSuppressed)
        {
            DisarmDrag("right-click-cancel");
            e.Handled = true;
        }
    }

    /// <summary>
    /// Right-click on bare box surface opens the paste menu. Right-click ON an item is left alone:
    /// that path shows the native shell menu (see <see cref="ItemBorder_MouseRightButtonUp"/>), and
    /// stealing it would remove Cut/Copy/Delete/Properties from every item.
    /// </summary>
    private void BoxControl_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // While dragging - or while the release that ended a drag is still pending - right-click
        // only cancels the drag; it never opens a menu. The shared guard covers the cross-window
        // case: the release belongs to a drag that may have started in another box.
        if (_dragging || _marqueeActive || _suppressDragUntilMouseUp || WindowDragController.IsContextMenuSuppressed)
        {
            DisarmDrag("right-click-cancel");
            e.Handled = true;
            return;
        }
        if (Box is not { } box || Host is null) { return; }
        if (IsOverItem(e.OriginalSource as DependencyObject)) { return; }

        var menu = (ContextMenu?)Resources["BoxEmptySpaceMenu"];
        if (menu is null) { return; }

        // The item that owns the generated name lives inside the ContextMenu itself, so it is
        // reached through the item container rather than as a field on this class.
        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Enables Paste only when the clipboard can actually supply something: Maktaby's own payload,
    /// or files from another application. Read at open time - the clipboard changes without the
    /// menu knowing.
    /// </summary>
    private void BoxEmptySpaceMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) { return; }

        var item = menu.Items.OfType<MenuItem>().FirstOrDefault();
        if (item is null) { return; }

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

    private async void EmptySpacePaste_Click(object sender, RoutedEventArgs e)
    {
        // PasteFromClipboard drives the copying pill and selects what arrived, so the menu entry
        // needs nothing beyond the call.
        await PasteFromClipboard();
    }

    /// <summary>
    /// True when <paramref name="source"/> sits inside one of the item templates, i.e. the
    /// right-click belonged to an item rather than to the empty surface behind them.
    /// </summary>
    private static bool IsOverItem(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: BoxItemViewModel }) { return true; }

            if (current is Visual or System.Windows.Media.Media3D.Visual3D) { continue; }
            if (current is not FrameworkElement) { break; }
        }

        return false;
    }

    /// <summary>
    /// An existing item's shell appearance changed - the Recycle Bin's icon is recomputed from the
    /// bin's contents, so it changes while the item itself stays identical. Re-read it.
    /// </summary>
    /// <remarks>
    /// Matching is by identity through <see cref="BoxItem.RefersToSame"/>, not by asking whether the
    /// PIDL "looks like" the bin: the bin is normally a PATH item here (a drop resolves it through
    /// <c>GetPathFromPidl</c> first, so its PIDL is empty), and an earlier PIDL-comparison approach
    /// therefore never matched anything. Dispatched because the shell notification arrives on the
    /// watcher's own window procedure, not the UI thread.
    /// </remarks>
    private void OnItemUpdated(BoxItem updated)
    {
        void Reload()
        {
            if (Box is not { } box) { return; }

            foreach (var vm in box.Items)
            {
                if (Matches(vm.Model, updated))
                {
                    vm.ReloadIconBypassingCache();
                }
            }
        }

        var dispatcher = Dispatcher;
        if (dispatcher.CheckAccess())
        {
            Reload();
        }
        else
        {
            _ = dispatcher.BeginInvoke(Reload);
        }
    }

    /// <summary>
    /// True when <paramref name="model"/> is the item the shell reported as updated.
    /// </summary>
    /// <remarks>
    /// <see cref="BoxItem.RefersToSame"/> alone is not enough, and that is what broke the first
    /// attempt. Measured on this machine: the Recycle Bin answers <c>SHGetPathFromIDListW</c> with a
    /// failure, so its stored item has <b>no path and only a PIDL</b>; while the shell-resolved item
    /// handed to <c>ItemUpdated</c> carries <b>no PIDL</b> and a path holding its namespace identity
    /// (<c>::{645FF040-...}</c>). RefersToSame skips both comparisons - one side is missing a path,
    /// the other a PIDL - so it always answers false.
    /// <para>
    /// The fallback therefore compares shell namespace identity, which both sides can produce. It
    /// costs one shell lookup per PIDL-backed item, on a rare event, and there are only ever a
    /// handful of those.
    /// </para>
    /// </remarks>
    private static bool Matches(BoxItem model, BoxItem updated)
    {
        if (BoxItem.RefersToSame(model, updated)) { return true; }

        if (!string.IsNullOrEmpty(model.Path) || string.IsNullOrEmpty(model.Pidl)) { return false; }

        var parse = Win32Apis.GetPidlParseName(model.Pidl);
        return !string.IsNullOrEmpty(parse) &&
               string.Equals(parse, updated.Path, StringComparison.OrdinalIgnoreCase);
    }

    private void ItemBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _marqueeActive || _suppressDragUntilMouseUp || WindowDragController.IsContextMenuSuppressed)
        {
            DisarmDrag("right-click-cancel");
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
        if (_dragging || _marqueeActive || _suppressDragUntilMouseUp || WindowDragController.IsContextMenuSuppressed)
        {
            DisarmDrag("right-click-cancel");
            e.Handled = true;
            return;
        }
        DisarmDrag("context-menu");

        // The menu is modal over the tile, so a tooltip still up would float above it or hang around
        // after it closes.
        Helpers.ToolTipGuard.Close();
        e.Handled = true;
    }
}
