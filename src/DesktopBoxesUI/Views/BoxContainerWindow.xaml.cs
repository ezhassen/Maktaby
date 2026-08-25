using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
public partial class BoxContainerWindow : Window, IContentDialogHostProvider
{
    private readonly ContainerViewModel _vm;
    private readonly MainViewModel _host;
    private readonly IWindowPositioningService _positioning;
    private readonly Action _save;
    private readonly WindowDragController _drag;
    private readonly IMouseMonitor _mouseMonitor;
    private readonly IZOrderService _zOrder = App.Services.GetRequiredService<IZOrderService>();
    private readonly uint _currentProcessId = (uint)System.Environment.ProcessId;
    private readonly IDialogService _dialogs = App.Services!.GetRequiredService<IDialogService>();

    // The global dialog service renders WPF-UI content dialogs on this host.
    public Wpf.Ui.Controls.ContentDialogHost DialogHost => RootContentDialogHost;

    // Chrome (header buttons, tab strip, scrollbar) is shown only when the container is hovered or focused.
    private bool _mouseOver;
    private bool _keyboardFocused;
    private bool _isRenaming;

    // Effective roll direction used for geometry/orientation. Starts at Top and follows either the
    // explicit user choice (_vm.RollDirection) or snap-based detection when _vm.RollDirection is null.
    private RollDirection _effectiveDir = RollDirection.Top;
    private DateTime? _lastTitleClick;

    // Thickness of the TitleBar strip (unrotated header height). Captured once; for Left/Right rolls the
    // header is rotated and fills the full window height, so its measured height can no longer be used.
    private double _headerThickness = 30;

    //static BoxContainerWindow()
    //{
    //      //Not needed any more when using surface 
    //    // Commit any active inline rename when a mouse button is pressed anywhere in the app
    //    // (covers clicks on the desktop, other containers, etc., where LostFocus may not fire).
    //    EventManager.RegisterClassHandler(
    //        typeof(Window),
    //        PreviewMouseDownEvent,
    //        new MouseButtonEventHandler(OnAnyPreviewMouseDown));
    //}

    //private static void OnAnyPreviewMouseDown(object sender, MouseButtonEventArgs e)
    //{
    //    if (Application.Current == null)
    //    {
    //        return;
    //    }

    //    foreach (var w in Application.Current.Windows.OfType<BoxContainerWindow>())
    //    {
    //        if (w._dragTab is not null)
    //        {
    //            w.CancelTabDrag();
    //        }
    //        w.TryCommitRename(e.OriginalSource);
    //    }

    //}

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

