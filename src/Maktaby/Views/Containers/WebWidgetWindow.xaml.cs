using Maktaby.Controls.ContainersControls;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Helpers;
using Maktaby.Native;
using Maktaby.ViewModels;
using Maktaby.Win32.NativeMethods;
using Maktaby.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Views.Containers;

public partial class WebWidgetWindow : WidgetWindow, IWidgetChromeOwner
{
    public override ContainerViewModel ContainerViewModel { get; }
    Window IWidgetChromeOwner.Window => this;
    System.Windows.Controls.MenuItem? IWidgetChromeOwner.LockMenuItem => LockMenuItem;
    private readonly DesktopItemContainer _container;
    private readonly IWebWidgetService _widgetService;
    private WebWidgetControl? _widgetControl;
    /// <summary>The hosted widget control (null before load / after shutdown teardown).
    /// Used by <see cref="DesktopManager"/> for fullscreen auto-suspend.</summary>
    internal WebWidgetControl? WidgetControl => _widgetControl;    //private WindowDragController? _drag;
    private bool _isHover;
    private bool _isActive;
    private HwndSource? _hwndSource;
    private IntPtr _layerHwnd = IntPtr.Zero;
    private HwndSourceHook? _layerHook;
    private WidgetChromeOverlay? _chromeOverlay;
    private readonly DesktopManager _desktopManager;
    private bool ShowChromeOnHover = false;
    /// <summary>Shared tier-0 fallback (freeze the chrome overlay; drop caches if the content
    /// ever has any). See <see cref="Helpers.SoftwareRenderingFallback"/>.</summary>
    private readonly Helpers.SoftwareRenderingFallback _softwareFallback = new();
    //private bool MoveWindowByWidgetMouseDown = false;

    public WebWidgetWindow(ContainerViewModel vm)
    {
        InitializeComponent();
        //DataContext = vm;
        ContainerViewModel = vm;
        _container = vm.Model;
        _widgetService = App.Services.GetRequiredService<IWebWidgetService>();
        _desktopManager = App.Services.GetRequiredService<DesktopManager>();
        var manifest = LoadWidget();
        ApplyManifest(manifest);

        Left = _container.Bounds.X;
        Top = _container.Bounds.Y;
        Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (manifest?.Width ?? 300);
        Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (manifest?.Height ?? 220);

        Title = manifest?.Name ?? _container.WebWidgetName ?? "Widget";

        //UpdateChrome();
        Loaded += OnLoaded;
        // Container bounds are authoritative layout state. SizeChanged/LocationChanged also fire for
        // framework-driven DPI remapping, so they must not overwrite the model here. Explicit user
        // drag/resize paths and DesktopManager commit bounds instead.
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        this.LockMenuItem.IsChecked = vm.IsLocked;
        // Hover/focus over the WebView HWND does not raise WPF hover reliably; use the root border
        // as the single hover source for the whole widget. This avoids the header/host transition churn
        // that caused the chrome to flicker while the pointer crossed between the title bar and the WebView.
        //TitleArea.MouseLeftButtonDown += TitleArea_MouseDown;
        //TitleArea.MouseMove += TitleArea_MouseMove;
        //TitleArea.MouseLeftButtonUp += TitleArea_MouseUp;
        //PreviewMouseLeftButtonDown += TitleArea_MouseDown;
        //MouseMove += TitleArea_MouseMove;
        //MouseUp += TitleArea_MouseUp;

        //RootBorder.MouseEnter += (_, _) => { _isHover = true; UpdateChrome(); };
        //RootBorder.MouseLeave += (_, _) => { _isHover = false; UpdateChrome(); };

        //WidgetMenu.Closed += (_, _) => UpdateChrome();
        //WidgetMenu.Opened += (_, _) => UpdateChrome();
        Activated += (_, _) => { _isActive = true; UpdateChrome(); try { _chromeOverlay?.EnsureAboveOwner(); } catch { } };
        Deactivated += (_, _) => { _isActive = false; _isHover = false; UpdateChrome(); };
    }

