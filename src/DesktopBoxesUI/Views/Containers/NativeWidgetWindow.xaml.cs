using DesktopBoxes.WidgetSdk;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32.Services;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WindowsNative;
using Wpf.Ui.Appearance;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI.Views.Containers;

/// <summary>Host window for a native plugin widget (<see cref="DesktopItemContainerType.NativeWidget"/>).
/// Mirrors <see cref="WebWidgetWindow"/>: borderless tool window glued to the desktop layer with an
/// external <see cref="WidgetChromeOverlay"/> (shared via <see cref="IWidgetChromeOwner"/>),
/// manifest-driven resize behavior, visibility-driven plugin suspend/resume, and hybrid
/// interaction detection (WPF routed events on the plugin visual merged with the plugin's
/// opt-in <see cref="INativeWidget"/> events).</summary>
public partial class NativeWidgetWindow : WidgetWindow, IWidgetChromeOwner
{
    public override ContainerViewModel ContainerViewModel { get; }
    Window IWidgetChromeOwner.Window => this;
    MenuItem? IWidgetChromeOwner.LockMenuItem => LockMenuItem;

    private readonly DesktopItemContainer _container;
    private readonly INativeWidgetService _widgetService;
    private INativeWidget? _plugin;
    private NativeWidgetInfo? _info;
    private int _loadGeneration;
    private WidgetChromeOverlay? _chromeOverlay;
    private IntPtr _layerHwnd = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private HwndSourceHook? _layerHook;
    private bool _isHover;
    private bool _isActive;
    private readonly DesktopManager _desktopManager;
    private bool ShowChromeOnHover = false;

    /// <summary>Live plugin instance, if one is attached (used by auto-pause/diagnostics).</summary>
    internal INativeWidget? Widget => _plugin;
    /// <summary>Live chrome overlay, if one has been created (used by layout diagnostics).</summary>
    internal WidgetChromeOverlay? ChromeOverlay => _chromeOverlay;

    public NativeWidgetWindow(ContainerViewModel vm)
    {
        InitializeComponent();
        ContainerViewModel = vm;
        _container = vm.Model;
        _widgetService = App.Services.GetRequiredService<INativeWidgetService>();
        _desktopManager = App.Services.GetRequiredService<DesktopManager>();

        // Manifest-only resolve here (fast, loads no code); the plugin itself loads async.
        var info = TryResolveInfo();
        ApplyManifest(info?.Manifest);
        Title = string.IsNullOrWhiteSpace(info?.Manifest.Name) ? (info?.Slug ?? "Widget") : info.Manifest.Name;

        Left = _container.Bounds.X;
        Top = _container.Bounds.Y;
        Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : (info?.Manifest.Width ?? 300);
        Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : (info?.Manifest.Height ?? 220);

        LoadNativeWidgetAsync();

        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        try { ApplicationThemeManager.Changed += OnAppThemeChanged; } catch { }
        this.LockMenuItem.IsChecked = vm.IsLocked;
        Activated += (_, _) => { _isActive = true; UpdateChrome(); try { _chromeOverlay?.EnsureAboveOwner(); } catch { } };
        Deactivated += (_, _) => { _isActive = false; _isHover = false; UpdateChrome(); };
    }

    private NativeWidgetInfo? TryResolveInfo()
    {
        try { return _widgetService.TryGetWidget(_container.NativeWidgetName ?? ""); }
        catch { return null; }
    }

    private void LoadNativeWidgetAsync()
    {
        int generation = Interlocked.Increment(ref _loadGeneration);
        _ = LoadNativeWidgetCoreAsync(generation);
    }