        ApplyAppearance();

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
            r => ApplyDraggedBounds(r),
            () => _host.Containers.Where(c => c.Id != _vm.Id).Select(GetDropRect).ToList(),
            _save,
            () => HeaderBorder.ActualHeight);

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Re-applies the window geometry from the view-model bounds (handles both the rolled and
    /// unrolled states). Used after a display/DPI/resolution change rescales the layout.</summary>
    internal void ApplyGeometry() => ApplyRoll();

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyType();
        UpdateBody();
        if (_vm.BoxContainerVm != null)
        {
            _vm.BoxContainerVm.SelectedIndexChanged += () =>
        {
            UpdateBody();
            RefreshRemoveTabMenu();
        };
            _vm.BoxContainerVm.PropertyChanged += OnBoxContainerVmPropertyChanged;
        }

        UpdateChrome();
        if (_vm.RollDirection != null)
        {
            _effectiveDir = _vm.RollDirection.Value;
        }

        // Self-heal any off-screen bounds (e.g. from a rolled container dragged/snapped before this
        // guard existed) so the layout loads correctly.
        ClampBoundsToWorkArea();
        ApplyRoll();
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
        MenuAddTab.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        RefreshRemoveTabMenu();
        MenuRollDir.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        RollButton.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        BoxContent.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = isBox ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome();
    }

    private void RefreshRemoveTabMenu()
    {
        bool isBox = _vm.BoxContainerVm != null;
        MenuRemoveTab.Visibility = (isBox && !(_vm.ActiveBox?.IsDefault ?? false))
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the header buttons, tab strip and scrollbar only while the container is hovered or focused;
    /// otherwise only the title (and content) remain, giving a clean desktop look.
    /// </summary>
    private void UpdateChrome()
    {
        bool isBox = _vm.BoxContainerVm != null;
        bool show = isBox && (_mouseOver || _keyboardFocused);
        Visibility headerButtonsVisibility = show ? Visibility.Visible : Visibility.Collapsed;
        MenuButton.Visibility = headerButtonsVisibility;
        RollButton.Visibility = headerButtonsVisibility;

        // The tab strip is always visible when there is more than one tab; the header buttons and
        // scrollbar stay hidden until the container is hovered or focused. While rolled it is hidden.
        TabStrip.Visibility = isBox && _vm.BoxContainerVm!.ShowTabs && !_vm.IsRolled
            ? Visibility.Visible
            : Visibility.Collapsed;

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

    internal void ApplyAppearance()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>().UserSettings;
        var palette = BoxAppearance.Resolve(settings, _vm.Transparency);

        RootBorder.Background = palette.Back;
        RootBorder.BorderBrush = palette.Border;
        RootBorder.BorderThickness = palette.Thickness;
        HeaderBorder.Background = palette.HeaderBack;
        TitleText.Foreground = palette.HeaderFore;
        MenuButton.Foreground = palette.HeaderFore;
        RollButton.Foreground = palette.HeaderFore;
        //TabStrip.Background = palette.TabBack;
        TabStrip.BorderBrush = palette.HeaderBorder;
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

    private void TitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isRenaming)
        {
            CommitRename();
            return;
        }

        // A fast double-click toggles roll. Two distinct clicks separated by a gap (a "slow double-click",
        // like Explorer's rename gesture) starts an inline rename.
        if (e.ClickCount == 2)
        {
            _lastTitleClick = null;
            RollButton_Click(sender, e);
            return;
        }

        if (_lastTitleClick != null && _vm.ActiveBox != null &&
            (DateTime.UtcNow - _lastTitleClick.Value).TotalMilliseconds <= 1200)
        {
            _lastTitleClick = null;
            BeginRename();
            return;
        }

        _lastTitleClick = DateTime.UtcNow;
        _drag.BeginTitleDrag(e);
    }

    private void TitleArea_MouseMove(object sender, MouseEventArgs e)
    {
        _drag.TitleDrag(e);
        if (_drag.IsDragging)
        {
            _lastTitleClick = null; // a real drag cancels the pending rename gesture
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
            if (_vm.RollDirection == null)
            {
                var dir = DetectRollDirection();
                if (dir != null)
                {
                    _effectiveDir = dir.Value;
                }
                else
                {
                    _effectiveDir = RollDirection.Top;//restore to default
                }
            }
            else
            {
                _effectiveDir = _vm.RollDirection.Value;
            }

            if (_vm.IsRolled)
            {
                ApplyRoll();
            }
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

    private async void RemoveTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveBox is { IsDefault: true })
        {
            await _dialogs.ShowMessageAsync("The default box cannot be deleted.", new DialogOptions { Title = "Cannot delete" });
            return;
        }

        if (_vm.ActiveBox is { Items.Count: > 0 })
        {
            var confirmed = await _dialogs.ShowConfirmAsync(
                "This box contains items. Delete it anyway?",
                new DialogOptions { Title = "Confirm delete", PrimaryButtonText = "Delete", PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger });
            if (!confirmed)
            {
                return;
            }
        }

        _vm.RemoveTabCommand.Execute(null);
        UpdateBody();
    }

    private void RollButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsRolled = !_vm.IsRolled;
        ApplyRoll();
        _save();
    }

    /// <summary>
    /// Sets the roll direction. A non-null value is an explicit user choice (persisted, overrides snap
    /// detection) and immediately rolls the container to that edge. <c>null</c> restores auto-detection.
    /// </summary>
    private void SetRollDirection(RollDirection? dir)
    {
        _vm.RollDirection = dir;
        if (dir == null)
        {
            var detected = DetectRollDirection();
            if (detected != null)
            {
                _effectiveDir = detected.Value;
            }
        }
        else
        {
            _effectiveDir = dir.Value;
            _vm.IsRolled = true;
        }

        if (_vm.IsRolled)
        {
            ApplyRoll();
            _save();
        }
    }

    private void MenuSetRollDir_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        RollDirection? dir = tag switch
        {
            "Top" => RollDirection.Top,
            "Bottom" => RollDirection.Bottom,
            "Left" => RollDirection.Left,
            "Right" => RollDirection.Right,
            _ => null,
        };
        SetRollDirection(dir);
    }

    /// <summary>
    /// Builds the on-screen rectangle for the rolled TitleBar strip, snapped to the given edge of the full
    /// (unrolled) <see cref="DesktopItemContainer"/> bounds.
    /// </summary>
    private RectD GetRolledRect(RectD full, RollDirection dir)
    {
        double headerH = _headerThickness;
        double stripW = headerH;
        return dir switch
        {
            RollDirection.Bottom => RectD.FromXYWH(full.X, full.Y + full.Height - headerH, full.Width, headerH),
            RollDirection.Left => RectD.FromXYWH(full.X, full.Y, stripW, full.Height),
            RollDirection.Right => RectD.FromXYWH(full.X + full.Width - stripW, full.Y, stripW, full.Height),
            _ => RectD.FromXYWH(full.X, full.Y, full.Width, headerH), // Top
        };
    }

    /// <summary>Applies the current roll state and direction to the window geometry, chrome and TitleBar orientation.</summary>
    private void ApplyRoll()
    {
        // Capture the unrotated header thickness. Once rolled Left/Right the header is rotated and spans
        // the full window height, so its measured height can no longer stand in for the strip thickness.
        if (HeaderContent.LayoutTransform == null && HeaderBorder.ActualHeight > 0)
        {
            _headerThickness = HeaderBorder.ActualHeight;
        }

        if (!_vm.IsRolled)
        {
            BodyContent.Visibility = Visibility.Visible;
            MinHeight = 120;
            MinWidth = 160;
            ResizeMode = ResizeMode.CanResize;
            Left = _vm.Left;
            Top = _vm.Top;
            Width = _vm.Width;
            Height = _vm.Height;
            ApplyTitleOrientation(RollDirection.Top);
            UpdateChrome();
            UpdateRollIcon();
            return;
        }

        ResizeMode = ResizeMode.NoResize;

        var rect = ClampToWorkArea(GetRolledRect(new RectD(_vm.Left, _vm.Top, _vm.Width, _vm.Height), _effectiveDir));

        BodyContent.Visibility = Visibility.Collapsed;
        TabStrip.Visibility = Visibility.Collapsed;

        double headerH = _headerThickness;
        double stripW = headerH;
        MinHeight = _effectiveDir is RollDirection.Top or RollDirection.Bottom ? headerH : 120;
        MinWidth = _effectiveDir is RollDirection.Left or RollDirection.Right ? stripW : 160;

        Left = rect.X;
        Top = rect.Y;
        Width = rect.Width;
        Height = rect.Height;

        ApplyTitleOrientation(_effectiveDir);
        UpdateRollIcon();
    }

    /// <summary>
    /// Positions the TitleBar on the rolled edge and rotates the header content so a Left/Right roll reads
    /// as a vertical TitleBar. When not rolled the TitleBar stays at the top, unrotated.
    /// </summary>
    private void ApplyTitleOrientation(RollDirection dir)
    {
        DockPanel.SetDock(HeaderBorder, dir switch
        {
            RollDirection.Bottom => Dock.Bottom,
            RollDirection.Left => Dock.Left,
            RollDirection.Right => Dock.Right,
            _ => Dock.Top,
        });

        double angle = dir switch
        {
            RollDirection.Left => 90,
            RollDirection.Right => -90,
            _ => 0,
        };
        HeaderContent.LayoutTransform = angle == 0 ? null! : new RotateTransform(-90);
    }

    private void UpdateRollIcon()
    {
        if (!_vm.IsRolled)
        {
            RollIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Subtract24;//FullScreenMinimize24, ArrowMinimize24,ArrowMinimizeVertical24
            RollButton.ToolTip = "Roll";
            return;
        }

        RollIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.FullScreenMaximize24;
        RollButton.ToolTip = "Unroll";
    }

    /// <summary>
    /// Maps a dragged/displayed bounds rectangle back onto the (unrolled) container bounds. The collapsed
    /// dimension is preserved so the "home" size survives rolling; the moved strip anchors the home rect.
    /// </summary>
    private void ApplyDraggedBounds(RectD r)
    {
        double headerH = _headerThickness;
        double stripW = headerH;

        if (!_vm.IsRolled)
        {
            _vm.Left = r.X;
            _vm.Top = r.Y;
            _vm.Width = r.Width;
            _vm.Height = r.Height;
            ClampBoundsToWorkArea();
            return;
        }

        switch (_effectiveDir)
        {
            case RollDirection.Top:
                _vm.Left = r.X;
                _vm.Top = r.Y;
                break;
            case RollDirection.Bottom:
                _vm.Left = r.X;
                _vm.Top = r.Y - _vm.Height + headerH;
                break;
            case RollDirection.Left:
                _vm.Left = r.X;
                _vm.Top = r.Y;
                break;
            case RollDirection.Right:
                _vm.Left = r.X - _vm.Width + stripW;
                _vm.Top = r.Y;
                break;
        }

        // The collapsed strip implies a full-size home; keep that home within the work area so a rolled
        // container can never be dragged/snapped into an off-screen position (and then saved that way).
        ClampBoundsToWorkArea();
    }

    private void ClampBoundsToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        _vm.Left = Math.Max(wa.X, Math.Min(_vm.Left, wa.Right - _vm.Width));
        _vm.Top = Math.Max(wa.Y, Math.Min(_vm.Top, wa.Bottom - _vm.Height));
    }

    private static RectD ClampToWorkArea(RectD rect)
    {
        var wa = SystemParameters.WorkArea;
        double x = Math.Max(wa.X, Math.Min(rect.X, wa.Right - rect.Width));
        double y = Math.Max(wa.Y, Math.Min(rect.Y, wa.Bottom - rect.Height));
        return RectD.FromXYWH(x, y, rect.Width, rect.Height);
    }

    /// <summary>
    /// Detects which work-area edge the TitleBar is flush against after a drag (i.e. where it snapped) and
    /// returns that as the roll direction. Horizontal edges (Top/Bottom) take priority over vertical ones so
    /// a corner snap (e.g. TopLeft, bottomRight) yields a Top/Bottom roll, not Left/Right. Returns null when
    /// not near any edge (caller keeps the current value).
    /// </summary>
    private RollDirection? DetectRollDirection()
    {
        var wa = SystemParameters.WorkArea;
        double left = this.Left, top = this.Top, right = this.Left + this.Width, bottom = this.Top + this.Height;
        double dl = left - wa.Left;
        double dr = wa.Right - right;
        double dt = top - wa.Top;
        double db = wa.Bottom - bottom;
        double threshold = 40;

        bool nearH = dt <= threshold || db <= threshold;
        bool nearV = dl <= threshold || dr <= threshold;

        if (nearH)
        {
            return dt <= db ? RollDirection.Top : RollDirection.Bottom;
        }

        if (nearV)
        {
            return dl <= dr ? RollDirection.Left : RollDirection.Right;
        }

        return null;
    }

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Rename is now driven by a slow double-click on the TitleBar (see TitleArea_MouseLeftButtonDown);
        // let the event bubble to the TitleBar handler. This stub keeps the XAML wiring intact.
    }

    private void BeginRename()
    {
        if (_vm.ActiveBox == null || _isRenaming)
        {
            return;
        }

        _isRenaming = true;
        TitleEdit.Text = _vm.ActiveBox.Name;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEdit.Visibility = Visibility.Visible;
        TitleEdit.Focus();
        TitleEdit.SelectAll();
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

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Any(t => t.Items.Count > 0))
        {
            if (_vm.BoxContainerVm.Tabs.Any(t => t.IsDefault == true))
            {
                await _dialogs.ShowMessageAsync("The default box cannot be deleted.", new DialogOptions { Title = "Cannot delete" });
                return;
            }

            var confirmed = await _dialogs.ShowConfirmAsync(
                "This container contains items. Delete it anyway?",
                new DialogOptions { Title = "Confirm delete", PrimaryButtonText = "Delete", PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger });
            if (!confirmed)
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
    private Point _lastWindowRel;           // last window-relative cursor point, for the debug A/B compare
    private BoxContainerWindow? _dropWindow;    // container currently under the cursor (owns the drop visuals)
    private int _tabDropIndex = -1;             // insertion index within _dropWindow
    private ContainerViewModel? _mergeTarget;
    private Brush? _origBorderBrush;
    private Thickness _origBorderThickness;
    private bool _tabStripTempShown;
    private Button? _dragButton;            // the tab button being dragged; hidden while dragging

    /// <summary>When true, a click-through overlay draws the computed drop-target rectangles and cursor
    /// point during a tab drag, so the coordinate maths can be verified visually. Set to false to disable.</summary>
    public static bool DragDebugEnabled = false;
    private DragDebugOverlay? _debugOverlay;

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { Tag: BoxViewModel box })
        {
            _dragTab = box;
            _dragButton = (Button)sender;
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
            // Capture on the always-visible root so the drag survives the dragged tab being hidden.
            //if (_dragButton != null)
            //{
            //    _dragButton.Visibility = Visibility.Collapsed;
            //}

            // Drop one column so the remaining tabs fill the strip (the hidden tab no longer reserves space).
            //SetTabColumnsForDrag();

            RootBorder.CaptureMouse();

            if (DragDebugEnabled)
            {
                _debugOverlay ??= new DragDebugOverlay();
                if (!_debugOverlay.IsVisible)
                {
                    _debugOverlay.Show();
                }
            }
        }

        _lastWindowRel = e.GetPosition(this);
        _lastDragPoint = GetScreenDragPoint(e);
        UpdateTabDropTarget(_lastDragPoint);
    }

    /// <summary>
    /// Screen (DIP) cursor position used for hit-testing in <c>FindContainerWindowAt</c>.
    /// <c>e.GetPosition(this)</c> stays accurate while the tab button holds mouse capture (and even when
    /// the cursor is over another window), and <c>PointToScreen</c> maps it to the same DIP screen space
    /// that every <c>w.PointToScreen</c> / <c>FindContainerWindowAt</c> comparison uses. We deliberately
    /// avoid converting <c>GetCursorPos</c>'s device pixels, because that requires the primary-monitor DPI
    /// while <c>VisualTreeHelper.GetDpi(this)</c> may report a different (non-primary) monitor's DPI.
    /// </summary>
    private Point GetScreenDragPoint(MouseEventArgs e) => this.PointToScreen(e.GetPosition(this));

    private void Tab_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragTab == null)
        {
            return;
        }

        if (_tabDragging)
        {
            // Restore the tab button before any model change regenerates the strip.
            //if (_dragButton != null)
            //{
            //    _dragButton.Visibility = Visibility.Visible;
            //}

            // Recompute from the release point so a stale last-move position can't drive the drop.
            _lastWindowRel = e.GetPosition(this);
            _lastDragPoint = GetScreenDragPoint(e);
            UpdateTabDropTarget(_lastDragPoint);

            Mouse.Capture(null);
            Mouse.OverrideCursor = null;
            PerformTabDrop();
            _dropWindow?.UpdateChrome();
            ClearTabDropVisuals();
            _debugOverlay?.Hide();
            this.UpdateChrome();
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

        _dragButton = null;
        _dragTab = null;
        _tabDragging = false;
        //SetTabColumnsForDrag();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tabDragging && e.Key == Key.Escape)
        {
            CancelTabDrag();
            e.Handled = true;
        }
    }

    private void CancelTabDrag()
    {
        if (_dragTab is null)
        {
            return;
        }

        //if (_dragButton != null)
        //{
        //    _dragButton.Visibility = Visibility.Visible;
        //}

        _dragTab = null;
        _tabDragging = false;
        _dragButton = null;
        Mouse.OverrideCursor = null;
        Mouse.Capture(null);
        ClearTabDropVisuals();
        _debugOverlay?.Hide();
        //SetTabColumnsForDrag();
    }

    /// <summary>
    /// The tab strip uses a <see cref="UniformGrid"/> whose column count is bound to the tab count. While a
    /// tab is dragged (and collapsed) we drop the column count by one so the remaining tabs fill the strip
    /// instead of leaving a gap. The binding reasserts the real count once the model changes on drop.
    /// </summary>
    private void SetTabColumnsForDrag()
    {
        if (_vm.BoxContainerVm == null)
        {
            return;
        }

        var ug = FindVisualChild<UniformGrid>(TabItems);
        if (ug != null)
        {
            if (_tabDragging)
            {
                // A local value overrides the XAML binding during the drag so the remaining tabs fill
                // the strip (the dragged tab's column would otherwise stay reserved/empty).
                int count = _vm.BoxContainerVm.Tabs.Count;
                ug.Columns = Math.Max(1, count - 1);
            }
            else
            {
                // Restore the binding so future tab-count changes keep the strip in sync.
                BindingOperations.SetBinding(
                    ug,
                    UniformGrid.ColumnsProperty,
                    new Binding("BoxContainerVm.Tabs.Count") { Source = this.DataContext, Mode = BindingMode.OneWay });
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
            {
                return t;
            }

            var inner = FindVisualChild<T>(child);
            if (inner != null)
            {
                return inner;
            }
        }

        return null;
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

        DrawTabDragDebug(p);
    }

    private void DrawTabDragDebug(Point p)
    {
        if (_debugOverlay is not { IsVisible: true })
        {
            return;
        }

        _debugOverlay.Clear();

        // Cursor markers: red computed dot, blue WPF reference dot (should coincide), a full crosshair,
        // and a live coordinate read-out, so the exact cursor position can be read against the rectangles.
        var oldScreen = this.PointToScreen(_lastWindowRel);
        _debugOverlay.DrawPoint(p.X, p.Y, Brushes.Red, "cursor (calc)");
        _debugOverlay.DrawPoint(oldScreen.X, oldScreen.Y, Brushes.Blue, "cursor (ref)");
        _debugOverlay.DrawCrosshair(p.X, p.Y, Brushes.Red);
        _debugOverlay.DrawTextAt(p.X, p.Y, $"cursor {p.X:0},{p.Y:0}", Brushes.Red);

        const double tolerance = 50;

        // Each container: actual window bounds (white), tolerance hit area (yellow/cyan), and every
        // visible tab rectangle with its index, so gaps and misalignment are easy to spot.
        foreach (var w in Application.Current.Windows.OfType<BoxContainerWindow>())
        {
            if (w._vm?.BoxContainerVm == null)
            {
                continue;
            }

            var tl = w.PointToScreen(new Point(0, 0));
            bool isSelf = w == this;
            Brush boundsStroke = isSelf ? Brushes.Yellow : Brushes.Cyan;

            _debugOverlay.DrawRect(tl.X, tl.Y, w.ActualWidth, w.ActualHeight, Brushes.White, isSelf ? "SELF" : "OTHER");
            _debugOverlay.DrawRect(tl.X - tolerance, tl.Y - tolerance, w.ActualWidth + tolerance * 2, w.ActualHeight + tolerance * 2, boundsStroke);

            var bounds = w.GetVisibleTabBounds();
            for (int i = 0; i < bounds.Count; i++)
            {
                _debugOverlay.DrawRect(bounds[i].Left, tl.Y, bounds[i].Right - bounds[i].Left, 30, boundsStroke, $"{(isSelf ? "S" : "O")}{i}");
            }
        }

        // The gap left by the hidden dragged tab in the source window — visualises the "tabs not filling
        // width" problem by outlining exactly where the collapsed tab used to sit. Computed from the
        // uniform-grid cell geometry so it stays valid even before the strip has fully laid out.
        //if (_dragTab != null && _vm.BoxContainerVm != null)
        //{
        //    int from = _vm.BoxContainerVm.Tabs.IndexOf(_dragTab);
        //    int count = _vm.BoxContainerVm.Tabs.Count;
        //    if (from >= 0 && count > 0)
        //    {
        //        var stripTl = TabStrip.PointToScreen(new Point(0, 0));
        //        double innerLeft = stripTl.X + TabStrip.Padding.Left;
        //        double innerRight = TabStrip.PointToScreen(new Point(TabStrip.ActualWidth, 0)).X - TabStrip.Padding.Right;
        //        double innerW = innerRight - innerLeft;
        //        double cell = innerW / count;
        //        double gapLeft = innerLeft + from * cell;

        //        _debugOverlay.DrawRect(gapLeft, stripTl.Y, cell, TabStrip.ActualHeight, Brushes.Magenta, "GAP");
        //    }
        //}

        var target = FindContainerWindowAt(p);
        if (target != null)
        {
            var itl = target.PointToScreen(new Point(0, 0));
            _debugOverlay.DrawRect(itl.X, itl.Y, target.ActualWidth, target.ActualHeight, Brushes.Lime, "TARGET");

            foreach (var (l, r) in target.GetVisibleTabBounds())
            {
                _debugOverlay.DrawRect(l, itl.Y, r - l, 30, Brushes.Orange);
            }

            _debugOverlay.DrawText(
                $"cursor p        = {p.X:0},{p.Y:0}\n" +
                $"target          = {(target == this ? "SELF" : target.TitleText?.Text ?? "?")}\n" +
                $"insertion index = {_tabDropIndex}");
        }
        else
        {
            _debugOverlay.DrawText(
                $"cursor p        = {p.X:0},{p.Y:0}\n" +
                $"target          = NONE (empty desktop, would spawn new container)");
        }
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

    const double tolerance = 5;
    private BoxContainerWindow? FindContainerWindowAt(Point p)
    {
        // A small tolerance lets a dragged tab "stick" to a container when the cursor is just outside
        // its window edge (e.g. nudged past the right edge, or over a rolled container's collapsed body)
        // instead of being treated as empty desktop — which would otherwise spawn a brand-new container.

        BoxContainerWindow? self = null;
        BoxContainerWindow? other = null;

        foreach (var w in Application.Current.Windows.OfType<BoxContainerWindow>())
        {
            if (w._vm.BoxContainerVm == null)
            {
                continue; // Custom widgets are not tab targets.
            }
            //checks using local point instead (works)
            var localPoint = w.PointFromScreen(p);

            if (localPoint.X < -tolerance ||
                localPoint.Y < -tolerance ||
                localPoint.X > w.ActualWidth + tolerance ||
                localPoint.Y > w.ActualHeight + tolerance)
            {
                continue;
            }

            // Prefer another container over the source when both contain the point (e.g. stacked
            // containers), so dragging onto an overlapping neighbor merges into it.
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

    /// <summary>
    /// Returns the on-screen left/right edges of every visible tab except the one being dragged (which is
    /// conceptually lifted out of the strip), in left-to-right order. Used to compute both the insertion
    /// index and where to draw the drop indicator.
    /// </summary>
    private List<(double Left, double Right)> GetVisibleTabBounds(bool includeDraggingTap = true)
    {
        var bounds = new List<(double, double)>();
        if (TabItems.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            return bounds;
        }

        int from = _dragTab != null && _vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Contains(_dragTab)
            ? _vm.BoxContainerVm.Tabs.IndexOf(_dragTab)
            : -1;

        int count = TabItems.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (!includeDraggingTap && i == from)
            {
                continue; // the dragged tab is lifted out; it defines no gap
            }

            if (TabItems.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement container || !container.IsVisible)
            {
                continue;
            }

            var sLeft = container.PointToScreen(new Point(0, 0)).X;
            var sRight = container.PointToScreen(new Point(container.RenderSize.Width, 0)).X;
            bounds.Add((sLeft, sRight));
            //bounds.Add((topLeft.X, topLeft.X + container.RenderSize.Width));
        }

        return bounds;
    }

    private int ComputeInsertionIndex(Point screenP)
    {
        var bounds = GetVisibleTabBounds();
        int index = 0;
        //calculate using half
        foreach (var (left, right) in bounds)
        {
            var tWidth = right - left;
            var rHalf = right - (tWidth / 2);
            if (screenP.X > rHalf)
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    private void ShowTabDropIndicator(int index)
    {
        var localX = 0d;
        int count = TabItems.Items.Count;
        var bwidth = 30d;
        if (count > 0)
        {
            if (TabItems.ItemContainerGenerator.ContainerFromIndex(0) is UIElement container)
            {
                bwidth = container.RenderSize.Width;
            }
        }
        if (index + 1 == count)
        {
            localX = (bwidth * count) - 4;//last so show in right
        }
        else if (index == 0)
        {
            localX = TabStrip.Padding.Left;
        }
        else //if (index != 0)
        {
            localX = TabStrip.Padding.Left;
            for (int i = 1; i < count; i++)
            {
                if (index >= i)
                {
                    localX += bwidth;
                }
                else
                {
                    break;
                }
            }
        }

        ////double stripLeft = TabStrip.PointToScreen(new Point(0, 0)).X + TabStrip.Padding.Left;
        //var bounds = GetVisibleTabBounds();

        //double screenBoundaryX;
        //if (bounds.Count == 0)
        //{
        //    screenBoundaryX = 0;
        //}
        //else if (index <= 0)
        //{
        //    screenBoundaryX = bounds[0].Left;
        //}
        //else if (index >= bounds.Count)
        //{
        //    screenBoundaryX = bounds[^1].Right;
        //}
        //else
        //{
        //    screenBoundaryX = bounds[index].Left;
        //}
        //var localpaddX = TabStrip.PointFromScreen(new Point(screenBoundaryX, 0)).X;
        ////double localX = screenBoundaryX - stripLeft;
        //double localX = localpaddX + TabStrip.Padding.Left;
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
            !c.IsRolled &&
            ContainsPoint(GetDropRect(c), cx, cy));

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

    /// <summary>
    /// The rectangle used for drop / snap detection against <paramref name="c"/>. When the container is
    /// rolled this is its on-screen strip (the visible title bar), not the full (hidden) unrolled bounds.
    /// </summary>
    private static RectD GetDropRect(ContainerViewModel c)
    {
        var w = FindWindowFor(c);
        if (w != null)
        {
            return RectD.FromXYWH(w.Left, w.Top, w.Width, w.Height);
        }

        return c.Bounds;
    }

    private static bool ContainsPoint(RectD r, double x, double y)
    {
        return x >= r.X && x <= r.X + r.Width && y >= r.Y && y <= r.Y + r.Height;
    }
}