    private WebWidgetManifest? LoadWidget()
    {
        var slug = _container.WebWidgetName;
        var source = _container.WebWidgetSource ?? WebWidgetSource.User;
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var info = _widgetService.TryGetWidget(slug, source);
        if (info is null)
        {
            var alt = source == WebWidgetSource.App ? WebWidgetSource.User : WebWidgetSource.App;
            info = _widgetService.TryGetWidget(slug, alt);
        }
        if (info is null)
        {
            WidgetHost.Content = new TextBlock
            {
                Text = $"Widget '{slug}' not found",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };
            return null;
        }
        // Unsubscribe previous control if any (e.g. Change Widget)
        if (_widgetControl != null)
        {
            _widgetControl.WidgetMouseEnter -= OnWidgetMouseEnter;
            _widgetControl.WidgetMouseLeave -= OnWidgetMouseLeave;
            _widgetControl.WidgetClicked -= OnWidgetClicked;
            //_widgetControl.WidgetMouseDown -= OnWidgetMouseDown;
        }
        _widgetControl = new WebWidgetControl();
        _widgetControl.WidgetMouseEnter += OnWidgetMouseEnter;
        _widgetControl.WidgetMouseLeave += OnWidgetMouseLeave;
        _widgetControl.WidgetClicked += OnWidgetClicked;
        //_widgetControl.WidgetMouseDown += OnWidgetMouseDown;
        WidgetHost.Content = _widgetControl;
        _widgetControl.LoadWidget(info.Slug, info.Source);
        return info.Manifest;
    }

    private void ApplyManifest(WebWidgetManifest? manifest)
    {
        bool resizable = manifest?.IsResizable ?? true;
        if (_container.IsLocked) resizable = false;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        if (_chromeOverlay is not null)
        {
            _chromeOverlay.ResizeMode = ResizeMode;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Attach();
    }
    bool _attached;
    private void Attach()
    {
        if (_attached) return;
        _layerHwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_layerHwnd);
        Win32Apis.RegisterBoxWindow(_layerHwnd);
        _layerHook = DesktopLayer.Attach(_layerHwnd, new DesktopLayer.Options
        {
            Kind = DesktopLayer.Kind.Widget,
            DesktopManager = _desktopManager,
            // Don't enforce the pin while the overlay drags — TitleDrag moves overlay+owner via Left/Top
            // with SWP_NOZORDER; the pin would see a pure z-order change and fight the move.
            // (BoxContainerWindow's pin also skips during move, same guard here.)
            KeepBelowSuppressed = () => _chromeOverlay != null && (_chromeOverlay.IsDragging || _chromeOverlay.IsResizing),
            ActiveChanged = active => { _isActive = active; UpdateChrome(); },
            // The overlay must stay directly above this window: showing/raising the owner
            // inserts it at the top and would otherwise bury the overlay behind the widget.
            KeepAbove = () =>
            {
                try { return _chromeOverlay?.Handle ?? IntPtr.Zero; }
                catch { return IntPtr.Zero; }
            },
        });
        _attached = true;
    }

    private void Detach()
    {
        _attached = false;
        DesktopLayer.Detach(_hwndSource, _layerHook);
        _layerHook = null;
        _hwndSource = null;
    }

    public override bool IsSuspended => _widgetControl?.IsSuspended ?? true;

    public override void Suspend()
    {
        try { _widgetControl?.Suspend(); } catch { }
    }

    public override void Resume()
    {
        try { _widgetControl?.Resume(); } catch { }
    }

    /// <summary>The hosted WebView control (null before load / after shutdown teardown).
    /// This is the content visual the tier-0 fallback and the render census act on — not
    /// <c>Window.Content</c>, which is host chrome that is never dropped.</summary>
    internal FrameworkElement? WidgetVisual => WidgetHost?.Content as FrameworkElement;

