using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Window for a <see cref="DesktopItemContainerType.BoxContainer"/>: shows the active tab's
/// <see cref="BoxControl"/>, a tab strip (only when more than one tab exists), and a header whose
/// title/menu operate on the selected box. Geometry and styling come from the <see cref="ContainerViewModel"/>
/// (i.e. the owning <see cref="DesktopItemContainer"/>). Movement/resize is delegated to
/// <see cref="WindowDragController"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class BoxContainerWindow : Window
{
    private readonly ContainerViewModel _vm;
    private readonly MainViewModel _host;
    private readonly IWindowPositioningService _positioning;
    private readonly Action _save;
    private readonly WindowDragController _drag;
    private readonly IMouseMonitor _mouseMonitor;
    private readonly IZOrderService _zOrder = App.Services.GetRequiredService<IZOrderService>();
    private readonly uint _currentProcessId = (uint)System.Environment.ProcessId;

    // Chrome (header buttons, tab strip, scrollbar) is shown only when the container is hovered or focused.
    private bool _mouseOver;
    private bool _keyboardFocused;
    private bool _isRenaming;

    static BoxContainerWindow()
    {
        // Commit any active inline rename when a mouse button is pressed anywhere in the app
        // (covers clicks on the desktop, other containers, etc., where LostFocus may not fire).
        EventManager.RegisterClassHandler(
            typeof(Window),
            PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnAnyPreviewMouseDown));
    }

    private static void OnAnyPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Application.Current == null)
        {
            return;
        }

        foreach (var w in Application.Current.Windows.OfType<BoxContainerWindow>())
        {
            w.TryCommitRename(e.OriginalSource);
        }
    }

    private void TryCommitRename(object? originalSource)
    {
        if (!_isRenaming)
        {
            return;
        }

        if (originalSource is DependencyObject d && (d == TitleEdit || TitleEdit.IsAncestorOf(d)))
        {
            return; // clicks inside the editing box should not commit
        }

        CommitRename();
    }

    public BoxContainerWindow(ContainerViewModel vm, MainViewModel host, IWindowPositioningService positioning, Action save)
    {
        InitializeComponent();

        _vm = vm;
        _host = host;
        _positioning = positioning;
        _save = save;

        _mouseMonitor = App.Services.GetRequiredService<IMouseMonitor>();
        _mouseMonitor.MouseButtonDown += OnGlobalMouseDown;

        var monitor = App.Services.GetRequiredService<IMonitorService>();
        var dpi = App.Services.GetRequiredService<IDpiService>();
        var snapping = App.Services.GetRequiredService<IWindowSnappingService>();

        DataContext = vm;

        AllowDrop = true;
        DragEnter += Window_DragEnter;
        DragOver += Window_DragOver;
        DragLeave += Window_DragLeave;
        Drop += Window_Drop;

        ApplyTransparency();

        Left = vm.Left;
        Top = vm.Top;
        Width = vm.Width;
        Height = vm.Height;

        _drag = new WindowDragController(
            this,
            monitor,
            dpi,
            snapping,
            _positioning,
            () => RectD.FromXYWH(_vm.Left, _vm.Top, _vm.Width, _vm.Height),
            r =>
            {
                _vm.Left = r.X;
                _vm.Top = r.Y;
                _vm.Width = r.Width;
                _vm.Height = r.Height;
            },
            () => _host.Containers.Where(c => c.Id != _vm.Id).Select(c => c.Bounds).ToList(),
            _save,
            () => HeaderBorder.ActualHeight);

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyType();
        UpdateBody();
        if (_vm.BoxContainerVm != null)
        {
            _vm.BoxContainerVm.SelectedIndexChanged += () => UpdateBody();
            _vm.BoxContainerVm.PropertyChanged += OnBoxContainerVmPropertyChanged;
        }

        UpdateChrome();
        _drag.Attach();
    }

    private void OnBoxContainerVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoxContainerViewModel.ShowTabs))
        {
            UpdateChrome();
        }
    }

    /// <summary>Adjusts which parts of the window are visible based on the container type.</summary>
    private void ApplyType()
    {
        bool isBox = _vm.BoxContainerVm != null;
        AddRemoveButtons.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        BoxContent.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = isBox ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome();
    }

    /// <summary>
    /// Shows the header buttons, tab strip and scrollbar only while the container is hovered or focused;
    /// otherwise only the title (and content) remain, giving a clean desktop look.
    /// </summary>
    private void UpdateChrome()
    {
        bool isBox = _vm.BoxContainerVm != null;
        bool show = isBox && (_mouseOver || _keyboardFocused);

        MenuButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AddRemoveButtons.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // The tab strip is always visible when there is more than one tab; the header buttons and
        // scrollbar stay hidden until the container is hovered or focused.
        TabStrip.Visibility = isBox && _vm.BoxContainerVm!.ShowTabs ? Visibility.Visible : Visibility.Collapsed;

        BoxContent.VerticalScrollBarVisibility = show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _mouseOver = true;
        UpdateChrome();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _mouseOver = false;
        UpdateChrome();
    }

    private void Window_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _keyboardFocused = (bool)e.NewValue;
        UpdateChrome();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isRenaming)
        {
            CommitRename();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _mouseMonitor.MouseButtonDown -= OnGlobalMouseDown;
        _drag.Detach();
    }

    /// <summary>Collapses the chrome when a mouse button is pressed on a window outside this application
    /// (the bare desktop, another app) — a desktop-glued window may not register that as a focus loss.</summary>
    private void OnGlobalMouseDown(object? sender, IntPtr hwnd)
    {
        if (!IsLoaded || hwnd == IntPtr.Zero)
        {
            return;
        }

        Win32Apis.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _currentProcessId)
        {
            return; // a window of our own app; natural focus handling covers it
        }

        _keyboardFocused = false;
        _mouseOver = false;
        if (_isRenaming)
        {
            CommitRename();
        }

        UpdateChrome();
    }

    private void ApplyTransparency()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        double? global = settings.GetValue<double>(SettingsKeys.DefaultBoxTransparency);
        double? effective = _vm.Transparency ?? global;
        var opacity = System.Math.Clamp(1.0 - (effective ?? SettingsKeys.DefaultBoxTransparencyValue), 0.0, 1.0);
        RootBorder.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)) { Opacity = opacity };
        HeaderBorder.Background = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)) { Opacity = opacity };
        TabStrip.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x37)) { Opacity = opacity };
    }

    private void UpdateBody()
    {
        if (_vm.ActiveBox != null)
        {
            BoxContent.DataContext = _vm.ActiveBox;
            BoxContent.RequestSave = _save;
            BoxContent.Host = _host;
        }
    }

    private void TitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _drag.BeginTitleDrag(e);

    private void TitleArea_MouseMove(object sender, MouseEventArgs e)
    {
        _drag.TitleDrag(e);
        if (_drag.IsDragging)
        {
            UpdateMergeTargetDuringDrag();
        }
    }

    private void TitleArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasDragging = _drag.IsDragging;
        _drag.EndTitleDrag(e);
        if (wasDragging)
        {
            CompleteMergeIfAny();
        }
        else
        {
            _mergeTarget = null;
        }
    }

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        _vm.AddTabCommand.Execute(null);
        UpdateBody();
    }

    private void RemoveTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveBox is { Items.Count: > 0 })
        {
            var result = MessageBox.Show(
                "This box contains items. Delete it anyway?",
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _vm.RemoveTabCommand.Execute(null);
        UpdateBody();
    }

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _vm.ActiveBox != null)
        {
            _isRenaming = true;
            TitleEdit.Text = _vm.ActiveBox.Name;
            TitleText.Visibility = Visibility.Collapsed;
            TitleEdit.Visibility = Visibility.Visible;
            TitleEdit.Focus();
            TitleEdit.SelectAll();
        }
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
        }
        else if (e.Key == Key.Escape)
        {
            _isRenaming = false;
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }
    }

    private void CommitRename()
    {
        if (!_isRenaming)
        {
            return;
        }

        _isRenaming = false;

        if (_vm.ActiveBox != null)
        {
            _vm.ActiveBox.Name = TitleEdit.Text;
        }

        TitleText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();

        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        _save();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        BoxMenu.PlacementTarget = MenuButton;
        BoxMenu.IsOpen = true;
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Any(t => t.Items.Count > 0))
        {
            var result = MessageBox.Show(
                "This container contains items. Delete it anyway?",
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _host.RemoveContainer(_vm);
        _save();
        Close();
    }

    private void MenuHide_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsVisible = false;
        _save();
        Close();
    }

    private void MenuLock_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsLocked = !_vm.IsLocked;
        _save();
    }

    // --- Tab drag (custom mouse-driven; no OLE, so the OS "no-drop" cursor never appears) ---

    private BoxViewModel? _dragTab;
    private bool _tabDragging;
    private Point _dragStart;
    private Point _lastDragPoint;
    private BoxContainerWindow? _dropWindow;    // container currently under the cursor (owns the drop visuals)
    private int _tabDropIndex = -1;             // insertion index within _dropWindow
    private ContainerViewModel? _mergeTarget;
    private Brush? _origBorderBrush;
    private Thickness _origBorderThickness;
    private bool _tabStripTempShown;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: BoxViewModel box })
        {
            _dragTab = box;
            _tabDragging = false;
            _dragStart = e.GetPosition(this);
        }
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null)
        {
            return;
        }

        if (!_tabDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            if ((e.GetPosition(this) - _dragStart).Length < 4)
            {
                return;
            }

            _tabDragging = true;
            Mouse.OverrideCursor = Cursors.SizeAll;
            ((UIElement)sender).CaptureMouse();
        }

        // GetPosition(null) under capture is window-relative; convert to true screen coordinates
        // so the live-bounds hit-test (FindContainerWindowAt) and the indicator maths line up.
        _lastDragPoint = this.PointToScreen(e.GetPosition(this));
        UpdateTabDropTarget(_lastDragPoint);
    }

    private void Tab_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragTab == null)
        {
            return;
        }

        if (_tabDragging)
        {
            // Recompute from the release point so a stale last-move position can't drive the drop.
            _lastDragPoint = this.PointToScreen(e.GetPosition(this));
            UpdateTabDropTarget(_lastDragPoint);
            ((UIElement)sender).ReleaseMouseCapture();
            Mouse.OverrideCursor = null;
            PerformTabDrop();
            ClearTabDropVisuals();
        }
        else if (_vm.BoxContainerVm != null)
        {
            // A plain click (no drag) selects the tab.
            int idx = _vm.BoxContainerVm.Tabs.IndexOf(_dragTab);
            if (idx >= 0)
            {
                _vm.BoxContainerVm.SelectedIndex = idx;
                UpdateBody();
            }
        }

        _dragTab = null;
        _tabDragging = false;
    }

    /// <summary>
    /// Resolves and renders the drop target for the current cursor position. The target window owns
    /// the visual feedback (vertical insertion indicator + a temporarily-revealed tab strip), so the
    /// same logic drives both reordering within this container and merging into another.
    /// </summary>
    private void UpdateTabDropTarget(Point p)
    {
        var target = FindContainerWindowAt(p);

        if (target != _dropWindow)
        {
            // Cursor left the previous target: clear its indicator and restore its tab-strip visibility.
            _dropWindow?.HideTabDropIndicator();
            _dropWindow?.RestoreTabStripTemp();
            _dropWindow = target;

            if (target != null)
            {
                target.ShowTabStripTemp();    // reveal the strip if it was hidden (single tab)
                target.BringToFrontForDrag(); // keep the target (and its indicator) above neighbours
            }
        }

        if (target == null)
        {
            _tabDropIndex = -1;
            return;
        }

        // Always show the vertical drop indicator while the cursor is over a container.
        _tabDropIndex = target.ComputeInsertionIndex(p);
        target.ShowTabDropIndicator(_tabDropIndex);
    }

    private void ClearTabDropVisuals()
    {
        _dropWindow?.HideTabDropIndicator();
        _dropWindow?.RestoreTabStripTemp();
        _dropWindow = null;
        _tabDropIndex = -1;
        UpdateChrome();
    }

    private void PerformTabDrop()
    {
        if (_dragTab == null)
        {
            return;
        }

        var box = _dragTab.Model;

        if (_dropWindow == null)
        {
            // Released over empty desktop: create a new container at the cursor.
            var p = _lastDragPoint;
            var wa = SystemParameters.WorkArea;
            double left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - 240));
            double top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - 200));
            _host.MoveBoxToNewContainer(box, _vm, left, top);
            _save();
            return;
        }

        if (_dropWindow == this && _vm.BoxContainerVm != null)
        {
            int from = _vm.BoxContainerVm.Tabs.IndexOf(_dragTab);
            _vm.BoxContainerVm.MoveTab(from, _tabDropIndex);
            _save();
        }
        else
        {
            // Merge into another container at the indicated insertion index; make it the active tab.
            _host.MoveBoxToContainer(box, _vm, _dropWindow._vm, _tabDropIndex);
            _save();
        }
    }

    private BoxContainerWindow? FindContainerWindowAt(Point p)
    {
        BoxContainerWindow? self = null;
        BoxContainerWindow? other = null;

        foreach (var w in Application.Current.Windows.OfType<BoxContainerWindow>())
        {
            if (w._vm.BoxContainerVm == null)
            {
                continue; // Custom widgets are not tab targets.
            }

            // Recompute the window's on-screen rectangle live (DIP, matching the screen mouse point)
            // rather than trusting a cached stored bounds value.
            var tl = w.PointToScreen(new Point(0, 0));
            double x = tl.X, y = tl.Y, ww = w.ActualWidth, hh = w.ActualHeight;
            if (p.X < x || p.X > x + ww || p.Y < y || p.Y > y + hh)
            {
                continue;
            }

            // Prefer another container over the source when both contain the point (e.g. stacked
            // containers), so dragging onto an overlapping neighbour merges into it.
            if (w == this)
            {
                self = w;
            }
            else
            {
                other ??= w;
            }
        }

        return other ?? self;
    }

    private int ComputeInsertionIndex(Point screenP)
    {
        int index = 0;
        int from = _dragTab != null && _vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Contains(_dragTab)
            ? _vm.BoxContainerVm.Tabs.IndexOf(_dragTab)
            : -1;

        if (TabItems.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            int count = TabItems.Items.Count;
            for (int i = 0; i < count; i++)
            {
                if (i == from)
                {
                    continue; // the dragged tab doesn't define a gap
                }

                if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement container || !container.IsVisible)
                {
                    continue;
                }

                var topLeft = container.PointToScreen(new Point(0, 0));
                double mid = topLeft.X + container.RenderSize.Width / 2;
                if (screenP.X > mid)
                {
                    index++;
                }
                else
                {
                    break;
                }
            }
        }

        return index;
    }

    private void ShowTabDropIndicator(int index)
    {
        int from = _dragTab != null && _vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Contains(_dragTab)
            ? _vm.BoxContainerVm.Tabs.IndexOf(_dragTab)
            : -1;
        int count = TabItems.Items.Count;

        double stripLeft = TabStrip.PointToScreen(new Point(0, 0)).X + TabStrip.Padding.Left;
        double screenBoundaryX = stripLeft;
        int gap = 0;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            if (i == from)
            {
                continue;
            }

            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement c || !c.IsVisible)
            {
                continue;
            }

            var tl = c.PointToScreen(new Point(0, 0));
            double left = tl.X;
            double right = tl.X + c.RenderSize.Width;

            if (gap == index)
            {
                screenBoundaryX = left;
                found = true;
                break;
            }

            gap++;
            if (gap == index)
            {
                screenBoundaryX = right;
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Index at/after the last non-dragged tab: drop at the end of the strip.
            for (int i = 0; i < count; i++)
            {
                if (i == from)
                {
                    continue;
                }

                if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is UIElement c && c.IsVisible)
                {
                    var tl = c.PointToScreen(new Point(0, 0));
                    screenBoundaryX = tl.X + c.RenderSize.Width;
                }
            }
        }

        double localX = screenBoundaryX - stripLeft;
        TabDropIndicator.Margin = new Thickness(localX, 0, 0, 0);
        TabDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideTabDropIndicator() => TabDropIndicator.Visibility = Visibility.Collapsed;

    private void Window_DragEnter(object sender, DragEventArgs e) => HandleContainerDragOver(e);

    private void Window_DragOver(object sender, DragEventArgs e) => HandleContainerDragOver(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // Only clear the highlight when the pointer actually leaves the window, not when it crosses
        // between child elements (which also raise DragLeave that bubbles here).
        if (e.OriginalSource is DependencyObject d && RootBorder.IsAncestorOf(d))
        {
            return;
        }

        HideDropHighlight();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        HideDropHighlight();

        if (!IsValidContainerDrop(e))
        {
            // Dropped back onto its own (or an invalid) container: treat as a cancel, never a move.
            e.Handled = true;
            return;
        }

        var source = (ContainerViewModel)e.Data.GetData(DndFormats.SourceContainer)!;
        var box = e.Data.GetData(DndFormats.Box) as Box;

        if (box != null)
        {
            _host.MoveBoxToContainer(box, source, _vm);
        }
        else
        {
            _host.MergeContainers(source, _vm);
        }

        e.Handled = true;
        _save();
    }

    private void HandleContainerDragOver(DragEventArgs e)
    {
        if (IsValidContainerDrop(e))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            ShowDropHighlight();
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            HideDropHighlight();
        }
    }

    private bool IsValidContainerDrop(DragEventArgs e)
    {
        // Only BoxContainer-type containers participate; drop must originate from a different BoxContainer.
        if (_vm.BoxContainerVm == null || !e.Data.GetDataPresent(DndFormats.SourceContainer))
        {
            return false;
        }

        if (e.Data.GetData(DndFormats.SourceContainer) is not ContainerViewModel source)
        {
            return false;
        }

        return source.Id != _vm.Id && source.BoxContainerVm != null;
    }

    internal void ShowDropHighlight()
    {
        if (_origBorderBrush == null)
        {
            _origBorderBrush = RootBorder.BorderBrush;
            _origBorderThickness = RootBorder.BorderThickness;
        }

        RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        RootBorder.BorderThickness = new Thickness(3);
    }

    internal void HideDropHighlight()
    {
        if (_origBorderBrush == null)
        {
            return;
        }

        RootBorder.BorderBrush = _origBorderBrush;
        RootBorder.BorderThickness = _origBorderThickness;
        _origBorderBrush = null;
    }

    /// <summary>Raises this window above its sibling containers so its drop highlight is visible during a drag.</summary>
    internal void BringToFrontForDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _zOrder.SetZOrder(hwnd, ZOrderTarget.Top);
    }

    /// <summary>Reveals the tab strip while the cursor is over this container during a drag, even when it is
    /// normally hidden (a single tab). <see cref="RestoreTabStripTemp"/> reverts to the bound visibility.</summary>
    internal void ShowTabStripTemp()
    {
        if (TabStrip.Visibility != Visibility.Visible)
        {
            TabStrip.Visibility = Visibility.Visible;
            _tabStripTempShown = true;
        }
    }

    internal void RestoreTabStripTemp()
    {
        if (_tabStripTempShown)
        {
            _tabStripTempShown = false;
            UpdateChrome(); // re-establish the hover/focus-driven visibility
        }
    }

    /// <summary>
    /// While the existing window-move gesture is in progress, checks whether this container's centre is
    /// over another <see cref="DesktopItemContainerType.BoxContainer"/>. If so, that container is highlighted
    /// as the merge target; on release <see cref="CompleteMergeIfAny"/> merges into it.
    /// </summary>
    private void UpdateMergeTargetDuringDrag()
    {
        if (_vm.BoxContainerVm == null)
        {
            return;
        }

        double cx = Left + Width / 2;
        double cy = Top + Height / 2;

        var newTarget = _host.Containers.FirstOrDefault(c =>
            c.Id != _vm.Id &&
            c.BoxContainerVm != null &&
            cx >= c.Bounds.X && cx <= c.Bounds.X + c.Bounds.Width &&
            cy >= c.Bounds.Y && cy <= c.Bounds.Y + c.Bounds.Height);

        if (newTarget == _mergeTarget)
        {
            return;
        }

        // While merging onto another container, hide the snap guides so only the drop highlight shows.
        _drag.EnableGuides = newTarget == null;

        if (_mergeTarget != null)
        {
            FindWindowFor(_mergeTarget)?.HideDropHighlight();
        }

        _mergeTarget = newTarget;
        if (_mergeTarget != null)
        {
            FindWindowFor(_mergeTarget)?.ShowDropHighlight();
        }
    }

    private void CompleteMergeIfAny()
    {
        var target = _mergeTarget;
        _mergeTarget = null;
        _drag.EnableGuides = true;

        if (target == null)
        {
            return;
        }

        FindWindowFor(target)?.HideDropHighlight();
        _host.MergeContainers(_vm, target);
        _save();
    }

    private static BoxContainerWindow? FindWindowFor(ContainerViewModel vm)
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is BoxContainerWindow bw && bw.DataContext == vm)
            {
                return bw;
            }
        }

        return null;
    }
}