    private async Task LoadNativeWidgetCoreAsync(int generation)
    {
        ShowPlaceholder("Loading…", error: false);
        var info = TryResolveInfo();
        if (Volatile.Read(ref _loadGeneration) != generation) return;
        if (info is null)
        {
            ShowPlaceholder($"Native widget '{_container.NativeWidgetName}' not found");
            return;
        }
        if (info.LoadError is not null)
        {
            ShowPlaceholder(info.LoadError);
            return;
        }
        _info = info;
        Title = string.IsNullOrWhiteSpace(info.Manifest.Name) ? info.Slug : info.Manifest.Name;
        ApplyManifest(info.Manifest);

        // Fail closed: untrusted user content never compiles or loads. App widgets are
        // implicitly trusted; user widgets are trusted once per content hash via the
        // gallery Place prompt.
        bool trusted = false;
        try { trusted = _widgetService.IsTrusted(info); } catch { }
        if (!trusted)
        {
            ShowPlaceholder($"Untrusted plugin '{info.Slug}'. Place it from the widget gallery to review and trust it.");
            return;
        }

        INativeWidget plugin;
        try
        {
            // Compile off the UI thread (can take seconds), instantiate on it (WPF
            // requires STA for visual construction — Task.Run would throw).
            string assemblyPath = await Task.Run(() => _widgetService.GetAssemblyPath(info));
            if (Volatile.Read(ref _loadGeneration) != generation) return;
            plugin = _widgetService.CreateInstance(assemblyPath, info);
        }
        catch (NativeWidgetLoadException ex) { ShowPlaceholder(ex.Reason); return; }
        catch (Exception ex) { ShowPlaceholder(ex.Message); return; }
        if (Volatile.Read(ref _loadGeneration) != generation)
        {
            try { plugin.Dispose(); } catch { }
            return;
        }
        AttachPlugin(plugin);
    }

    private void ShowPlaceholder(string text)
    {
        ShowPlaceholder(text, error: true);
    }