    /// <summary>
    /// Tier 0 freezes the chrome overlay, and recovery hands it back.
    /// <para>
    /// The overlay is the whole story here. <see cref="WebWidgetControl.Suspend()"/> already
    /// does the right thing with the expensive part — it calls <c>TrySuspendAsync</c>, so the
    /// WebView2 browser process genuinely stops rendering — and the WPF-side content has no
    /// <c>CacheMode</c> to strand. What is left is the overlay: a second top-level WPF window
    /// whose render surface it re-acquires on every Show/Hide and every <c>SyncFromOwner</c>,
    /// all of which a resume's DPI remap triggers across 2 windows per widget. The cache drop
    /// is still routed through the shared helper so a future control with a <c>CacheMode</c>
    /// is covered without touching this file again.
    /// </para></summary>
    public override void OnRenderTierChanged(bool software)
    {
        if (IsClosing || IsClosed) return;
        try { _softwareFallback.Apply(WidgetVisual, _chromeOverlay, software, UpdateChrome); }
        catch { }
    }


    public void ShowWidgetMenu()
    {
        //WidgetMenu.PlacementTarget = MenuButton;
        WidgetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        WidgetMenu.IsOpen = true;
    }

    private void OnWidgetMouseEnter(object? sender, EventArgs e)
    {
        if (!HeaderIsShown() && !_isHover)
        {
            //Debug.WriteLine("[WebWidgetWindow] OnWidgetMouseEnter to show");
            _isHover = true;
            UpdateChrome();
        }
    }
    private void OnWidgetMouseLeave(object? sender, EventArgs e)
    {
        //Debug.WriteLine("[WebWidgetWindow] OnWidgetMouseLeave");
        try
        {
            // If the native cursor is still inside this window's bounds, ignore the DOM 'leave' messages
            // (they can fire when moving between elements inside the WebView). Use physical coordinates
            // which match GetWindowRect and GetCursorPos.
            Win32Apis.GetCursorPos(out var pt);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.GetWindowRect(hwnd, out var rect))
            {
                if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                {
                    //Debug.WriteLine("[WebWidgetWindow] OnWidgetMouseLeave ignored — cursor still inside window");
                    return;
                }
            }
        }
        catch { }

