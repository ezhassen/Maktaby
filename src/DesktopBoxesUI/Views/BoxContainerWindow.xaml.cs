using DesktopBoxesUI.Controls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
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
public partial class BoxContainerWindow : WidgetWindow, IContentDialogHostProvider
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
            this, App.Services.GetRequiredService<DesktopManager>(),
            monitor,
            dpi,
            snapping,
            _positioning,
            r => ApplyDraggedBounds(r),
            () => _host.Containers.Where(c => c.Id != _vm.Id).Select(GetDropRect).ToList(),
            _save,
            () => HeaderBorder.ActualHeight,
            getIsLocked: () => _vm.IsLocked);

        Loaded += OnLoaded;
        Closed += OnClosed;
        BoxMenu.Opened += BoxMenu_Opened;
    }

    private void BoxMenu_Opened(object sender, RoutedEventArgs e)
    {
        SyncIconSizeChecks();
        //
        MenuSetRollDirAuto.IsChecked = _vm.RollDirection is null;
        MenuSetRollDirTop.IsChecked = _vm.RollDirection == RollDirection.Top;
        MenuSetRollDirLeft.IsChecked = _vm.RollDirection == RollDirection.Left;
        MenuSetRollDirRight.IsChecked = _vm.RollDirection == RollDirection.Right;
        MenuSetRollDirBottom.IsChecked = _vm.RollDirection == RollDirection.Bottom;

        if (_vm.ActiveBox != null)
        {
            bool isFolder = _vm.ActiveBox.BoxType == BoxType.FolderPortal;
            MenuBoxTypeDesktop.IsChecked = !isFolder;
            MenuBoxTypeFolder.IsChecked = isFolder;
            MenuFolderView.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            MenuBoxType.Visibility = _vm.ActiveBox.IsDefault ? Visibility.Collapsed : Visibility.Visible;
            bool isIcons = _vm.ActiveBox.FolderPortalViewMode == FolderPortalViewMode.Icons;
            MenuFolderViewIcons.IsChecked = isIcons;
            MenuFolderViewDetails.IsChecked = !isIcons;
        }
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

        //SyncIconSizeChecks();
    }

    /// <summary>Per-box icon size override from the menu: Auto/Default (null = follow the user
    /// default), Small (24), Mid (48), Large (96), or the custom slider. Applies immediately and
    /// persists.</summary>
    private void MenuSetIconSize_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveBox is not { } active)
        {
            return;
        }

        var tag = (sender as FrameworkElement)?.Tag as string;
        active.Model.IconSize = tag switch
        {
            "Small" => 24,
            "Mid" => 48,
            "Large" => 96,
            _ => null, // Auto / Default
        };

        ApplyIconSize(active);
    }

    private void MenuIconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Keep value label in sync even while dragging (before the model update)
        // Header StackPanel lives inside ContextMenu popup namescope — field may be null until menu is opened.
        if (MenuIconSizeValueText != null)
            MenuIconSizeValueText.Text = $"{(int)Math.Round(e.NewValue)}px";
        else if (sender is Slider s && s.Parent is StackPanel sp)
        {
            var tb = sp.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault(t => t.Name == "MenuIconSizeValueText");
            if (tb != null) tb.Text = $"{(int)Math.Round(e.NewValue)}px";
        }

        // Programmatic positioning during sync must not re-apply/persist.
        if (_suppressIconSizeSlider || !IsLoaded || _vm.ActiveBox is not { } active)
        {
            return;
        }

        int value = (int)Math.Round(e.NewValue);
        if (active.Model.IconSize == value)
        {
            return;
        }

        active.Model.IconSize = value;
        ApplyIconSize(active);
    }

    private void ApplyIconSize(BoxViewModel active)
    {
        BoxContent.RefreshIconSize();
        SyncIconSizeChecks();
        _save();
    }

    /// <summary>Guards programmatic slider positioning from re-applying/persisting icon size.</summary>
    private bool _suppressIconSizeSlider;

    /// <summary>Reflects which icon-size mode is active on the current box in the menu checkmarks,
    /// and positions the custom slider at the EFFECTIVE rendered size (so Auto shows the real px).</summary>
    private void SyncIconSizeChecks()
    {
        int? size = _vm.ActiveBox?.Model.IconSize;
        MenuIconSizeAuto.IsChecked = size is null;
        MenuIconSizeSmall.IsChecked = size == 24;
        MenuIconSizeMid.IsChecked = size == 48;
        MenuIconSizeLarge.IsChecked = size == 96;

        _suppressIconSizeSlider = true;
        if (MenuIconSizeSlider != null)
        {
            MenuIconSizeSlider.Value = BoxContent.IconSize; // effective: resolves Auto/default fallbacks
            if (MenuIconSizeValueText != null)
                MenuIconSizeValueText.Text = $"{(int)Math.Round(MenuIconSizeSlider.Value)}px";
        }
        _suppressIconSizeSlider = false;
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
        // Avoid stale multi-selections persisting across windows and causing drops to surface.
        try { BoxContent?.ClearSelectionOnDeactivate(); } catch { }
        try { FolderPortalContent?.ClearSelectionOnDeactivate(); } catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _mouseMonitor.MouseButtonDown -= OnGlobalMouseDown;
        _drag.Detach();
        if (_watchedBox != null) { _watchedBox.StopWatching(); _watchedBox = null; }
        if (_vm.BoxContainerVm != null)
        {
            foreach (var t in _vm.BoxContainerVm.Tabs.Where(t => t.IsFolderPortal && t.IsWatching))
                t.StopWatching();
        }
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

        // Re-resolve per-box/default icon size (user may have changed DefaultBoxIconSize).
        BoxContent.RefreshIconSize();
        FolderPortalContent.RefreshIconSize();
    }

    private BoxViewModel? _watchedBox;

    private void UpdateBody()
    {
        if (_vm.ActiveBox != null)
        {
            BoxContent.DataContext = _vm.ActiveBox;
            BoxContent.RequestSave = _save;
            BoxContent.Host = _host;
            FolderPortalContent.DataContext = _vm.ActiveBox;
            FolderPortalContent.Host = _host;
            FolderPortalContent.RequestSave = _save;

            bool isFolder = _vm.ActiveBox.BoxType == BoxType.FolderPortal;
            BoxContent.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
            FolderPortalContent.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
            // Keep placeholder logic in ApplyType, but ensure correct BoxContent state
            FolderPortalContent.UpdateView();
        }
        else
        {
            BoxContent.Visibility = Visibility.Collapsed;
            FolderPortalContent.Visibility = Visibility.Collapsed;
        }
        ManageFolderWatcher();
    }

    internal void ManageFolderWatcher()
    {
        // Stop previous watched box if it is no longer active
        if (_watchedBox != null && _watchedBox != _vm.ActiveBox)
        {
            _watchedBox.StopWatching();
            _watchedBox = null;
        }

        bool shouldWatch = _vm.ActiveBox != null
            && _vm.ActiveBox.IsFolderPortal
            && _vm.ActiveBox.HasFolder
            && !_vm.IsRolled;

        if (shouldWatch)
        {
            _watchedBox = _vm.ActiveBox!;
            _watchedBox.StartWatching();
            _ = _watchedBox.RefreshFolderAsync();
        }
        else
        {
            if (_watchedBox != null)
            {
                _watchedBox.StopWatching();
                _watchedBox = null;
            }
            // Ensure no other tab is left watching (e.g. after roll)
            if (_vm.BoxContainerVm != null)
            {
                foreach (var t in _vm.BoxContainerVm.Tabs.Where(t => t.IsFolderPortal && t.IsWatching))
                    t.StopWatching();
            }
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

    private void AddFolderPortal_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.BoxContainerVm == null) return;
        var vm = _vm.BoxContainerVm.AddTab("Folder Portal");
        vm.BoxType = BoxType.FolderPortal;
        vm.FolderPath = null;
        vm.FolderPortalViewMode = FolderPortalViewMode.Icons;
        UpdateBody();
        _save();
    }

    private async void MenuBoxType_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveBox is null || _vm.ActiveBox.IsDefault) return;
        var tag = (sender as FrameworkElement)?.Tag as string;
        BoxType newType = tag == "FolderPortal" ? BoxType.FolderPortal : BoxType.DesktopItems;
        if (_vm.ActiveBox.BoxType == newType) return;

        // DesktopItems -> FolderPortal with items: move to default box
        if (_vm.ActiveBox.BoxType == BoxType.DesktopItems && newType == BoxType.FolderPortal && _vm.ActiveBox.Items.Count > 0)
        {
            var confirmed = await _dialogs.ShowConfirmAsync(
                $"This box contains {_vm.ActiveBox.Items.Count} items. Switch to Folder Portal will move them to the default box. Continue?",
                new DialogOptions { Title = "Switch Box Type", PrimaryButtonText = "Switch", PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Caution });
            if (!confirmed) return;

            // Move items to default via BoxContainerViewModel helper (reuse MoveDesktopItemsToDefault)
            var doomed = _vm.ActiveBox.Model;
            var boxService = App.Services.GetRequiredService<IBoxService>();
            var defaultBox = boxService.GetBoxes().FirstOrDefault(b => b.IsDefault);
            if (defaultBox != null && defaultBox.Id != doomed.Id)
            {
                foreach (var item in doomed.Items.ToList())
                    defaultBox.Items.Add(item);
            }
            doomed.Items.Clear();
            // The BoxViewModel Items will update via CollectionChanged
        }

        _vm.ActiveBox.BoxType = newType;
        if (newType == BoxType.FolderPortal)
        {
            // Ensure view mode valid
            if (_vm.ActiveBox.FolderPath == null) _vm.ActiveBox.FolderPath = null;
        }
        UpdateBody();
        _save();
    }

    private void MenuFolderView_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveBox is null || _vm.ActiveBox.BoxType != BoxType.FolderPortal) return;
        var tag = (sender as FrameworkElement)?.Tag as string;
        _vm.ActiveBox.FolderPortalViewMode = tag == "Details" ? FolderPortalViewMode.Details : FolderPortalViewMode.Icons;
        UpdateBody();
        _save();
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
            ManageFolderWatcher();
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
        ManageFolderWatcher();
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
    private bool _suppressTabDragUntilMouseUp;
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
    private UIElement? _dragContainer;        // ItemContainer ContentPresenter collapsed so TabStripPanel snaps remaining tabs
    private TabDragGhostWindow? _tabGhost;
    private Point _tabGhostOffset;
    private double _dragTabOriginalWidth;
    private double _dragTabOriginalHeight;

    private TabStripPanel? GetTabPanel() => FindVisualChild<TabStripPanel>(TabItems);
    private static TabStripPanel? GetTabPanelFor(BoxContainerWindow w) => FindVisualChild<TabStripPanel>(w.TabItems);

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
        if (_suppressTabDragUntilMouseUp)
        {
            if (e.LeftButton == MouseButtonState.Released) _suppressTabDragUntilMouseUp = false;
            return;
        }

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

            if (!IsActive) return;

            if ((e.GetPosition(this) - _dragStart).Length < SystemParameters.MinimumHorizontalDragDistance)
            {
                return;
            }

            _tabDragging = true;
            Mouse.OverrideCursor = Cursors.SizeAll;
            // Chrome-like: collapse the dragged container so TabStripPanel snaps remaining tabs to fill its space.
            // The ghost shows the dragged tab; panel GapIndex will show insertion gap in target window.
            double ghostW = _dragButton != null && _dragButton.ActualWidth > 0 ? _dragButton.ActualWidth : 120;
            double ghostH = _dragButton != null && _dragButton.ActualHeight > 0 ? _dragButton.ActualHeight : TabStrip.ActualHeight > 0 ? TabStrip.ActualHeight : 28;
            _dragTabOriginalWidth = ghostW;
            _dragTabOriginalHeight = ghostH;
            if (_vm.BoxContainerVm != null)
            {
                int fromIdx = _vm.BoxContainerVm.Tabs.IndexOf(_dragTab!);
                if (fromIdx >= 0 && TabItems.ItemContainerGenerator.ContainerFromIndex(fromIdx) is UIElement c)
                {
                    _dragContainer = c;
                    _dragContainer.Visibility = Visibility.Collapsed;
                }
                else if (_dragButton != null)
                {
                    _dragButton.Visibility = Visibility.Collapsed;
                }
            }
            _tabGhost = new TabDragGhostWindow(_dragTab.Name, _dragTab.IsFolderPortal, ghostW, ghostH);
            _tabGhostOffset = _dragButton != null ? e.GetPosition(_dragButton) : new Point(ghostW / 2, ghostH / 2);
            _tabGhost.Show();
            PositionTabGhost(e);
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
        PositionTabGhost(e);
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

    private void PositionTabGhost(MouseEventArgs e)
    {
        if (_tabGhost == null || _dragButton == null) return;
        if (!Win32Apis.GetCursorPos(out ManualApis.POINT pt)) return;
        var ghostSrc = PresentationSource.FromVisual(_tabGhost);
        if (ghostSrc == null) return;

        // Match ghost width to target tabs if they are wider; restore original in empty area.
        if (_dropWindow != null)
        {
            var targetPanel = GetTabPanelFor(_dropWindow);
            if (targetPanel != null && _dropWindow._vm.BoxContainerVm != null)
            {
                _dropWindow.TabStrip.UpdateLayout();
                int visible = _dropWindow._vm.BoxContainerVm.Tabs.Count;
                // Target shows gap while dragging, so available width for tabs is W - GapWidth
                double stripW = _dropWindow.TabStrip.ActualWidth;
                double gap = targetPanel.GapIndex >= 0 ? targetPanel.GapWidth : 0;
                double targetTabW = visible > 0 ? (stripW - gap) / visible : _dragTabOriginalWidth;
                // Only enlarge (as requested) — keep original width when target tabs are narrower
                double desiredW = targetTabW < _dragTabOriginalWidth ? targetTabW : _dragTabOriginalWidth;
                if (System.Math.Abs(_tabGhost.Width - desiredW) > 0.5)
                    _tabGhost.Width = desiredW;
            }
        }
        else
        {
            if (System.Math.Abs(_tabGhost.Width - _dragTabOriginalWidth) > 0.5)
                _tabGhost.Width = _dragTabOriginalWidth;
        }

        Point cursorGhost = ghostSrc.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y));
        double x = cursorGhost.X - _tabGhostOffset.X;
        double y;
        if (_dropWindow != null)
        {
            _dropWindow.TabStrip.UpdateLayout();
            FrameworkElement tabRef = _dropWindow.TabStrip;
            Point tabScreenPhysical = tabRef.PointToScreen(new Point(0, 0));
            Point tabScreenGhost = ghostSrc.CompositionTarget.TransformFromDevice.Transform(tabScreenPhysical);
            double tabH = tabRef.ActualHeight;
            y = tabScreenGhost.Y + (tabH - _tabGhost.Height) / 2;
            Point stripScreenPhysical = _dropWindow.TabStrip.PointToScreen(new Point(0, 0));
            Point stripGhost = ghostSrc.CompositionTarget.TransformFromDevice.Transform(stripScreenPhysical);
            double stripW = _dropWindow.TabStrip.ActualWidth;
            double minX = stripGhost.X;
            double maxX = stripGhost.X + stripW - _tabGhost.Width;
            x = Math.Max(minX, Math.Min(x, maxX));
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - _tabGhost.Width));
            y = cursorGhost.Y - _tabGhostOffset.Y;
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - _tabGhost.Height));
        }
        _tabGhost.Left = x;
        _tabGhost.Top = y;
    }

    internal double GetTabStripScreenY() => TabStrip.PointToScreen(new Point(0, 0)).Y;

    private void Tab_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragTab == null)
        {
            return;
        }

        if (_tabDragging)
        {
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
            // Chrome-like cleanup: ghost and source gap
            if (_tabGhost != null) { _tabGhost.Close(); _tabGhost = null; }
            if (_dragContainer != null) { _dragContainer.Visibility = Visibility.Visible; _dragContainer = null; }
            else if (_dragButton != null) _dragButton.Visibility = Visibility.Visible;
            _tabDragging = false;
            ClearTabGap();
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
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tabDragging && e.Key == Key.Escape)
        {
            CancelTabDrag();
            e.Handled = true;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Any non-left click cancels tab drag (right/middle/XButton).
        if (_tabDragging && e.ChangedButton != MouseButton.Left)
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

        _suppressTabDragUntilMouseUp = true;
        if (_tabGhost != null) { _tabGhost.Close(); _tabGhost = null; }
        if (_dragContainer != null) { _dragContainer.Visibility = Visibility.Visible; _dragContainer = null; }
        else if (_dragButton != null) _dragButton.Visibility = Visibility.Visible;
        _dragTab = null;
        _tabDragging = false;
        _dragButton = null;
        Mouse.OverrideCursor = null;
        Mouse.Capture(null);
        ClearTabDropVisuals();
        ClearTabGap();
        _debugOverlay?.Hide();
    }

    private System.Windows.Threading.DispatcherTimer? _tabHoverTimer;
    private BoxViewModel? _pendingHoverTab;

    private void TabItem_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DndFormats.BoxItems) && !e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent("Shell IDList Array")) return;
        if (sender is Button { Tag: BoxViewModel vm } && _vm.BoxContainerVm != null)
        {
            _pendingHoverTab = vm;
            StartTabHoverTimer();
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void TabItem_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DndFormats.BoxItems) && !e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent("Shell IDList Array")) return;
        if (sender is Button { Tag: BoxViewModel vm } && _vm.BoxContainerVm != null)
        {
            if (_pendingHoverTab != vm)
            {
                _pendingHoverTab = vm;
                StartTabHoverTimer();
            }
            BringToFrontForDrag();
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void TabItem_DragLeave(object sender, DragEventArgs e)
    {
        // Keep timer - hover will still switch if briefly leaving; cancel only when leaving TabStrip entirely
        // Checked via mouse over TabStrip in DragLeave of window
    }

    private void StartTabHoverTimer()
    {
        _tabHoverTimer?.Stop();
        _tabHoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _tabHoverTimer.Tick += (s, ev) =>
        {
            _tabHoverTimer.Stop();
            if (_pendingHoverTab != null && _vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Contains(_pendingHoverTab))
            {
                int idx = _vm.BoxContainerVm.Tabs.IndexOf(_pendingHoverTab);
                if (idx >= 0 && idx != _vm.BoxContainerVm.SelectedIndex)
                {
                    _vm.BoxContainerVm.SelectedIndex = idx;
                    UpdateBody();
                    Activate();
                }
            }
            _pendingHoverTab = null;
        };
        _tabHoverTimer.Start();
    }

    private async void TabItem_Drop(object sender, DragEventArgs e)
    {
        _tabHoverTimer?.Stop();
        _pendingHoverTab = null;
        if (sender is not Button { Tag: BoxViewModel targetBox } || _vm.BoxContainerVm == null) return;
        int idx = _vm.BoxContainerVm.Tabs.IndexOf(targetBox);
        if (idx >= 0 && idx != _vm.BoxContainerVm.SelectedIndex)
        {
            _vm.BoxContainerVm.SelectedIndex = idx;
            UpdateBody();
            Activate();
        }
        // Direct drop onto tab appends to that box
        if (e.Data.GetDataPresent(DndFormats.BoxItems) || e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("Shell IDList Array"))
        {
            var moved = await DropHelper.AddToBoxAsync(targetBox, _host, e, -1);
            if (e.Handled)
            {
                if (moved != null && moved.Count > 0)
                {
                    foreach (var m in moved)
                    {
                        var vm = targetBox.Items.FirstOrDefault(i => i.Model == m);
                        if (vm != null) vm.IsSelected = true;
                    }
                }
                _save();
            }
        }
        e.Handled = true;
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
                // Do NOT shrink Columns to count-1: UniformGrid with Rows=1 and 3 children + Columns=2
                // wraps the 3rd child to row 1 (clipped by MaxHeight 36) so right tabs "disappear".
                // Keep original binding; the dragged tab's cell stays empty but visible tabs stay in
                // row 0. Gap-fill is handled by animating suffix tabs left (see AnimateTabGap) if needed.
                // Intentionally keep Columns = Tabs.Count to avoid wrap.
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
            // Cursor left the previous target: clear its indicator and gap
            _dropWindow?.HideTabDropIndicator();
            _dropWindow?.ClearTabGap();
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
        target.AnimateTabGap(_tabDropIndex);

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
        _dropWindow?.ClearTabGap();
        _dropWindow?.RestoreTabStripTemp();
        _dropWindow = null;
        _tabDropIndex = -1;
        ClearTabGap();
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
            // GetCursorPos (device px) divided by THIS window's DPI gives true screen DIPs.
            // The old PointToScreen value is device-pixel space and was fed straight into the DIP
            // WorkArea math — on any scaled display the container landed far from the cursor.
            Point p;
            var dpi = VisualTreeHelper.GetDpi(this);
            if (Win32Apis.GetCursorPos(out ManualApis.POINT cp))
            {
                p = new Point(cp.X / dpi.DpiScaleX, cp.Y / dpi.DpiScaleY);
            }
            else
            {
                p = new Point(_lastDragPoint.X / dpi.DpiScaleX, _lastDragPoint.Y / dpi.DpiScaleY);
            }

            var wa = SystemParameters.WorkArea;
            double left = Math.Max(wa.Left, Math.Min(p.X, wa.Right - 240));
            double top = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - 200));
            _host.MoveBoxToNewContainer(box, _vm, left, top);
            _save();
            // Activate the newly created container window.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var newVm = _host.Containers.FirstOrDefault(c => c.BoxContainerVm != null && c.BoxContainerVm.Tabs.Any(t => t.Model == box));
                var newWin = newVm != null ? FindWindowFor(newVm) : null;
                newWin?.Activate();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        if (_dropWindow == this && _vm.BoxContainerVm != null)
        {
            int from = _vm.BoxContainerVm.Tabs.IndexOf(_dragTab);
            _vm.BoxContainerVm.MoveTab(from, _tabDropIndex);
            Activate();
            _save();
        }
        else
        {
            // Merge into another container at the indicated insertion index; make it the active tab.
            var targetVm = _dropWindow._vm;
            _host.MoveBoxToContainer(box, _vm, targetVm, _tabDropIndex);
            // Activate target window so dropped tab selection is visible.
            _dropWindow.Activate();
            // Ensure the moved box becomes active tab in target (MoveBoxToContainer may already, but enforce)
            if (targetVm.BoxContainerVm != null)
            {
                var movedIdx = targetVm.BoxContainerVm.Tabs.IndexOf(_dragTab);
                if (movedIdx >= 0) targetVm.BoxContainerVm.SelectedIndex = movedIdx;
            }
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

            // PointToScreen includes RenderTransform (our gap animation). For hit-testing we need the
            // layout bounds without the animated offset, otherwise thresholds drift as we animate.
            double tx = 0;
            if (container.RenderTransform is TranslateTransform tt) tx = tt.X;
            var sLeft = container.PointToScreen(new Point(-tx, 0)).X;
            var sRight = container.PointToScreen(new Point(container.RenderSize.Width - tx, 0)).X;
            bounds.Add((sLeft, sRight));
            //bounds.Add((topLeft.X, topLeft.X + container.RenderSize.Width));
        }

        return bounds;
    }

    private int ComputeInsertionIndex(Point screenP)
    {
        var bounds = GetVisibleTabBounds(includeDraggingTap: false);
        int index = 0;
        for (int bi = 0; bi < bounds.Count; bi++)
        {
            var (left, right) = bounds[bi];
            var tWidth = right - left;
            bool isLast = bi == bounds.Count - 1;
            // Small gap: most of tab is "before", only near right edge counts as "after".
            // Far-right tab gets larger zone so dropping at end is easy.
            var threshold = right - Math.Min(tWidth * (isLast ? 0.45 : 0.22), isLast ? 32 : 18);
            if (screenP.X > threshold)
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
        // Use actual visible tab bounds so indicator stays aligned even with UniformGrid rounding
        // and small-gap shift (not full tab width). Falls back to estimated layout if not generated.
        var bounds = GetVisibleTabBounds(includeDraggingTap: false);
        double localX;
        if (bounds.Count == 0)
        {
            localX = TabStrip.Padding.Left;
        }
        else if (index <= 0)
        {
            var screenBoundaryX = bounds[0].Left;
            localX = TabStrip.PointFromScreen(new Point(screenBoundaryX, 0)).X;
        }
        else if (index >= bounds.Count)
        {
            var screenBoundaryX = bounds[^1].Right;
            localX = TabStrip.PointFromScreen(new Point(screenBoundaryX, 0)).X - 1.5;
        }
        else
        {
            var screenBoundaryX = bounds[index].Left;
            localX = TabStrip.PointFromScreen(new Point(screenBoundaryX, 0)).X;
        }
        TabDropIndicator.Margin = new Thickness(localX, 0, 0, 0);
        TabDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideTabDropIndicator() => TabDropIndicator.Visibility = Visibility.Collapsed;

    private const double TabGapWidth = 22;
    private int _animatedGapIndex = int.MinValue;

    private void AnimateTabGap(int index)
    {
        // TabStripPanel reserves GapWidth at GapIndex via layout (no TranslateTransform overflow).
        // Layout distributes remaining width uniformly so last tab never clips beyond window.
        if (index == _animatedGapIndex) return;
        _animatedGapIndex = index;
        var panel = GetTabPanel();
        if (panel != null) panel.GapIndex = index;
    }

    private void ClearTabGap()
    {
        _animatedGapIndex = int.MinValue;
        var panel = GetTabPanel();
        if (panel != null) panel.GapIndex = -1;
    }

    private void ClearAllTabGaps()
    {
        foreach (var w in System.Windows.Application.Current.Windows.OfType<BoxContainerWindow>())
            w.ClearTabGap();
    }

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
            // Activate target and select moved box
            Activate();
            if (_vm.BoxContainerVm != null && box != null)
            {
                var tab = _vm.BoxContainerVm.Tabs.FirstOrDefault(t => t.Model == box);
                if (tab != null) _vm.BoxContainerVm.SelectedIndex = _vm.BoxContainerVm.Tabs.IndexOf(tab);
                UpdateBody();
            }
        }
        else
        {
            _host.MergeContainers(source, _vm);
            Activate();
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