    private void ShowPlaceholder(string text, bool error)
    {
        try
        {
            WidgetHost.Content = new TextBlock
            {
                Text = text,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12),
                Foreground = error
                    ? System.Windows.Media.Brushes.IndianRed
                    : System.Windows.Media.Brushes.Gray,
            };
        }
        catch { }
    }

    private void AttachPlugin(INativeWidget plugin)
    {
        DetachPlugin();
        _plugin = plugin;
        FrameworkElement visual;
        try
        {
            visual = plugin.Visual;
            if (visual is null) throw new InvalidOperationException("Plugin returned a null visual.");
        }
        catch (Exception ex)
        {
            ShowPlaceholder(ex.Message);
            try { plugin.Dispose(); } catch { }
            _plugin = null;
            return;
        }
        try
        {
            // Host-detected half of the hybrid interaction model (works with zero plugin code).
            visual.MouseEnter += OnVisualMouseEnter;
            visual.MouseLeave += OnVisualMouseLeave;
            visual.PreviewMouseLeftButtonUp += OnVisualClicked;
            visual.GotFocus += OnVisualFocused;
            visual.LostFocus += OnVisualLostFocus;
            // Plugin-raised half (covers HWND-hosted content the WPF tree cannot see).
            plugin.Entered += OnPluginEntered;
            plugin.Left += OnPluginLeft;
            plugin.Clicked += OnPluginClicked;
            plugin.Focused += OnPluginFocused;
            plugin.Unfocused += OnPluginUnfocused;
            WidgetHost.Content = visual;
        }
        catch (Exception ex)
        {
            ShowPlaceholder(ex.Message);
            try { plugin.Dispose(); } catch { }
            _plugin = null;
            return;
        }
        ApplyThemeToPlugin();
        // Settings load after the visual exists: declared defaults show first, stored values
        // apply through ValueChanged (widgets cache them there).
        try
        {
            if (plugin is DesktopBoxes.WidgetSdk.IWidgetSettingsProvider provider && _info != null)
            {
                App.Services.GetRequiredService<INativeWidgetSettingsService>().LoadInto(_info, provider);
            }
        }
        catch { }
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            try { plugin.Suspend(); } catch { }
        }
    }

    private void DetachPlugin()
    {
        var plugin = Interlocked.Exchange(ref _plugin, null);
        if (plugin is null) return;
        try
        {
            FrameworkElement? visual = null;
            try { visual = plugin.Visual; } catch { }
            if (visual is not null)
            {
                visual.MouseEnter -= OnVisualMouseEnter;
                visual.MouseLeave -= OnVisualMouseLeave;
                visual.PreviewMouseLeftButtonUp -= OnVisualClicked;
                visual.GotFocus -= OnVisualFocused;
                visual.LostFocus -= OnVisualLostFocus;
            }
            plugin.Entered -= OnPluginEntered;
            plugin.Left -= OnPluginLeft;
            plugin.Clicked -= OnPluginClicked;
            plugin.Focused -= OnPluginFocused;
            plugin.Unfocused -= OnPluginUnfocused;
        }
        catch { }
        try { WidgetHost.Content = null; } catch { }
        try { plugin.Dispose(); } catch { }
    }

    private void ApplyManifest(NativeWidgetManifest? manifest)
    {
        bool resizable = manifest?.Resizable ?? true;
        if (_container.IsLocked) resizable = false;
        ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        if (_chromeOverlay is not null)
        {
            _chromeOverlay.ResizeMode = ResizeMode;
        }
    }

    private void ApplyThemeToPlugin()
    {
        var plugin = _plugin;
        if (plugin is null || _info?.Manifest.SupportsTheme != true) return;
        try { plugin.ApplyTheme(ResolveTheme()); } catch { }
    }

    private string? ResolveTheme()
    {
        string? global = null;
        try { global = App.Services.GetRequiredService<ISettingsService>().UserSettings.DefaultWebWidgetsTheme?.Trim().ToLowerInvariant(); } catch { }
        if (global == "dark") return "dark";
        if (global == "light") return "light";
        try
        {
            var app = ApplicationThemeManager.GetAppTheme();
            if (app == ApplicationTheme.Dark) return "dark";
            if (app == ApplicationTheme.Light) return "light";
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? "dark" : "light";
        }
        catch { return null; }
    }

    private void OnAppThemeChanged(ApplicationTheme current, System.Windows.Media.Color systemAccent)
    {
        if (_info?.Manifest.SupportsTheme != true) return;
        try { Dispatcher.BeginInvoke(ApplyThemeToPlugin); } catch { }
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
        _layerHook = DesktopLayer.Attach(this, new DesktopLayer.Options
        {
            Kind = DesktopLayer.Kind.Widget,
            DesktopManager = _desktopManager,
            // Don't enforce the pin while the overlay drags — TitleDrag moves overlay+owner via
            // Left/Top with SWP_NOZORDER; the pin would see a pure z-order change and fight the move.
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

    public void ShowWidgetMenu()
    {
        WidgetMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        WidgetMenu.IsOpen = true;
    }

    // ---- interaction: WPF-routed half ----
    private void OnVisualMouseEnter(object sender, MouseEventArgs e) => SetHovered(true);
    private void OnVisualMouseLeave(object sender, MouseEventArgs e) => SetHovered(false);
    private void OnVisualClicked(object sender, MouseButtonEventArgs e) => ActivateWidget();
    private void OnVisualFocused(object sender, RoutedEventArgs e) => UpdateChrome();
    private void OnVisualLostFocus(object sender, RoutedEventArgs e) => UpdateChrome();

    // ---- interaction: plugin-raised half ----
    private void OnPluginEntered(object? sender, EventArgs e) => SetHovered(true);
    private void OnPluginLeft(object? sender, EventArgs e)
    {
        // Same guard as the CSS window: ignore leaves while the cursor is physically
        // still inside (plugins can fire them moving between inner elements).
        try
        {
            Win32Apis.GetCursorPos(out var pt);
            if (_layerHwnd != IntPtr.Zero && Win32Apis.GetWindowRect(_layerHwnd, out var rect))
            {
                if (pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom)
                    return;
            }
        }
        catch { }
        SetHovered(false);
    }
    private void OnPluginClicked(object? sender, EventArgs e) => ActivateWidget();
    private void OnPluginFocused(object? sender, EventArgs e) => UpdateChrome();
    private void OnPluginUnfocused(object? sender, EventArgs e) => UpdateChrome();

    private void SetHovered(bool hover)
    {
        if (_isHover == hover) return;
        _isHover = hover;
        UpdateChrome();
    }

    private void ActivateWidget()
    {
        try { Activate(); } catch { }
        try
        {
            if (_layerHwnd != IntPtr.Zero) Win32Apis.SetForegroundWindow(_layerHwnd);
        }
        catch { }
        _isActive = true;
        UpdateChrome();
    }

    public void SetHover(bool hover) { _isHover = hover; UpdateChrome(); }
    bool HeaderIsShown() => _chromeOverlay?.IsVisible == true;
    bool CanShowHeader()
    {
        bool show = (ShowChromeOnHover && _isHover) || _isActive || this.IsActive || this.IsFocused || (ShowChromeOnHover && IsMouseOver);
        return show;
    }
    public override void UpdateChrome()
    {
        EnsureChromeOverlay();
        if (_chromeOverlay!.IsDragging || _chromeOverlay.IsResizing) return;
        // Keep chrome visible while user is interacting with it (mouse down gap before
        // WM_ENTERSIZEMOVE/WM_NCLBUTTONDOWN sets IsDragging).
        if (_chromeOverlay.IsVisible && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;

        bool show = CanShowHeader();
        if (_chromeOverlay != null)
        {
            if (show)
            {
                _chromeOverlay.SyncFromOwner();
                _chromeOverlay.UpdateTitle(Title);
                if (!_chromeOverlay.IsVisible)
                {
                    if (_chromeOverlay.ResizeMode != ResizeMode) _chromeOverlay.ResizeMode = ResizeMode;
                    _chromeOverlay.Show();
                }
                EnsureOverlayAboveHost();
            }
            else if (_chromeOverlay.IsVisible)
            {
                _chromeOverlay.Hide();
            }
            else
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

    private void WidgetMenu_Opened(object sender, RoutedEventArgs e)
    {
        // Settings entry only when the loaded plugin actually exposes settings.
        bool hasSettings = false;
        try { hasSettings = _plugin is DesktopBoxes.WidgetSdk.IWidgetSettingsProvider p && p.Settings.Count > 0; } catch { }
        SettingsMenuItem.Visibility = hasSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_plugin is not DesktopBoxes.WidgetSdk.IWidgetSettingsProvider provider || provider.Settings.Count == 0) return;
        if (_info is null) return;
        try
        {
            var svc = App.Services.GetRequiredService<INativeWidgetSettingsService>();
            var w = new Views.AppWindows.WidgetSettingsWindow(_info, provider, svc) { Owner = this };
            w.ShowDialog();
        }
        catch { }
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var w = new WidgetsListWindow(selectMode: true);
        if (w.ShowDialog() != true) return;
        // Cross-kind pick: a web widget replaces this native widget wholesale at the same bounds.
        if (w.SelectedNativeInfo is null && w.SelectedInfo is not null)
        {
            ReplaceWithWeb(w.SelectedInfo);
            return;
        }
        if (w.SelectedNativeInfo is not null)
        {
            var info = w.SelectedNativeInfo;
            _container.NativeWidgetName = info.Slug;
            Title = string.IsNullOrWhiteSpace(info.Manifest.Name) ? info.Slug : info.Manifest.Name;
            Left = _container.Bounds.X;
            Top = _container.Bounds.Y;
            Width = _container.Bounds.Width > 0 ? _container.Bounds.Width : info.Manifest.Width;
            Height = _container.Bounds.Height > 0 ? _container.Bounds.Height : info.Manifest.Height;
            try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
            LoadNativeWidgetAsync();
            UpdateChrome();
        }
    }

    /// <summary>Replaces this native widget with a web one: the old container is removed
    /// (window closes via the collection change) and the web widget is created at the exact
    /// same bounds — creation auto-opens its window activated.</summary>
    private void ReplaceWithWeb(WebWidgetInfo info)
    {
        var bounds = _container.Bounds;
        var dm = App.Services.GetRequiredService<DesktopManager>();
        var main = App.Services.GetRequiredService<MainViewModel>();
        dm.RemoveContainer(_container.Id);
        main.CreateWebWidgetAt(info.Slug, info.Source, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        try { dm.SaveAsyncFireAndForget(); } catch { }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var main = App.Services.GetRequiredService<MainViewModel>();
        main.CreateNativeWidgetAt(
            _container.NativeWidgetName ?? "widget",
            _container.Bounds.X + 20, _container.Bounds.Y + 20,
            _container.Bounds.Width, _container.Bounds.Height);
        try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _container.IsLocked = !_container.IsLocked;
        LockMenuItem.IsChecked = _container.IsLocked;
        ApplyManifest(_info?.Manifest);
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

    /// <summary>Detaches and disposes the plugin. Must run BEFORE <see cref="Window.Close"/>:
    /// mirrors the WebView2 shutdown ordering (control out of the visual tree first).
    /// Idempotent — also called from <see cref="OnClosed"/> as a fallback.</summary>
    public void PrepareForClose() => ShutdownPlugin();

    private void ShutdownPlugin()
    {
        Interlocked.Increment(ref _loadGeneration); // invalidate in-flight loads
        try { ApplicationThemeManager.Changed -= OnAppThemeChanged; } catch { }
        DetachPlugin();
    }

    protected override void OnClosed(EventArgs e)
    {
        ShutdownPlugin();
        _chromeOverlay = null;
        if (_layerHwnd != IntPtr.Zero) try { Win32Apis.UnregisterBoxWindow(_layerHwnd); } catch { }
        try { Detach(); } catch { }
        try { base.OnClosed(e); } catch { }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) _plugin?.Suspend();
        else if (WindowState != WindowState.Minimized) _plugin?.Resume();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _plugin?.Suspend();
        else if (IsVisible) _plugin?.Resume();
    }
}