        _isHover = false;
        UpdateChrome();
    }
    private void OnWidgetClicked(object? sender, EventArgs e)
    {
        try { Activate(); } catch { }
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Win32Apis.SetForegroundWindow(hwnd);
        }
        catch { }
        _isActive = true;
        UpdateChrome();
    }

    /*private void OnWidgetMouseDown(object? sender, EventArgs e)
    {
        if (!MoveWindowByWidgetMouseDown || _container.IsLocked) return;
        if (_drag?.IsDragging == true) return;
        // WebView2CompositionControl has no airspace but still captures mouse; DragMove() fails when
        // invoked from async WebMessage (button state lost). Use _drag title loop via synthetic
        // WM_NCLBUTTONDOWN so WindowDragController's snapping (WmMoving/WmSizing) still applies.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                const int WM_NCLBUTTONDOWN = 0x00A1;
                const int HTCAPTION = 2;
                User32.SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
        }
        catch { }
    }*/

    private void ApplyDraggedBounds(RectD bounds)
    {
        _container.Bounds = bounds;
        Left = bounds.X;
        Top = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    public void SetHover(bool hover) { _isHover = hover; UpdateChrome(); }
    /// <summary>Live chrome overlay, if one has been created (used by layout diagnostics).</summary>
    internal WidgetChromeOverlay? ChromeOverlay => _chromeOverlay;
    bool HeaderIsShown() => _chromeOverlay?.IsVisible == true;
    bool CanShowHeader()
    {
        // Check both _isActive (WM_ACTIVATE, set before WPF updates IsActive) and IsActive/IsFocused.
        // ShowChromeOnHover gates hover; when false only activation matters.
        bool show = (ShowChromeOnHover && _isHover) || _isActive || this.IsActive || this.IsFocused || (ShowChromeOnHover && IsMouseOver);
        return show;
    }
    /// <summary>Shows the chrome overlay one dispatcher turn after the decision to show it, so
    /// it is inserted into the z-order after this widget's activation raise settles. Re-checks
    /// the decision first: the state that asked for the chrome may be gone by then.</summary>
    private void ShowChromeOverlay()
    {
        try
        {
            if (_chromeOverlay is null) return;
            if (_chromeOverlay is null)
            {
                EnsureOverlayAboveHost();
                return;
            }
            if (_softwareFallback.IsFrozen || _chromeOverlay.IsDragging || _chromeOverlay.IsResizing) return;
            if (!CanShowHeader()) return;
            _chromeOverlay.Show();
            EnsureOverlayAboveHost();
        }
        catch { }
    }

    public override void UpdateChrome()
    {
        EnsureChromeOverlay();
        //if (_drag is null) return;
        //if (_drag.IsDragging || WindowDragController.IsNativeSizing) return;
        if (_chromeOverlay!.IsDragging || _chromeOverlay.IsResizing) return;
        // Tier 0: the overlay is frozen hidden. Every early-out below must respect that —
        // otherwise the next hover/activation event re-shows it and we are back to
        // re-acquiring a render surface per widget while the driver is degraded.
        if (_softwareFallback.IsFrozen) return;
        // Keep chrome visible while user is interacting with it (mouse down gap before
        // WM_ENTERSIZEMOVE/WM_NCLBUTTONDOWN sets IsDragging). Without this, the
        // WM_ACTIVATE transient during NOACTIVATE caption click would hide it.
        if (_chromeOverlay.IsVisible && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;

        bool show = CanShowHeader();
        // Chrome is now in overlay window glued above WebView (no airspace, title shows over WebView2 HwndHost)
        if (_chromeOverlay != null)
        {
            bool shouldShow = show;
            if (shouldShow)
            {
                _chromeOverlay.SyncFromOwner();
                _chromeOverlay.UpdateTitle(Title);
                if (!_chromeOverlay.IsVisible)
                {
                    if (_chromeOverlay.ResizeMode != ResizeMode) _chromeOverlay.ResizeMode = ResizeMode;
                    // Insert the overlay LAST, not now. Showing it inline loses the race with
                    // this widget's own activation raise and the desktop-band re-pin, both of
                    // which insert the WIDGET at the top of the band AFTER this call returns -
                    // and last inserted wins. That is the whole failure: the overlay was shown,
                    // then the widget was raised over it, and because this window is
                    // WS_EX_LAYERED with a GDI child HWND (the HwndHost WebView2), DWM then
                    // lets that child win over the overlay in some frame orders.
                    // Deferring one dispatcher turn lets the raise settle first, so the overlay
                    // is genuinely the last window inserted. Cost: one turn of latency.
                    Dispatcher.BeginInvoke(new Action(ShowChromeOverlay),
                        System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
                EnsureOverlayAboveHost();
            }
            else if (!shouldShow && _chromeOverlay.IsVisible)
            {
                _chromeOverlay.Hide();
            }
            else if (!shouldShow)
            {
                // Keep a hidden overlay tracking the owner (a DPI transition remaps hidden
                // windows too). Invisible move, no model write — SyncFromOwner only aligns it.
                _chromeOverlay.SyncFromOwner();
            }
        }
    }

    private void EnsureChromeOverlay()
    {
        if (_chromeOverlay != null) return;
        _chromeOverlay = new WidgetChromeOverlay(this);
        _chromeOverlay.UpdateTitle(Title);
        _chromeOverlay.ResizeMode = ResizeMode;
        _chromeOverlay.Deactivated += _chromeOverlay_Deactivated;
        // Visibility controlled by UpdateChrome (ShowChromeOnHover)
    }

    private void _chromeOverlay_Deactivated(object? sender, EventArgs e)
    {
        if (!this.IsActive)
        {
            UpdateChrome();
        }
    }

    internal void EnsureOverlayAboveHost()
    {
        if (_chromeOverlay is null) return;
        var ohwnd = new WindowInteropHelper(_chromeOverlay).Handle;
        if (ohwnd == IntPtr.Zero) return;
        IntPtr ownerH = new WindowInteropHelper(this).Handle;
        if (ownerH == IntPtr.Zero) return;
        User32.SetWindowLongPtr(ohwnd, GWL_HWNDPARENT, ownerH);

        // The owner re-set above does NOT move anything in the z-order; force the overlay
        // directly above the owner so a show/raise that buried it cannot stick.
        try { _chromeOverlay.EnsureAboveOwner(); } catch { }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        //WidgetMenu.PlacementTarget = MenuButton;
        //WidgetMenu.IsOpen = true;
        ShowWidgetMenu();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var slug = _container.WebWidgetName ?? "";
        var source = _container.WebWidgetSource ?? WebWidgetSource.User;
        if (source == WebWidgetSource.App)
        {
            var info = _widgetService.TryGetWidget(slug, source);
            if (info is not null)
            {
                var html = File.Exists(info.HtmlPath) ? File.ReadAllText(info.HtmlPath) : "";
                var css = File.Exists(info.CssPath) ? File.ReadAllText(info.CssPath) : "";
                var js = File.Exists(info.JsPath) ? File.ReadAllText(info.JsPath) : "";
                var newSlug = _widgetService.CreateUserWidget(slug + "_copy", html, css, js, info.Manifest);
                var w2 = new WidgetDataWindow(newSlug, isNew: false);
                w2.ShowDialog();
            }
            return;
        }
        var w = new WidgetDataWindow(slug, isNew: false);
        if (w.ShowDialog() == true)
        {
            _widgetControl?.LoadWidget(slug, source);
            foreach (var win in System.Windows.Application.Current.Windows.OfType<WebWidgetWindow>())
            {
                if (win != this && string.Equals(win._container.WebWidgetName, slug, StringComparison.OrdinalIgnoreCase)
                    && win._container.WebWidgetSource == source)
                    win._widgetControl?.LoadWidget(slug, source);
            }
        }
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var w = new WidgetsListWindow(selectMode: true);
        if (w.ShowDialog() != true) return;
        // Cross-kind pick: a native widget replaces this web widget wholesale at the same bounds.
        if (w.SelectedInfo is null && w.SelectedNativeInfo is not null)
        {
            ReplaceWithNative(w.SelectedNativeInfo);
            return;
        }
        if (w.SelectedInfo is not null)
        {
            _container.WebWidgetName = w.SelectedInfo.Slug;
            _container.WebWidgetSource = w.SelectedInfo.Source;
            var info = w.SelectedInfo;
            Title = info.Manifest.Name ?? info.Slug;
            //TitleText.Text = Title;
            _widgetControl?.LoadWidget(info.Slug, info.Source);
            ApplyManifest(info.Manifest);
            Left = _container.Bounds.X;
            Top = _container.Bounds.Y;
            Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (info.Manifest.Width ?? 300);
            Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (info.Manifest.Height ?? 220);
            try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
            //reload
            var manifest = LoadWidget();
            ApplyManifest(manifest);
            UpdateChrome();
        }
    }

    /// <summary>Replaces this web widget with a native one: the old container is removed
    /// (window closes via the collection change) and the native widget is created at the exact
    /// same bounds — creation auto-opens its window activated.</summary>
    private void ReplaceWithNative(NativeWidgetInfo info)
    {
        var bounds = _container.Bounds;
        var dm = App.Services.GetRequiredService<DesktopManager>();
        var main = App.Services.GetRequiredService<MainViewModel>();
        dm.RemoveContainer(_container.Id);
        main.CreateNativeWidgetAt(info.Slug, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        try { dm.SaveAsyncFireAndForget(); } catch { }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var main = App.Services.GetRequiredService<MainViewModel>();
        main.CreateWebWidgetAt(
            _container.WebWidgetName ?? "widget",
            _container.WebWidgetSource ?? WebWidgetSource.User,
            _container.Bounds.X + 20, _container.Bounds.Y + 20,
            _container.Bounds.Width, _container.Bounds.Height);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _container.IsLocked = !_container.IsLocked;
        LockMenuItem.IsChecked = _container.IsLocked;
        var m = _widgetService.TryGetWidget(_container.WebWidgetName ?? "", _container.WebWidgetSource ?? WebWidgetSource.User)?.Manifest;
        ApplyManifest(m);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        await ContainerHideHint.MaybeShowAsync(this);
        _desktopManager.HideContainer(_container.Id);
        _desktopManager.SaveAsyncFireAndForget();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var dm = App.Services.GetRequiredService<DesktopManager>();
        dm.RemoveContainer(_container.Id);
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Overlay must be closed before owner handle is destroyed — otherwise overlay's
        // hook (pin/SyncOwnerToThis) and OwnerClosed re-entrancy run on half-torn-down owner.
        base.OnClosing(e);
        if (e.Cancel) return;
        var overlay = _chromeOverlay;
        _chromeOverlay = null;
        if (overlay != null)
        {
            try { overlay.Deactivated -= _chromeOverlay_Deactivated; } catch { }
            try { overlay.Owner = null; } catch { }
            try { overlay.Close(); } catch { }
        }
    }

    /// <summary>Detaches and disposes the WebView2 control. Must run BEFORE <see cref="Window.Close"/>:
    /// closing with a live WebView2 inside throws InvalidOperationException ("Notification Window
    /// is null") from HwndHost teardown mid-close. Idempotent — also called from <see cref="OnClosed"/>
    /// as a fallback for direct closes.</summary>
    public void PrepareForClose() => ShutdownWebView();

    /// <summary>Prevent CoreWebView2Controller.IsVisible race on shutdown:
    /// detach events, collapse and dispose WebView before the Window visual tree is torn down.</summary>
    private void ShutdownWebView()
    {
        if (_widgetControl != null)
        {
            try { _widgetControl.WidgetMouseEnter -= OnWidgetMouseEnter; } catch { }
            try { _widgetControl.WidgetMouseLeave -= OnWidgetMouseLeave; } catch { }
            try { _widgetControl.WidgetClicked -= OnWidgetClicked; } catch { }
            //try { _widgetControl.WidgetMouseDown -= OnWidgetMouseDown; } catch { }
            try { _widgetControl.CleanupForShutdown(); } catch { }
            // Any dropped cache mode belonged to the WebView visual that is going away.
            try { WidgetHost.Content = null; } catch { }
            _softwareFallback.ForgetCaches();
            _widgetControl = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // WebView teardown already ran in PrepareForClose (called before Close by the manager);
        // this is the fallback for direct closes so it never runs twice.
        ShutdownWebView();
        // Overlay already closed in OnClosing — defensive null check only
        _chromeOverlay = null;
        if (_layerHwnd != IntPtr.Zero) try { Win32Apis.UnregisterBoxWindow(_layerHwnd); } catch { }
        try { Detach(); } catch { }
        try { base.OnClosed(e); } catch { }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) _widgetControl?.Suspend();
        else if (WindowState != WindowState.Minimized) _widgetControl?.Resume();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _widgetControl?.Suspend();
        else if (IsVisible) _widgetControl?.Resume();
    }

    private void WidgetMenu_Opened(object sender, RoutedEventArgs e)
    {
        WidgetMenuEdit.Visibility = ContainerViewModel.WebWidgetSource == WebWidgetSource.App ? Visibility.Collapsed : Visibility.Visible;
    }
}
