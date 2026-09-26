using DesktopBoxes.WidgetSdk;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Views.Containers;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.Win32APIs.Services;
using DesktopBoxesUI.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using WindowsNative;
using WindowsNative.Playback;
using static WindowsNative.Win32Constants;

namespace DesktopBoxesUI;

/// <summary>
/// Orchestrates the live container windows. One window per <see cref="ContainerViewModel"/>
/// (i.e. per <see cref="DesktopItemContainer"/>): a <see cref="BoxContainerWindow"/> for
/// <see cref="DesktopItemContainerType.BoxContainer"/>, a <see cref="BoxContainerWindow"/> for
/// <see cref="DesktopItemContainerType.Custom"/>. Keeps the windows in sync with the view-model
/// collection, owns loading/saving the snapshot (including desktop resolution for rescaling), and
/// hides/restores Explorer's desktop icons. Dependencies come from the composition root.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopManager
{
    #region fields

    private readonly MainViewModel _mainVm;
    private readonly IContainerService _containers;
    private readonly IPersistenceService _persistence;
    private readonly IDesktopService _desktop;
    private readonly IWindowPositioningService _positioning;
    private readonly IExplorerDesktopService _explorer;
    private readonly IRuleService _rules;
    private readonly IBoxService _boxRegistry;
    private readonly IFileRuleCoordinator _coordinator;
    private readonly IMouseMonitor _mouseMonitor;
    private readonly ISettingsService _settingsService;
    private readonly IMonitorService _monitors;
    private readonly IDispatcher _dispatcher;

    private readonly Dictionary<System.Guid, Window> _windows = new();
    /// <summary>Fullscreen/system auto-pause for widget WebViews (shared engine with live
    /// wallpapers). Created in <see cref="InitializeAsync"/>, torn down in
    /// <see cref="CloseAll"/>; transitions marshal to the UI thread.</summary>
    private PlaybackSupervisor? _widgetPause;
    private DesktopSurface? _surface;
    /// <summary>Owns the above-icons widget layer (probe/attach/watch) on the custom-surface
    /// feature path. The surface window is the layer window; boxes/widgets are owned by it.</summary>
    private DesktopWidgetLayerHost? _widgetLayerHost;
    private bool _allBoxesHidden;

    // WinEvent watch for desktop-icon show/hide toggles (e.g. via Explorer's own menu):
    // the hook handle, the rooted callback delegate, and the list-view handle it filters on.
    private IntPtr _iconWatchHook;
    private WinEventProc? _iconWatchProc;
    private IntPtr _iconWatchListView;

    /// <summary>When true all app functionality is suspended: boxes, surface, watchers and shell
    /// hooks are stopped. Only the tray icon remains. <see cref="ToggleDisableAsync"/> flips it.</summary>
    public bool IsDisabled { get; private set; }

    /// <summary>The work-area resolution the current container layout was computed against. When the
    /// display settings change we rescale every container proportionally against this baseline.</summary>
    private RectD _appliedResolution;

    private int _layoutUpdateDepth;

    private string _lastPassSummary = "no rescale pass yet";

    /// <summary>True while <see cref="RescaleToCurrent"/> is applying authoritative container bounds.
    /// Framework-driven geometry echoes must not overwrite the model during this scope.</summary>
    internal bool IsLayoutUpdateActive => _layoutUpdateDepth > 0;

    internal IDisposable BeginLayoutUpdate() => new LayoutUpdateScope(this);

    private sealed class LayoutUpdateScope : IDisposable
    {
        private DesktopManager? _owner;

        public LayoutUpdateScope(DesktopManager owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._layoutUpdateDepth);
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._layoutUpdateDepth);
            }
        }
    }

    #endregion

    #region Init

    public DesktopManager(IServiceProvider provider)
    {
        _mainVm = provider.GetRequiredService<MainViewModel>();
        _containers = provider.GetRequiredService<IContainerService>();
        _persistence = provider.GetRequiredService<IPersistenceService>();
        _desktop = provider.GetRequiredService<IDesktopService>();
        _positioning = provider.GetRequiredService<IWindowPositioningService>();
        _explorer = provider.GetRequiredService<IExplorerDesktopService>();
        _rules = provider.GetRequiredService<IRuleService>();
        _boxRegistry = provider.GetRequiredService<IBoxService>();
        _coordinator = provider.GetRequiredService<IFileRuleCoordinator>();
        _mouseMonitor = provider.GetRequiredService<IMouseMonitor>();
        _settingsService = provider.GetRequiredService<ISettingsService>();
        _monitors = provider.GetRequiredService<IMonitorService>();
        _dispatcher = provider.GetRequiredService<IDispatcher>();
    }

    public async Task InitializeAsync()
    {
        //the init can be called multiple times so
        _coordinator.Stop();
        _mouseMonitor.Stop();
        //
        await _explorer.SetDesktopIconsVisibleAsync(false);
        var snapshot = await _persistence.LoadSnapshotAsync();
        if (snapshot is not { Containers.Count: > 0 })
        {
            await BuildDefaultContainerAsync();
            RegisterAllBoxes();
            _rules.EnsureDefaultRule(DefaultBoxId());
            //RescaleIfNeeded(GetCurrentDesktopSnapshot());
        }
        else
        {
            RescaleIfNeeded(snapshot);
            foreach (var container in snapshot.Containers)
            {
                _containers.AddContainer(container);
            }

            RegisterAllBoxes();

            if (snapshot.Rules?.Count > 0)
            {
                _rules.LoadRules(snapshot.Rules);
            }

            EnsureDefaultRuleAndBox();
        }

        // Build the container view-models off the UI thread (this also kicks off the per-item icon loads)
        // so the splash keeps animating, then add the already-built VMs on the UI thread. The
        // CollectionChanged handler is detached so we don't synchronously spin up windows during the load.
        _mainVm.Containers.CollectionChanged -= Containers_CollectionChanged;
        _mainVm.Containers.Clear();
        var builtVms = await Task.Run(() => _mainVm.BuildContainerViewModels(_containers.GetContainers()));
        foreach (var vm in builtVms)
        {
            _mainVm.Containers.Add(vm);
        }

        _mainVm.Containers.CollectionChanged += Containers_CollectionChanged;

        EnsureSurface();
        StartIconVisibilityWatch();

        // Show the first container right away so the desktop isn't empty, then lazily stream the
        // remaining container windows in the background (after the splash closes) for an instant startup.
        // This defers window creation only — item drag/drop logic is untouched.
        if (_mainVm.Containers.Count > 0)
        {
            AddWindow(_mainVm.Containers[0], showActivated: false);//focusWorkaround: true
            EnsureDesktopZOrder();
        }

        _ = StreamRemainingContainersAsync(onFinishAction: StartWidgetAutoPause);

        await ReconcileItemsAsync();

        _appliedResolution = GetPrimaryWorkAreaDip();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        //
        _coordinator.Start();
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == true) _mouseMonitor.Start();
        //StartWidgetAutoPause();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>
    /// Toggles between fully operational and suspended. When disabled, all boxes, the surface,
    /// shell watchers and mouse hooks are stopped — only the tray icon remains. When re-enabled
    /// the app is restored via <see cref="InitializeAsync"/>.
    /// </summary>
    public async Task ToggleDisableAsync()
    {
        if (IsDisabled)
        {
            // --- Re-enable: full initialization restores boxes, surface and watchers ---
            IsDisabled = false;
            App.SyncDesktopIconSizeWatcher();
            await InitializeAsync();
        }
        else
        {
            // --- Disable: save state, stop everything, close all windows except tray host ---
            IsDisabled = true;

            SaveSync();
            _mouseMonitor.Stop();
            App.Services.GetRequiredService<Core.Interfaces.IDesktopIconSizeService>().Stop();
            CloseAll();
            RestoreIcons();
        }
    }

    #endregion


    #region Scale

    /// <summary>
    /// If the saved desktop resolution differs from the current one, proportionally rescale every
    /// container's bounds so the layout is preserved. BoxContainer-type bounds live on the wrapped
    /// <see cref="BoxContainer"/>; Custom-type bounds live on the container itself.
    /// </summary>
    private void RescaleIfNeeded(DesktopSnapshot snapshot)
    {
        if (snapshot.DesktopResolution.Width <= 0 || snapshot.DesktopResolution.Height <= 0)
        {
            return;
        }

        var current = GetPrimaryWorkAreaDip();

        double maxRight = 0;
        double maxBottom = 0;
        foreach (var container in snapshot.Containers)
        {
            maxRight = Math.Max(maxRight, container.Bounds.Right);
            maxBottom = Math.Max(maxBottom, container.Bounds.Bottom);
        }

        // Older snapshots stored DesktopResolution in PHYSICAL pixels while bounds are DIPs. If the
        // stored resolution is clearly larger than the current DIP work area yet the bounds already
        // fit inside it, the bounds are correctly placed in DIP space — just re-baseline (the
        // resolution is rewritten correctly on the next save) instead of wrongly shrinking them.
        bool storedLooksPhysical = snapshot.DesktopResolution.Width > current.Width * 1.15;
        if (storedLooksPhysical && maxRight <= current.Width && maxBottom <= current.Height)
        {
            return;
        }

        if (current.Width == snapshot.DesktopResolution.Width &&
            current.Height == snapshot.DesktopResolution.Height)
        {
            return;
        }

        double sx = current.Width / snapshot.DesktopResolution.Width;
        double sy = current.Height / snapshot.DesktopResolution.Height;

        foreach (var container in snapshot.Containers)
        {
            var b = container.Bounds;
            container.Bounds = RectD.FromXYWH(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
        }
    }

    private const double MinContainerWidth = 160;
    private const double MinContainerHeight = 120;

    /// <summary>Clamps a rescaled width/height to the window minimums and the work-area extent.</summary>
    private static double ClampRescaledExtent(double value, double workExtent, double min)
    {
        if (workExtent <= 0) return Math.Max(min, value);
        return Math.Clamp(value, min, Math.Max(min, workExtent));
    }

    /// <summary>Clamps a rescaled left/top so the (already-clamped) window stays inside the work area.</summary>
    private static double ClampRescaledOrigin(double value, double workStart, double workEnd, double extent)
    {
        double max = workEnd - extent;
        if (max <= workStart) return workStart;
        return Math.Clamp(value, workStart, max);
    }

    /// <summary>
    /// The primary work area in WPF logical (DIP) coordinates. Container <c>Bounds</c> are always stored
    /// in this same space (the WPF window geometry), so this is the correct basis for the persisted
    /// <see cref="DesktopSnapshot.DesktopResolution"/> and for rescaling.
    /// Computed from live physical pixels + live monitor DPI on every call: unlike
    /// <see cref="SystemParameters.WorkArea"/> (a WPF cache that only refreshes when the UI thread
    /// pumps the broadcast) this cannot report pre-change values after a scale switch.
    /// </summary>
    internal static RectD GetPrimaryWorkAreaDip()
    {
        var previous = PinMonitorAwareness();
        try
        {
            return GetPrimaryWorkAreaDipCore();
        }
        finally
        {
            RestoreAwareness(previous);
        }
    }

    /// <summary>Live primary-monitor DPI, pinned like the area query. Falls back to 96.</summary>
    internal static uint GetPrimaryDpi()
    {
        var previous = PinMonitorAwareness();
        try
        {
            var hmon = Win32Apis.MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            if (hmon != IntPtr.Zero
                && Win32Apis.TryGetDpiForMonitor(hmon, out uint dpi, out _)
                && dpi > 0)
            {
                return dpi;
            }
        }
        catch { }
        finally
        {
            RestoreAwareness(previous);
        }
        return 96;
    }

    private static IntPtr PinMonitorAwareness()
    {
        // GetMonitorInfoW virtualizes rects for non-per-monitor threads, and this process has a
        // known path that flips the UI thread to system-aware (the legacy file dialog does not
        // reliably restore it). A virtualized read here would silently corrupt the rescale
        // baseline and every clamp/snapshot derived from it.
        var previous = User32.GetThreadDpiAwarenessContext();
        if (User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == IntPtr.Zero)
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
        return previous;
    }

    private static void RestoreAwareness(IntPtr previous)
    {
        if (previous != IntPtr.Zero)
        {
            try { User32.SetThreadDpiAwarenessContext(previous); } catch { }
        }
    }

    private static RectD GetPrimaryWorkAreaDipCore()
    {
        try
        {
            var hmon = Win32Apis.MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
            if (hmon != IntPtr.Zero)
            {
                MONITORINFO mi = default;
                mi.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>();
                if (Win32Apis.GetMonitorInfo(hmon, ref mi) &&
                    Win32Apis.TryGetDpiForMonitor(hmon, out uint dpi, out _))
                {
                    double s = 96.0 / (dpi > 0 ? dpi : 96);
                    var w = mi.Work;
                    return RectD.FromXYWH(w.Left * s, w.Top * s, (w.Right - w.Left) * s, (w.Bottom - w.Top) * s);
                }
            }
        }
        catch { }
        var wa = SystemParameters.WorkArea;
        return RectD.FromXYWH(wa.X, wa.Y, wa.Width, wa.Height);
    }

    private static bool SameArea(in RectD a, in RectD b) =>
        a.Width == b.Width && a.Height == b.Height && a.X == b.X && a.Y == b.Y;

    /// <summary>
    /// One-line layout-anchor state for diagnostics UI (Performance Monitor): the baseline the
    /// current layout was computed against, the live primary area + DPI right now, and what the
    /// last rescale decision was. If baseline and live disagree with no recent pass, scheduling
    /// (not math) is the suspect.
    /// </summary>
    public string GetLayoutAnchorStatus()
    {
        var live = GetPrimaryWorkAreaDip();
        uint dpi = GetPrimaryDpi();
        return $"baseline=({_appliedResolution.X:0},{_appliedResolution.Y:0} {_appliedResolution.Width:0}x{_appliedResolution.Height:0})"
            + $" live=({live.X:0},{live.Y:0} {live.Width:0}x{live.Height:0}) @{dpi}dpi | {_lastPassSummary}";
    }

    /// <summary>Scales one bounds rect from an old primary area to a new one (position relative
    /// to the area origin, then size), clamped into the new area.</summary>
    private static (double Left, double Top, double Width, double Height) ScaleBounds(
        double x, double y, double w, double h,
        in RectD oldArea, in RectD newArea, in RectD clampArea,
        double minW, double minH)
    {
        double sx = oldArea.Width > 0 ? newArea.Width / oldArea.Width : 1.0;
        double sy = oldArea.Height > 0 ? newArea.Height / oldArea.Height : 1.0;
        double width = ClampRescaledExtent(w * sx, clampArea.Width, minW);
        double height = ClampRescaledExtent(h * sy, clampArea.Height, minH);
        double left = ClampRescaledOrigin(newArea.X + (x - oldArea.X) * sx, clampArea.X, clampArea.Right, width);
        double top = ClampRescaledOrigin(newArea.Y + (y - oldArea.Y) * sy, clampArea.Y, clampArea.Bottom, height);
        return (left, top, width, height);
    }

    /// <summary>
    /// Display-change entry points (<see cref="SystemEvents.DisplaySettingsChanged"/> for resolution /
    /// primary-monitor switches, per-window <c>DpiChanged</c> for DPI-only changes which never raise
    /// the former) are debounced into a single <see cref="RescaleToCurrent"/>: the OS fires bursts
    /// with transient intermediate values, and <see cref="SystemParameters.WorkArea"/> only refreshes
    /// after WPF processes the broadcast — rescaling immediately would compound stale factors.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _rescaleDebounce;

    private void ScheduleRescale()
    {
        if (IsDisabled) return;
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.InvokeAsync(ScheduleRescale);
            return;
        }

        _rescaleDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _rescaleDebounce.Tick -= RescaleDebounceTick;
        _rescaleDebounce.Tick += RescaleDebounceTick;
        _rescaleDebounce.Stop();
        _rescaleDebounce.Start();
    }

    private void RescaleDebounceTick(object? sender, EventArgs e)
    {
        if (_rescaleDebounce != null)
        {
            _rescaleDebounce.Stop();
            _rescaleDebounce.Tick -= RescaleDebounceTick;
        }
        RescaleToCurrent();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleRescale();

    private void OnWindowDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e)
    {
        /*Serilog.Log.Debug(
            "Window DPI changed: {OldDpi} -> {NewDpi} on {Window}",
            e.OldDpi, e.NewDpi, sender?.GetType().Name ?? "<unknown>");
        ScheduleRescale();*/
    }

    private void RescaleToCurrent()
    {
        if (IsDisabled) return;
        // Never rescale mid-gesture: a native move/size loop or an active title drag owns the
        // geometry until it exits.
        if (Helpers.WindowDragController.IsNativeSizing ||
            Helpers.WindowDragController.IsTitleDragging)
        {
            Serilog.Log.Debug("Rescale deferred: a move/resize gesture is active.");
            ScheduleRescale();
            return;
        }

        var current = GetPrimaryWorkAreaDip();
        if (current.Width <= 0 || current.Height <= 0)
        {
            return;
        }

        if (_appliedResolution.Width <= 0 || _appliedResolution.Height <= 0)
        {
            _appliedResolution = current;
            return;
        }

        if (SameArea(current, _appliedResolution))
        {
            // Same size — re-baseline anyway so rounding drift can never accumulate.
            Serilog.Log.Debug("Rescale skipped: area unchanged ({Area}).", current);
            _appliedResolution = current;
            _lastPassSummary = $"no-op, area unchanged @ {DateTime.Now:HH:mm:ss}";
            // The surface has no DpiChanged subscription of its own: a pass that skips
            // container scaling must still re-anchor/re-size it, otherwise it keeps the
            // pre-change rect (visible in the debug overlay as a stale surface).
            try { _surface?.Relayout(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Surface relayout failed."); }
            return;
        }

        // Stability re-check: the desktop may still be settling (bursts deliver transient
        // intermediate areas). If values moved under us, drop this pass WITHOUT touching the
        // baseline and let the next scheduled pass apply clean numbers — otherwise partial
        // factors compound with clamping into a wrong layout.
        var settled = GetPrimaryWorkAreaDip();
        if (!SameArea(settled, current))
        {
            Serilog.Log.Debug("Rescale deferred: area still settling ({First} vs {Second}).", current, settled);
            ScheduleRescale();
            return;
        }
        current = settled;

        double sx = current.Width / _appliedResolution.Width;
        double sy = current.Height / _appliedResolution.Height;
        Serilog.Log.Information(
            "Rescale layout: {Old} -> {New} (sx={Sx:0.###}, sy={Sy:0.###}, {Count} containers)",
            _appliedResolution, current, sx, sy, _mainVm.Containers.Count);
        _lastPassSummary = $"scaled {_appliedResolution.Width:0}x{_appliedResolution.Height:0}"
            + $" -> {current.Width:0}x{current.Height:0} (sx={sx:0.###}, sy={sy:0.###}) @ {DateTime.Now:HH:mm:ss}";

        using (BeginLayoutUpdate())
        {
            foreach (var vm in _mainVm.Containers)
            {
                // Minimums must match each window type (BoxContainerWindow 160x120,
                // WebWidgetWindow 120x80) — otherwise a shrink would inflate small widgets.
                double minW = vm.Type == DesktopItemContainerType.WebWidget ? 120 : MinContainerWidth;
                double minH = vm.Type == DesktopItemContainerType.WebWidget ? 80 : MinContainerHeight;
                var (left, top, width, height) = ScaleBounds(
                    vm.Left, vm.Top, vm.Width, vm.Height,
                    _appliedResolution, current, current, minW, minH);
                Serilog.Log.Debug(
                    "Rescale container: ({OldL:0},{OldT:0} {OldW:0}x{OldH:0}) -> ({L:0},{T:0} {W:0}x{H:0})",
                    vm.Left, vm.Top, vm.Width, vm.Height, left, top, width, height);
                vm.Width = width;
                vm.Height = height;
                vm.Left = left;
                vm.Top = top;
            }

            _appliedResolution = current;

            foreach (var window in _windows.Values)
            {
                if (window is WidgetWindow widgetWindow)
                {
                    // One failing window must never abort the pass: the baseline is already
                    // updated, so anything skipped here would be left stale with no retry
                    // (a later pass sees SameArea and no-ops). Isolate, log, continue — the
                    // verify pass scheduled below heals whatever is still out of sync.
                    try
                    {
                        widgetWindow.ApplyGeometry();
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "Rescale: ApplyGeometry failed for a container window; continuing pass.");
                    }
                }
            }
        }

        RefreshDesktopLayer();

        // Windows may now sit on different monitors: re-evaluate pause state so moved
        // widgets pick up the covering state of their new monitor.
        _widgetPause?.Invalidate();

        _ = SaveAsync();
        try { LayoutRefreshed?.Invoke(); } catch { }
        _verifyAttempts = 0;
        _verifySweepsLeft = 2;
        ScheduleVerifyLayout(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Re-anchors/re-sizes the desktop surface and re-asserts the desktop z-band and icon
    /// watch. Isolated per step so one failing piece never blocks the others (or the save
    /// and verification scheduled after a rescale pass).
    /// </summary>
    private void RefreshDesktopLayer()
    {
        try { _surface?.Relayout(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Surface relayout failed."); }
        try { SyncSurfaceWithIconVisibility(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Surface visibility sync failed."); }
        try { EnsureDesktopZOrder(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Desktop z-order sync failed."); }
        try { StartIconVisibilityWatch(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Icon visibility watch restart failed."); }
    }

    /// <summary>Raised after a display/DPI rescale pass that changed the layout completes.
    /// Lets app-level UI (tray popup) re-anchor itself.</summary>
    public event Action? LayoutRefreshed;

    private const double VerifyToleranceDip = 1.0;
    private System.Windows.Threading.DispatcherTimer? _verifyTimer;
    private int _verifyAttempts;
    private int _verifySweepsLeft;

    private void ScheduleVerifyLayout(TimeSpan delay)
    {
        if (IsDisabled) return;
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.InvokeAsync(() => ScheduleVerifyLayout(delay));
            return;
        }

        _verifyTimer ??= new System.Windows.Threading.DispatcherTimer();
        _verifyTimer.Tick -= VerifyTimerTick;
        _verifyTimer.Tick += VerifyTimerTick;
        _verifyTimer.Interval = delay;
        _verifyTimer.Stop();
        _verifyTimer.Start();
    }

    private void VerifyTimerTick(object? sender, EventArgs e)
    {
        if (_verifyTimer != null)
        {
            _verifyTimer.Stop();
            _verifyTimer.Tick -= VerifyTimerTick;
        }
        VerifyLayout();
    }

    /// <summary>
    /// Settles a rescale pass: WPF, hooks and overlay sync can leave a live window out of sync
    /// with its (already correct) model after a DPI transition. This compares every live window
    /// against the model-derived expected rect and re-applies mismatches once. Re-applying writes
    /// model values, never scaled values, so it cannot compound — it only enforces convergence.
    /// </summary>
    private void VerifyLayout()
    {
        if (IsDisabled) return;
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.InvokeAsync(VerifyLayout);
            return;
        }

        if (Helpers.WindowDragController.IsNativeSizing ||
            Helpers.WindowDragController.IsTitleDragging ||
            IsLayoutUpdateActive)
        {
            if (_verifyAttempts < 3)
            {
                _verifyAttempts++;
                ScheduleVerifyLayout(TimeSpan.FromSeconds(1));
            }
            else
            {
                Serilog.Log.Warning("Layout verification skipped: a move/resize gesture is still active.");
            }
            return;
        }

        var current = GetPrimaryWorkAreaDip();
        if (current.Width <= 0 || current.Height <= 0)
        {
            return;
        }

        // A newer display change may be settling (or its rescale pass debouncing): never enforce
        // against a stale baseline — that could clamp a pre-change layout into the new area and
        // corrupt the next pass. Defer until the area matches the applied baseline.
        if (!SameArea(current, _appliedResolution))
        {
            if (_verifyAttempts < 5)
            {
                _verifyAttempts++;
                ScheduleVerifyLayout(TimeSpan.FromSeconds(1));
            }
            else
            {
                Serilog.Log.Warning("Layout verification skipped: display area is still settling.");
            }
            return;
        }

        bool repaired = false;
        using (BeginLayoutUpdate())
        {
            foreach (var vm in _mainVm.Containers)
            {
                double minW = vm.Type == DesktopItemContainerType.WebWidget ? 120 : MinContainerWidth;
                double minH = vm.Type == DesktopItemContainerType.WebWidget ? 80 : MinContainerHeight;
                var clamped = ClampBoundsToArea(vm.Bounds, current, minW, minH);
                if (Math.Abs(clamped.X - vm.Bounds.X) > VerifyToleranceDip ||
                    Math.Abs(clamped.Y - vm.Bounds.Y) > VerifyToleranceDip ||
                    Math.Abs(clamped.Width - vm.Bounds.Width) > VerifyToleranceDip ||
                    Math.Abs(clamped.Height - vm.Bounds.Height) > VerifyToleranceDip)
                {
                    Serilog.Log.Warning(
                        "Layout verify: container {Id} model {Bounds} is outside {Area}; clamping.",
                        vm.Id, vm.Bounds, current);
                    vm.Left = clamped.X;
                    vm.Top = clamped.Y;
                    vm.Width = clamped.Width;
                    vm.Height = clamped.Height;
                    repaired = true;
                }

                if (_windows.TryGetValue(vm.Id, out var window) && window is WidgetWindow widgetWindow)
                {
                    var expected = widgetWindow.GetExpectedDisplayRect();
                    var live = GetLiveWindowRect(window);
                    bool dipMismatch =
                        Math.Abs(expected.X - live.X) > VerifyToleranceDip ||
                        Math.Abs(expected.Y - live.Y) > VerifyToleranceDip ||
                        Math.Abs(expected.Width - live.Width) > VerifyToleranceDip ||
                        Math.Abs(expected.Height - live.Height) > VerifyToleranceDip;
                    // Physical-space comparison with the MONITOR scale: a window stuck at a stale
                    // DPI looks consistent in its own DIP space while rendering off-bounds.
                    double monScale = GetPrimaryDpi() / 96.0;
                    if (monScale <= 0) monScale = 1.0;
                    var expectedPhys = RectD.FromXYWH(
                        expected.X * monScale, expected.Y * monScale,
                        expected.Width * monScale, expected.Height * monScale);
                    var livePhys = GetLiveWindowRectPhysical(window);
                    const double physicalTolerancePx = 2.0;
                    bool physMismatch =
                        Math.Abs(expectedPhys.X - livePhys.X) > physicalTolerancePx ||
                        Math.Abs(expectedPhys.Y - livePhys.Y) > physicalTolerancePx ||
                        Math.Abs(expectedPhys.Width - livePhys.Width) > physicalTolerancePx ||
                        Math.Abs(expectedPhys.Height - livePhys.Height) > physicalTolerancePx;
                    if (dipMismatch || physMismatch)
                    {
                        Serilog.Log.Warning(
                            "Layout verify: container {Id} ({Window}) live {Live} != expected {Expected}; {Details} reapplying.",
                            vm.Id, widgetWindow.GetType().Name, live, expected, DescribeLiveState(widgetWindow));
                        try
                        {
                            widgetWindow.ApplyGeometry();
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Error(ex, "Layout verify: reapply failed for container {Id}.", vm.Id);
                        }
                        if (physMismatch)
                        {
                            // Native placement is immune to a stale window DPI context: physical
                            // pixels are physical pixels. Property sets alone cannot fix a stuck
                            // window (they get reinterpreted in the stale space).
                            try
                            {
                                var hwnd = new WindowInteropHelper(window).Handle;
                                if (hwnd != IntPtr.Zero) _positioning.SetBounds(hwnd, expectedPhys);
                            }
                            catch (Exception ex)
                            {
                                Serilog.Log.Error(ex, "Layout verify: native repair failed for container {Id}.", vm.Id);
                            }
                        }
                        repaired = true;
                    }
                }
            }
        }

        if (repaired)
        {
            _ = SaveAsync();
            try { LayoutRefreshed?.Invoke(); } catch { }
        }

        // Late DPI remaps can re-smear a window after an early sweep heals it: keep sweeping
        // a bounded number of times so a repeat offender is caught, not just the first drift.
        if (_verifySweepsLeft > 0)
        {
            _verifySweepsLeft--;
            ScheduleVerifyLayout(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Extra state for mismatch diagnostics: chrome-overlay geometry for widgets and the live
    /// per-window DPI scale, so a repeat smear can be attributed instead of guessed at.
    /// </summary>
    private static string DescribeLiveState(WidgetWindow window)
    {
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            string dpiText = $"dpi={dpi.DpiScaleX:0.###}x{dpi.DpiScaleY:0.###}";
            string nativeText;
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                nativeText = hwnd != IntPtr.Zero && Win32Apis.GetWindowRect(hwnd, out RECT nr)
                    ? $" native=({nr.Left},{nr.Top} {nr.Right - nr.Left}x{nr.Bottom - nr.Top}px)"
                    : " native=<none>";
            }
            catch
            {
                nativeText = " native=<unknown>";
            }
            if (window is Views.Containers.WebWidgetWindow widget)
            {
                var overlay = widget.ChromeOverlay;
                if (overlay is null)
                {
                    return dpiText + nativeText + " overlay=<none>";
                }

                return dpiText + nativeText + $" overlay=({overlay.Left:0},{overlay.Top:0} {overlay.Width:0}x{overlay.Height:0})"
                    + (overlay.IsVisible ? " visible" : " hidden");
            }
            return dpiText + nativeText;
        }
        catch
        {
            return "dpi=<unknown>";
        }
    }

    /// <summary>
    /// Live window rect in DIPs, read from the native window rect (device pixels) converted with
    /// the window's own DPI scale — the same ground truth the debug overlay and Performance
    /// Monitor show. WPF's cached Left/Top/Width/Height can disagree with it after a DPI
    /// transition, and verification must compare against reality, not the cache.
    /// </summary>
    private static RectD GetLiveWindowRect(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.GetWindowRect(hwnd, out RECT r))
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                return RectD.FromXYWH(r.Left / sx, r.Top / sy, (r.Right - r.Left) / sx, (r.Bottom - r.Top) / sy);
            }
        }
        catch { }
        return RectD.FromXYWH(window.Left, window.Top, window.Width, window.Height);
    }

    /// <summary>
    /// Live native (physical-pixel) window rect. Falls back to props × monitor scale.
    /// Compared in physical space so a window stuck at a stale DPI (whose DIP props look
    /// self-consistent while it renders oversized/off-bounds) is still detected.
    /// </summary>
    private static RectD GetLiveWindowRectPhysical(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.GetWindowRect(hwnd, out RECT r))
            {
                return RectD.FromXYWH(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        catch { }
        double s = GetPrimaryDpi() / 96.0;
        if (s <= 0) s = 1.0;
        return RectD.FromXYWH(window.Left * s, window.Top * s, window.Width * s, window.Height * s);
    }

    /// <summary>Clamps bounds into an area without rescaling (used by post-pass verification).</summary>
    private static RectD ClampBoundsToArea(RectD bounds, RectD area, double minW, double minH)
    {
        double width = ClampRescaledExtent(bounds.Width, area.Width, minW);
        double height = ClampRescaledExtent(bounds.Height, area.Height, minH);
        double left = ClampRescaledOrigin(bounds.X, area.X, area.Right, width);
        double top = ClampRescaledOrigin(bounds.Y, area.Y, area.Bottom, height);
        return RectD.FromXYWH(left, top, width, height);
    }

    #endregion

    #region Items management


    /// <summary>
    /// Enumerates the live desktop on a dedicated STA thread. The Shell COM walk
    /// (<c>IShellFolder.EnumObjects</c>, <c>IShellItem</c> display names) must run on an STA thread and is
    /// CPU/IO heavy, so running it off the WPF UI thread keeps startup responsive. Only plain
    /// <see cref="BoxItem"/> models cross the thread boundary — no COM objects are marshalled.
    /// </summary>
    private Task<List<BoxItem>> EnumerateDesktopAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<List<BoxItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var items = new List<BoxItem>();
                var enumerator = _desktop.GetDesktopItemsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        items.Add(enumerator.Current);
                    }
                }
                finally
                {
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                tcs.TrySetResult(items);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Resolves the id of the default box (the one the default rule feeds). Falls back to the first
    /// box found if no box is flagged.
    /// </summary>
    private System.Guid? DefaultBoxId()
    {
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                if (box.IsDefault)
                {
                    return box.Id;
                }
            }
        }

        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is { Boxes.Count: > 0 })
            {
                return container.ChildContainer.Boxes[0].Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes sure a default rule exists and that the box it targets is flagged as the default box
    /// (so it cannot be deleted).
    /// </summary>
    private void EnsureDefaultRuleAndBox()
    {
        var id = DefaultBoxId();
        _rules.EnsureDefaultRule(id);

        if (id is { } boxId)
        {
            foreach (var container in _containers.GetContainers())
            {
                if (container.ChildContainer is null)
                {
                    continue;
                }

                foreach (var box in container.ChildContainer.Boxes)
                {
                    if (box.Id == boxId)
                    {
                        box.IsDefault = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reconciles the persisted <see cref="BoxItem"/>s with the live desktop: items that no longer exist
    /// on the desktop are removed from every <see cref="BoxType.DesktopItems"/> box, and desktop items that
    /// aren't tracked in any box yet get a <see cref="BoxItem"/> created and routed (by rule, falling back to
    /// the default box). Keeps the snapshot truthful after files were created/deleted outside the app.
    /// </summary>
    private async Task ReconcileItemsAsync()
    {
        List<BoxItem> current;
        try
        {
            current = await EnumerateDesktopAsync();
        }
        catch
        {
            return;
        }

        // Drop entries whose underlying desktop item has disappeared. Path-backed items (files, folders,
        // shortcuts, virtual desktop items that resolve to a path) are matched by path or PIDL. Items that
        // carry only a PIDL (e.g. UWP apps dragged from the Start Menu, which are not desktop items) cannot
        // be verified against the desktop enumeration, so they are preserved.
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                if (box.BoxType != BoxType.DesktopItems)
                {
                    continue;
                }

                foreach (var existing in box.Items.ToList())
                {
                    if (string.IsNullOrEmpty(existing.Path))
                    {
                        continue;
                    }

                    if (!current.Any(c => BoxItem.RefersToSame(c, existing)))
                    {
                        box.Items.Remove(existing);
                    }
                }
            }
        }

        // Create entries for desktop items that aren't represented in any box yet.
        var defaultId = DefaultBoxId();
        foreach (var item in current)
        {
            if (IsAlreadyTracked(item))
            {
                continue;
            }

            var targetId = !string.IsNullOrEmpty(item.Path)
                ? _rules.MatchTargetBoxId(Path.GetFileName(item.Path))
                : null;
            targetId ??= defaultId;
            if (targetId is null)
            {
                continue;
            }

            var box = _boxRegistry.GetBox(targetId.Value);
            if (box is null || box.BoxType != BoxType.DesktopItems)
            {
                continue;
            }

            box.Items.Add(item);
        }

        await SaveAsync();
    }

    private bool IsAlreadyTracked(BoxItem item)
    {
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                if (box.Items.Any(i => BoxItem.RefersToSame(i, item)))
                {
                    return true;
                }
            }
        }

        return false;
    }


    private async Task BuildDefaultContainerAsync()
    {
        var items = new List<BoxItem>();
        try
        {
            for (int attempt = 0; attempt < 5 && items.Count == 0; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(400);
                }

                items = await EnumerateDesktopAsync();
            }
        }
        catch
        {
            // Shell enumeration can fail on exotic sessions; fall back to an empty default container.
        }

        var box = new Box
        {
            Name = "Desktop",
            BoxType = BoxType.DesktopItems,
            IsDefault = true,
        };
        foreach (var item in items)
        {
            box.Items.Add(item);
        }

        var boxContainer = new BoxContainer
        {
            Boxes = { box },
            SelectedIndex = 0,
        };

        _containers.CreateContainer(DesktopItemContainerType.BoxContainer, 60, 60, 340, 460, childContainer: boxContainer);

        // FolderPortal for Downloads at top-right
        string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (string.IsNullOrWhiteSpace(downloadsPath) || !Directory.Exists(downloadsPath))
        {
            try
            {
                var up = Environment.GetEnvironmentVariable("USERPROFILE");
                if (!string.IsNullOrWhiteSpace(up))
                {
                    var alt = Path.Combine(up, "Downloads");
                    if (Directory.Exists(alt)) downloadsPath = alt;
                }
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(downloadsPath))
        {
            string dlName = Path.GetFileName(downloadsPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dlName)) dlName = "Downloads";
            var dlBox = new Box
            {
                Name = dlName,
                BoxType = BoxType.FolderPortal,
                FolderPath = downloadsPath,
                FolderPortalViewMode = FolderPortalViewMode.Details,
                FolderSortBy = FolderSortMode.DateModified,
                FolderSortAscending = false,
            };
            var dlContainer = new BoxContainer
            {
                Boxes = { dlBox },
                SelectedIndex = 0,
            };
            var wa = GetPrimaryWorkAreaDip();
            double dlW = 380;
            double dlH = 420;
            double dlLeft = wa.Right - dlW - 20;
            double dlTop = wa.Y + 20;
            // Clamp in case work area is smaller than assumed
            dlLeft = Math.Max(wa.X, Math.Min(dlLeft, wa.Right - dlW));
            dlTop = Math.Max(wa.Y, Math.Min(dlTop, wa.Bottom - dlH));
            _containers.CreateContainer(DesktopItemContainerType.BoxContainer, dlLeft, dlTop, dlW, dlH, childContainer: dlContainer);

            // Native analog clock widget directly under the Downloads box
            const string clockSlug = "NativeClock";
            double clockW = 220;
            double clockH = 220;
            try
            {
                var svc = App.Services?.GetService<INativeWidgetService>();
                var info = svc?.TryGetWidget(clockSlug, NativeWidgetSource.App);
                if (info is not null)
                {
                    if (info.Manifest.Width > 0) clockW = info.Manifest.Width;
                    if (info.Manifest.Height > 0) clockH = info.Manifest.Height;
                }
                else
                {
                    // Fallback: read manifest directly if service not yet available
                    var appBase = AppContext.BaseDirectory;
                    var clockManifestPath = Path.Combine(appBase, "NativeWidgets", clockSlug, "nwidget.json");
                    if (!File.Exists(clockManifestPath))
                    {
                        var devPath = Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "src", "DesktopBoxesUI", "NativeWidgets", clockSlug, "nwidget.json"));
                        if (File.Exists(devPath)) clockManifestPath = devPath;
                    }
                    if (File.Exists(clockManifestPath))
                    {
                        var json = File.ReadAllText(clockManifestPath);
                        var manifest = DesktopBoxes.WidgetSdk.NativeWidgetManifest.TryParse(json, clockSlug, out _);
                        if (manifest is not null)
                        {
                            if (manifest.Width > 0) clockW = manifest.Width;
                            if (manifest.Height > 0) clockH = manifest.Height;
                        }
                    }
                }
            }
            catch { }

            double clockLeft = dlLeft + (dlW - clockW) / 2; // centered under Downloads
            double clockTop = dlTop + dlH + 20;
            // Clamp inside work area
            clockLeft = Math.Max(wa.X, Math.Min(clockLeft, wa.Right - clockW));
            clockTop = Math.Max(wa.Y, Math.Min(clockTop, wa.Bottom - clockH));
            // Only place if there is vertical space below Downloads (avoid overlapping bottom edge)
            if (clockTop + clockH <= wa.Bottom + 1)
            {
                _containers.CreateNativeWidgetContainer(clockLeft, clockTop, clockW, clockH, clockSlug);
            }
        }
    }

    #endregion

    #region Window Management

    private void Containers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            CloseAll();
            return;
        }

        if (e.NewItems != null)
        {
            foreach (ContainerViewModel vm in e.NewItems)
            {
                AddWindow(vm, showActivated: true);
            }
        }

        if (e.OldItems != null)
        {
            foreach (ContainerViewModel vm in e.OldItems)
            {
                RemoveWindow(vm.Id);
            }
        }
    }

    public bool IsDesktopWindow(Window wind, bool checkSurfaceToo = true)
    {
        if (checkSurfaceToo && wind is DesktopSurface) return true;
        // The chrome overlay counts too: when the OS keeps an owner below its owned overlay,
        // the insert-after is the overlay — misclassifying it as foreign makes the z-pin rip
        // the owner above its own chrome on every activation/show.
        return wind is BoxContainerWindow || wind is WebWidgetWindow || wind is NativeWidgetWindow || wind is WidgetChromeOverlay;
    }

    public bool IsDesktopWindow(IntPtr hWnd, bool checkSurfaceToo = true)
    {
        if (checkSurfaceToo && hWnd == Win32Apis.DesktopSurfaceHandle) return true;
        // Check if hwnd belongs to our app's containers or their chrome overlays.
        try
        {
            foreach (var w in _windows.Values)
            //foreach (Window w in App.Current.Windows)
            {
                if (IsDesktopWindow(w, checkSurfaceToo: false))
                {
                    var wh = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    if (wh == hWnd) return true;
                }
            }

            // Overlays are not in _windows (they belong to their owner, not a container).
            foreach (Window w in App.Current.Windows)
            {
                if (w is WidgetChromeOverlay)
                {
                    var wh = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    if (wh != IntPtr.Zero && wh == hWnd) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private void AddWindow(ContainerViewModel vm, bool showActivated = true)//, bool focusWorkaround = false
    {
        if (_windows.ContainsKey(vm.Id))
        {
            if (!vm.IsVisible)
            {//if is not set to be visible hide it
                this.HideContainer(vm.Id);
            }
            return;
        }
        //if is not set to be visible do not add it
        if (!vm.IsVisible) return;
        Window window;
        if (vm.Type == DesktopItemContainerType.WebWidget)
        {
            window = new WebWidgetWindow(vm)
            {
                ShowActivated = false
            };
        }
        else if (vm.Type == DesktopItemContainerType.NativeWidget)
        {
            window = new NativeWidgetWindow(vm)
            {
                ShowActivated = false
            };
        }
        else
        {
            window = new BoxContainerWindow(vm, _mainVm, _positioning, SaveAsyncFireAndForget)
            {
                ShowActivated = false
            };
        }

        _windows[vm.Id] = window;
        // Per-window DPI changes never raise DisplaySettingsChanged — observe them directly so
        // DPI-only switches (same resolution, different scale) also funnel into the rescale path.
        //window.DpiChanged += OnWindowDpiChanged;//not needed
        // RegisterBoxWindow is done inside each window's OnLoaded for WebWidget; keep for BoxContainer compat
        try { Win32Apis.RegisterBoxWindow(new WindowInteropHelper(window).Handle); } catch { }
        //IntPtr? foregroundWindowHwnd = null;
        //if (!showActivated && focusWorkaround)
        //{
        //    foregroundWindowHwnd = User32.GetForegroundWindow();
        //}
        window.Show();
        if (showActivated) window.Activate();
        // A window created while its monitor is already covered must start suspended.
        ApplySupervisorStateToWindow(window);

        /*if (!showActivated && focusWorkaround && foregroundWindowHwnd is not null)
        {
            window.ContentRendered += (_, _) =>
            {
                //Win32Apis.NudgeCursor();
                NudgeWindowActivation(window, foregroundWindowHwnd.Value);
            };
            //window.Focus();
        }*/
    }

    /*public void NudgeWindowActivation(Window window, IntPtr prevForegroundWindow)
    {
        var widgetHwnd = new WindowInteropHelper(window).Handle;
        //var previousHwnd = User32.GetForegroundWindow();

        //Debug.WriteLine($"Previous: 0x{previousHwnd.ToInt64():X}");

        User32.SetForegroundWindow(widgetHwnd);
        //window.Activate();

        //Debug.WriteLine(
        //    $"Widget active: {User32.GetForegroundWindow() == widgetHwnd}");

        if (prevForegroundWindow != IntPtr.Zero &&
            prevForegroundWindow != widgetHwnd)
        {
            //Win32.NativeMethods.Win32Apis.GlueToDesktop(widgetHwnd, Win32Apis.DesktopSurfaceHandle);
            User32.SetActiveWindow(prevForegroundWindow);
            User32.SetForegroundWindow(prevForegroundWindow);
        }
    }*/

    /// <summary>True when every open <see cref="BoxContainerWindow"/> is currently hidden via
    /// <see cref="HideAllBoxes"/>. Session-only (not persisted across restarts).</summary>
    public bool AllBoxesHidden => _allBoxesHidden;

    /// <summary>Raised whenever the all-boxes-hidden state changes (argument is the new state), so the
    /// tray menu check box stays in sync no matter which trigger performed the toggle.</summary>
    public event EventHandler<bool>? AllBoxesHiddenChanged;

    /// <summary>Toggles between hiding and showing all open box windows.</summary>
    public void ToggleHideAllBoxes()
    {
        if (_allBoxesHidden)
        {
            ShowAllBoxes();
        }
        else
        {
            HideAllBoxes();
        }
    }

    /// <summary>
    /// Temporarily hides every open box window. The <see cref="Win32Apis.MinimizePreventionHook"/> would
    /// otherwise re-show them, so each hide is wrapped in <see cref="Win32Apis.AllowHide"/>. The tray host
    /// is deliberately left visible so the toggle can be reversed; the desktop surface keeps whatever
    /// visibility <see cref="SyncSurfaceWithIconVisibility"/> assigned it (hidden while icons are shown).
    /// </summary>
    public void HideAllBoxes()
    {
        foreach (var window in _windows.Values)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Win32Apis.AllowHide(hwnd);
            window.Hide();
            Win32Apis.DisallowHide(hwnd);
        }

        _allBoxesHidden = true;
        AllBoxesHiddenChanged?.Invoke(this, true);
    }

    /// <summary>Re-shows every box window previously hidden by <see cref="HideAllBoxes"/>.</summary>
    public void ShowAllBoxes()
    {
        foreach (var window in _windows.Values)
        {
            window.Show();
        }

        _allBoxesHidden = false;
        AllBoxesHiddenChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Shows every container window, yielding to the dispatcher between each so the UI thread stays
    /// responsive while a potentially large number of <see cref="BoxContainerWindow"/>s is constructed.
    /// </summary>
    private async Task AddWindowsAsync()
    {
        foreach (var vm in _mainVm.Containers)
        {
            AddWindow(vm, showActivated: false);
            await Task.Yield();
        }
    }

    /// <summary>
    /// Lazily creates the remaining container windows in the background (the caller shows the first one
    /// synchronously). Creation is spread across dispatcher turns so the UI stays responsive and the splash
    /// can close — the containers stream in after startup instead of blocking it.
    /// </summary>
    private async Task StreamRemainingContainersAsync(Action? onFinishAction = null)
    {
        try
        {
            // Let the first frame render (and the splash close) before continuing.
            await Task.Yield();

            var rest = _mainVm.Containers.Skip(1).ToList();
            foreach (var vm in rest)
            {
                AddWindow(vm, showActivated: false);
                await Task.Yield();
            }

            // Settle the desktop z-band once streaming ends: boxes above the surface, so
            // hover/clicks reach boxes from the first frame without needing a click first.
            EnsureDesktopZOrder();
            //await Task.Delay(TimeSpan.FromSeconds(1));
            //_surface!.Focus();
            //_surface!.Activate();
        }
        catch
        {
            // Best-effort background streaming; a window failure must never break startup.
        }
        finally
        {
            onFinishAction?.Invoke();
        }
    }

    private void RemoveWindow(System.Guid id)
    {
        if (_windows.TryGetValue(id, out var window))
        {
            //try { window.DpiChanged -= OnWindowDpiChanged; } catch { }
            var handle = new WindowInteropHelper(window).Handle;
            Win32Apis.UnregisterBoxWindow(handle);
            // WebView2 must be torn down BEFORE Window.Close: closing with a live WebView2
            // inside throws InvalidOperationException ("Notification Window is null") from
            // HwndHost teardown mid-close (kills ToggleDisable/Reset teardown).
            try
            {
                if (window is WebWidgetWindow widgetWindow) widgetWindow.PrepareForClose();
                else if (window is NativeWidgetWindow nativeWindow) nativeWindow.PrepareForClose();
            }
            catch { }
            //Win32Apis.AllowHide(handle);
            //window.Close();
            window.CloseWindowEx(handle);
            _windows.Remove(id);
        }
    }
    #endregion


    #region Widget auto-pause

    /// <summary>Starts per-monitor fullscreen/system auto-suspend for widget WebViews on the
    /// shared <see cref="PlaybackSupervisor"/> engine (same as live wallpapers). No class
    /// exclusions: every own window is in-process (already excluded by PID) and shell
    /// windows are excluded by default.</summary>
    private void StartWidgetAutoPause()
    {
        try { _widgetPause?.Dispose(); } catch { }
        _widgetPause = new PlaybackSupervisor(
            () => new PausePolicy
            {
                OnFullscreen = _settingsService.UserSettings.PauseWidgetsOnFullscreen,
                OnBatterySaver = _settingsService.UserSettings.PauseWidgetsOnBatterySaver,
                OnRemoteSession = _settingsService.UserSettings.PauseWidgetsOnRemoteSession,
            },
            GetPauseMonitors,
            extraExcludedWindowClasses: null);
        _widgetPause.PauseStateChanged += OnWidgetPauseChanged;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var supervisor = _widgetPause;
        if (supervisor is null) return;
        if (e.Reason == SessionSwitchReason.SessionLock) supervisor.SessionLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) supervisor.SessionLocked = false;
    }

    /// <summary>Fires on the supervisor's hook thread; marshal to the UI thread.</summary>
    private void OnWidgetPauseChanged(string device, PauseReason reason)
    {
        if (IsDisabled) return;
        _ = _dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (IsDisabled) return;
                ApplyWidgetPause(device, reason);
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "Widget auto-pause apply failed"); }
        });
    }

    /// <summary>Suspends (or resumes) every widget window currently on the given monitor.
    /// Runs on the UI thread. Resume is gated on visibility so a hidden/minimized widget
    /// keeps its own visibility-driven suspension.</summary>
    private void ApplyWidgetPause(string device, PauseReason reason)
    {
        List<Window> snapshot;
        try { snapshot = _windows.Values.ToList(); }
        catch { return; }
        List<PauseMonitor>? monitors = null;
        foreach (var window in snapshot)
        {
            try
            {
                monitors ??= GetPauseMonitors();
                if (!TryGetMonitorDevice(window, monitors, out var actual)) continue;
                if (!string.Equals(actual, device, StringComparison.OrdinalIgnoreCase)) continue;
                bool resume = reason == PauseReason.None;
                //Just hide it for now
                if (resume)
                {
                    window.Show();
                }
                else
                {
                    window.Hide();
                }


                /*if (window is Views.Containers.WebWidgetWindow cssWindow)
                {
                    var control = cssWindow.WidgetControl;
                    if (control is null) continue;
                    if (resume)
                    {
                        if (window.IsVisible && window.WindowState != WindowState.Minimized)
                            control.Resume();
                    }
                    else control.Suspend();
                }
                else if (window is Views.Containers.NativeWidgetWindow nativeWindow)
                {
                    var plugin = nativeWindow.Widget;
                    if (plugin is null) continue;
                    try
                    {
                        if (resume)
                        {
                            if (window.IsVisible && window.WindowState != WindowState.Minimized)
                                plugin.Resume();
                        }
                        else plugin.Suspend();
                    }
                    catch { }
                }*/
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "Widget auto-pause failed for a window"); }
        }
    }

    /// <summary>Applies the current pause state to a freshly shown window (it may have been
    /// created while its monitor was already covered — no transition will fire for it).</summary>
    private void ApplySupervisorStateToWindow(Window window)
    {
        var supervisor = _widgetPause;
        if (supervisor is null) return;
        try
        {
            if (!TryGetMonitorDevice(window, GetPauseMonitors(), out var device)) return;
            bool resume = supervisor.GetPauseReason(device) == PauseReason.None;
            //Just hide it for now
            if (resume)
            {
                window.Show();
            }
            else
            {
                window.Hide();
            }
            /*if (window is Views.Containers.WebWidgetWindow cssWindow)
            {
                var control = cssWindow.WidgetControl;
                if (control is null) return;
                if (resume)
                {
                    if (window.IsVisible && window.WindowState != WindowState.Minimized)
                        control.Resume();
                }
                else control.Suspend();
            }
            else if (window is Views.Containers.NativeWidgetWindow nativeWindow)
            {
                var plugin = nativeWindow.Widget;
                if (plugin is null) return;
                try
                {
                    if (resume)
                    {
                        if (window.IsVisible && window.WindowState != WindowState.Minimized)
                            plugin.Resume();
                    }
                    else plugin.Suspend();
                }
                catch { }
            }*/
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Widget initial pause state failed"); }
    }

    private List<PauseMonitor> GetPauseMonitors()
    {
        try
        {
            return _monitors.GetAllMonitors()
                .Select(m => new PauseMonitor(m.DeviceName, ToNativeRect(m.Bounds), ToNativeRect(m.WorkArea)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static WindowsNative.RECT ToNativeRect(Core.Models.RectD r) =>
        new((int)r.X, (int)r.Y, (int)(r.X + r.Width), (int)(r.Y + r.Height));

    /// <summary>Which pause-monitor device a window is currently on, by its physical center.</summary>
    private static bool TryGetMonitorDevice(Window window, IReadOnlyList<PauseMonitor> monitors, out string device)
    {
        device = "";
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            if (!User32.GetWindowRect(hwnd, out var rect)) return false;
            double cx = (rect.Left + rect.Right) / 2.0;
            double cy = (rect.Top + rect.Bottom) / 2.0;
            foreach (var m in monitors)
            {
                if (cx >= m.Bounds.Left && cx < m.Bounds.Right && cy >= m.Bounds.Top && cy < m.Bounds.Bottom)
                {
                    device = m.Device;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    #endregion

    #region Boxes

    public async Task ResetAsync()
    {
        CloseAll();
        //
        foreach (var container in _containers.GetContainers().ToList())
        {
            _containers.RemoveContainer(container.Id);
        }
        _persistence.DeleteSnapshotFile();

        _boxRegistry.Clear();

        await InitializeAsync();
        //await BuildDefaultContainerAsync();
        //RegisterAllBoxes();
        //EnsureSurface();

        //_mainVm.Containers.CollectionChanged -= Containers_CollectionChanged;
        //var builtVms = await Task.Run(() => _mainVm.BuildContainerViewModels(_containers.GetContainers()));
        //foreach (var vm in builtVms)
        //{
        //    _mainVm.Containers.Add(vm);
        //}

        //_mainVm.Containers.CollectionChanged += Containers_CollectionChanged;

        //await AddWindowsAsync();
        //await SaveAsync();
    }

    public void CloseAll()
    {
        try { _widgetPause?.Dispose(); } catch { }
        _widgetPause = null;
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        StopIconVisibilityWatch();
        _coordinator.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        // Debounce timers are otherwise rooted for app lifetime and keep firing into the
        // torn-down session (rescale passes re-arm the icon-visibility hook per pass).
        try { _rescaleDebounce?.Stop(); } catch { }
        try { _verifyTimer?.Stop(); } catch { }
        // The layer host (and its Explorer-scoped WinEvent hook) must not survive the
        // disabled window: re-enable lazily recreates it via RequireWidgetLayerHost.
        try
        {
            if (_widgetLayerHost != null)
            {
                _widgetLayerHost.LayerLost -= OnWidgetLayerLost;
                _widgetLayerHost.Dispose();
                _widgetLayerHost = null;
            }
        }
        catch { }

        // Snapshot: RemoveWindow mutates the dictionary, and a single failing window must never
        // abort teardown (disable/reset) or crash the app — each close is independently guarded.
        foreach (var id in _windows.Keys.ToList())
        {
            //Win32Apis.AllowHide(new WindowInteropHelper(window).Handle);
            //window.Close();
            try { RemoveWindow(id); }
            catch (Exception ex) { Serilog.Log.Error(ex, "CloseAll: failed to close window {Id}", id); }
        }

        _windows.Clear();
        if (_surface is not null)
        {
            //Win32Apis.AllowHide(new WindowInteropHelper(_surface).Handle);
            _surface?.CloseWindowEx();
            _surface = null;
        }

        // Clear the published handle so nothing can target a dead window.
        Win32Apis.DesktopSurfaceHandle = IntPtr.Zero;
    }

    /// <summary>Registers every box from every container into <see cref="IBoxService"/> so the rule
    /// coordinator can resolve a box by id (snapshot-loaded boxes are not created via the service).</summary>
    private void RegisterAllBoxes()
    {
        foreach (var container in _containers.GetContainers())
        {
            if (container.ChildContainer is null)
            {
                continue;
            }

            foreach (var box in container.ChildContainer.Boxes)
            {
                _boxRegistry.AddBox(box);
            }
        }
    }


    public void NewBox()
    {
        _mainVm.CreateBox();
        _ = SaveAsync();
    }

    public void NewFolderPortal()
    {
        var offset = _mainVm.Containers.Count * 24;
        _mainVm.CreateFolderPortalAt(60 + offset, 60 + offset);
        _ = SaveAsync();
    }

    public void NewBoxContainer()
    {
        var offset = _mainVm.Containers.Count * 24;
        _mainVm.CreateBoxContainerAt(60 + offset, 60 + offset);
        _ = SaveAsync();
    }

    public void NewWebWidget(string slug, WebWidgetSource source)
    {
        var offset = _mainVm.Containers.Count * 24;
        // Use manifest default size if available
        var svc = App.Services.GetRequiredService<IWebWidgetService>();
        var info = svc.TryGetWidget(slug, source);
        double w = info?.Manifest.Width ?? 300;
        double h = info?.Manifest.Height ?? 220;
        _mainVm.CreateWebWidgetAt(slug, source, 60 + offset, 60 + offset, w, h);
        _ = SaveAsync();
    }

    public void NewNativeWidget(string slug)
    {
        var offset = _mainVm.Containers.Count * 24;
        // Use manifest default size if available
        var svc = App.Services.GetRequiredService<INativeWidgetService>();
        var info = svc.TryGetWidget(slug);
        double w = info?.Manifest.Width ?? 300;
        double h = info?.Manifest.Height ?? 220;
        _mainVm.CreateNativeWidgetAt(slug, 60 + offset, 60 + offset, w, h);
        _ = SaveAsync();
    }

    public void RegisterContainer(DesktopItemContainer container)
    {
        _containers.AddContainer(container);
        var vm = new ContainerViewModel(container, App.Services.GetRequiredService<IconImageService>(), App.Services.GetRequiredService<IBoxService>());
        _mainVm.Containers.Add(vm);
        _ = SaveAsync();
    }

    public void RemoveContainer(Guid id)
    {
        var vm = _mainVm.Containers.FirstOrDefault(c => c.Id == id);
        if (vm is not null) _mainVm.RemoveContainer(vm);
        else _containers.RemoveContainer(id);
        _ = SaveAsync();
    }

    /// <summary>Hides a container's window and marks the model not visible. Never saves the
    /// snapshot — callers (menus) save explicitly; the Settings containers list must not save.</summary>
    public void HideContainer(Guid id)
    {
        var vm = _mainVm.Containers.FirstOrDefault(c => c.Id == id);
        if (vm is null) return;
        vm.IsVisible = false;
        RemoveWindow(id);
    }

    /// <summary>Marks a container visible and (re)shows its window. Never saves the snapshot.</summary>
    public void ShowContainer(Guid id, bool showActivated = false)
    {
        var vm = _mainVm.Containers.FirstOrDefault(c => c.Id == id);
        if (vm is null) return;
        vm.IsVisible = true;
        if (_windows.TryGetValue(id, out var existing))
        {
            try
            {
                if (!existing.IsVisible) existing.Show();
                if (showActivated) existing.Activate();
                ApplySupervisorStateToWindow(existing);
            }
            catch { }
            return;
        }
        AddWindow(vm, showActivated: showActivated);
    }

    public DesktopSnapshot GetCurrentDesktopSnapshot()
    {
        var current = GetPrimaryWorkAreaDip();
        return new DesktopSnapshot
        {
            DesktopResolution = new SizeD(current.Width, current.Height),
            Containers = _containers.GetContainers().ToList(),
            Rules = _rules.GetRules().ToList(),
        };
    }

    #endregion

    #region Snapshot Save

    /// <summary>
    /// Sync save for app exit and other sync only operations. (No Task)
    /// </summary>
    public void SaveSync()
    {
        _persistence.SaveSnapshot(GetCurrentDesktopSnapshot());
    }
    public async Task SaveAsync()
    {
        await _persistence.SaveSnapshotAsync(GetCurrentDesktopSnapshot());
    }

    /// <summary>
    /// SaveAsync FireAndForget
    /// </summary>
    public void SaveAsyncFireAndForget() => _ = SaveAsync();

    /// <summary>
    /// Creates a .dbe1 bundle (zip) containing boxes.snapshot.json, UserSettings.json and UserWidgets/
    /// (the "Backup" action in Settings). The live snapshot is flushed first so the backup is up to date.
    /// </summary>
    public async Task BackupAsync(string destinationPath)
    {
        await SaveAsync();

        var appDataDir = SettingsService.AppDataDir;
        var snapshotPath = _persistence.SnapshotFilePath;
        var settingsPath = Path.Combine(appDataDir, "UserSettings.json");
        var userWidgetsRoot = Path.Combine(appDataDir, "UserWidgets");

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // Build bundle as a zip archive with .dbe1 extension
        using var fs = File.Create(destinationPath);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        if (File.Exists(snapshotPath))
        {
            archive.CreateEntryFromFile(snapshotPath, "boxes.snapshot.json", CompressionLevel.Optimal);
        }

        if (File.Exists(settingsPath))
        {
            archive.CreateEntryFromFile(settingsPath, "UserSettings.json", CompressionLevel.Optimal);
        }

        if (Directory.Exists(userWidgetsRoot))
        {
            var files = Directory.EnumerateFiles(userWidgetsRoot, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
            {
                // Preserve empty folder so restore knows it existed
                archive.CreateEntry("UserWidgets/");
            }
            else
            {
                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(userWidgetsRoot, file);
                    var entryName = Path.Combine("UserWidgets", relative).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }
        }
    }

    /// <summary>
    /// Restores from a .dbe1 bundle or a legacy JSON snapshot file, then re-initializes boxes.
    /// Bundle restore replaces UserSettings.json and UserWidgets/ before reloading the snapshot.
    /// </summary>
    public async Task RestoreAsync(string sourcePath)
    {
        if (IsBundleFile(sourcePath))
        {
            await RestoreBundleAsync(sourcePath);
            return;
        }

        // Legacy: plain JSON snapshot file
        var legacySnapshot = await _persistence.LoadFromFileAsync(sourcePath);
        if (legacySnapshot is null)
        {
            return;
        }

        CloseAll();
        foreach (var container in _containers.GetContainers().ToList())
        {
            _containers.RemoveContainer(container.Id);
        }
        _boxRegistry.Clear();

        await _persistence.SaveSnapshotAsync(legacySnapshot);
        await InitializeAsync();
    }

    private static bool IsBundleFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".dbe1", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Fallback: peek header for zip signature (PK\x03\x04) even if extension is .json
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            Span<byte> header = stackalloc byte[4];
            fs.ReadExactly(header);
            return header[0] == 0x50 && header[1] == 0x4B && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07);
        }
        catch
        {
            return false;
        }
    }

    private async Task RestoreBundleAsync(string bundlePath)
    {
        var appDataDir = SettingsService.AppDataDir;
        var snapshotPath = _persistence.SnapshotFilePath;
        var settingsPath = Path.Combine(appDataDir, "UserSettings.json");
        var userWidgetsRoot = Path.Combine(appDataDir, "UserWidgets");

        // Extract bundle to a temp directory first so we can validate before touching live files
        var tempDir = Path.Combine(Path.GetTempPath(), "DesktopBoxesRestore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using (var fs = File.OpenRead(bundlePath))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var fullName = entry.FullName.Replace('\\', '/');

                    // Directory entry
                    if (string.IsNullOrEmpty(entry.Name) && fullName.EndsWith('/'))
                        continue;

                    // Security: prevent zip-slip and absolute paths
                    if (fullName.Contains("..") || Path.IsPathRooted(fullName))
                        continue;

                    // Only allow known prefixes
                    bool allowed = fullName.Equals("boxes.snapshot.json", StringComparison.OrdinalIgnoreCase) ||
                                   fullName.Equals("UserSettings.json", StringComparison.OrdinalIgnoreCase) ||
                                   fullName.StartsWith("UserWidgets/", StringComparison.OrdinalIgnoreCase);
                    if (!allowed)
                        continue;

                    var destPath = Path.Combine(tempDir, fullName);
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                    // Skip directory entries
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    using var entryStream = entry.Open();
                    using var outStream = File.Create(destPath);
                    await entryStream.CopyToAsync(outStream);
                }
            }

            var extractedSnapshot = Path.Combine(tempDir, "boxes.snapshot.json");
            var extractedSettings = Path.Combine(tempDir, "UserSettings.json");
            var extractedWidgets = Path.Combine(tempDir, "UserWidgets");

            DesktopSnapshot? snapshot = null;
            if (File.Exists(extractedSnapshot))
            {
                snapshot = await _persistence.LoadFromFileAsync(extractedSnapshot);
            }

            // If bundle has no snapshot we cannot proceed with layout restore,
            // but still restore settings/widgets if present
            if (snapshot is null && !File.Exists(extractedSettings) && !Directory.Exists(extractedWidgets))
            {
                return;
            }

            if (snapshot is not null)
            {
                // Prepare to re-init: close windows and clear state first
                CloseAll();
                foreach (var container in _containers.GetContainers().ToList())
                {
                    _containers.RemoveContainer(container.Id);
                }
                _boxRegistry.Clear();

                await _persistence.SaveSnapshotAsync(snapshot);
            }

            // Restore UserSettings.json
            if (File.Exists(extractedSettings))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.Copy(extractedSettings, settingsPath, overwrite: true);
                try
                {
                    _settingsService.Load();
                    // Re-apply appearance/theme on UI thread
                    var app = Application.Current;
                    if (app is not null)
                    {
                        if (app.Dispatcher.CheckAccess())
                        {
                            App.ApplyTheme(_settingsService.UserSettings.SelectedTheme);
                            App.ApplyBoxAppearance();
                            App.SyncDesktopIconSizeWatcher();
                        }
                        else
                        {
                            app.Dispatcher.Invoke(() =>
                            {
                                App.ApplyTheme(_settingsService.UserSettings.SelectedTheme);
                                App.ApplyBoxAppearance();
                                App.SyncDesktopIconSizeWatcher();
                            });
                        }
                    }
                }
                catch { }
            }

            // Restore UserWidgets: replace entire folder if bundle contained it
            bool hasWidgetEntries = Directory.Exists(extractedWidgets) &&
                                    (Directory.EnumerateFileSystemEntries(extractedWidgets).Any() ||
                                     Directory.Exists(Path.Combine(tempDir, "UserWidgets")));
            // Also consider empty UserWidgets/ marker
            if (Directory.Exists(extractedWidgets) || hasWidgetEntries)
            {
                try
                {
                    if (Directory.Exists(userWidgetsRoot))
                        Directory.Delete(userWidgetsRoot, recursive: true);
                }
                catch { }

                if (Directory.Exists(extractedWidgets))
                {
                    CopyDirectory(extractedWidgets, userWidgetsRoot);
                }
                else
                {
                    // Bundle had empty UserWidgets/ marker
                    Directory.CreateDirectory(userWidgetsRoot);
                }
            }

            if (snapshot is not null)
            {
                await InitializeAsync();
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destPath = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }

    #endregion

    #region Surface

    private void EnsureSurface()
    {
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == false)
        {
            _surface ??= new DesktopSurface(_mainVm, SaveAsyncFireAndForget);
            RequireWidgetLayerHost().TryEnsureLayer();
            SyncSurfaceWithIconVisibility();
        }
        else
        {
            if (_surface is not null)
            {
                // Boxes may be OWNED by the surface (GlueToDesktop with the published handle), and
                // destroying an owner destroys its owned windows. Re-glue every box back to Progman
                // and unpublish the handle BEFORE closing the surface.
                Win32Apis.DesktopSurfaceHandle = IntPtr.Zero;
                foreach (var window in _windows.Values)
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        Win32Apis.GlueToDesktop(hwnd);
                    }
                }

                _surface.CloseWindowEx();
                _surface = null;
            }
            if (_widgetLayerHost is not null)
            {
                _widgetLayerHost.LayerLost -= OnWidgetLayerLost;
                _widgetLayerHost.Dispose();
                _widgetLayerHost = null;
            }
        }
    }

    /// <summary>Lazy, single host for the custom-surface path. Subscribes layer-loss exactly once;
    /// the hook unsubscribes on teardown so a stale host never fires into a newer session.</summary>
    private DesktopWidgetLayerHost RequireWidgetLayerHost()
    {
        _widgetLayerHost ??= new DesktopWidgetLayerHost();
        _widgetLayerHost.LayerLost -= OnWidgetLayerLost;
        _widgetLayerHost.LayerLost += OnWidgetLayerLost;
        return _widgetLayerHost;
    }

    private void OnWidgetLayerLost()
    {
        // Installed on the UI thread, so this already runs there — but stay safe if that ever changes.
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { RecoverAfterShellRestart(); }
                catch (Exception ex) { Serilog.Log.Error("Widget layer recovery failed", ex); }
            }));
        }
        catch { }
    }

    /// <summary>
    /// Live desktop-icon check (not the cached service flag, which goes stale when the
    /// user toggles icons via Explorer's own menu): true = icons visible, false = hidden,
    /// null = shell unreachable (Explorer dead/restarting).
    /// </summary>
    private static bool? AreDesktopIconsShownLive()
    {
        try
        {
            var listView = Win32.Services.ExplorerDesktopService.FindDesktopListView();
            if (listView == IntPtr.Zero || !Win32Apis.IsWindow(listView))
            {
                return null;
            }

            return Win32Apis.IsWindowVisible(listView);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hides the surface window in place so native desktop input reaches Explorer directly.
    /// Boxes keep their windows, ownership and z-order — hiding (unlike minimizing) an owner
    /// never hides owned windows, and the published handle stays valid so re-show is cheap.
    /// </summary>
    public void HideSurface()
    {
        var surface = _surface;
        if (surface is null)
        {
            return;
        }

        IntPtr hwnd;
        try
        {
            hwnd = new WindowInteropHelper(surface).Handle;
        }
        catch
        {
            return;
        }

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            User32.ReleaseCapture();
        }
        catch
        {
            // Releasing when nothing is captured is a no-op by design; aborting a
            // mid-marquee capture here keeps the hook state consistent after hiding.
        }

        // Same AllowHide wrapping as HideAllBoxes: a plain Hide() posts lParam 0 (not
        // SW_PARENTCLOSING, so the minimize-prevention hook would leave it alone), but
        // this guards against any future hook tightening at zero cost.
        Win32Apis.AllowHide(hwnd);
        try
        {
            surface.Hide();
        }
        finally
        {
            Win32Apis.DisallowHide(hwnd);
        }
    }

    /// <summary>
    /// Re-shows a hidden surface (creating it first when needed) and re-establishes
    /// glue, anchor and geometry. No-op when the global-hook path is active (no surface
    /// exists there by design) and when already visible.
    /// </summary>
    public void ShowSurface()
    {
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface != false)
        {
            return;
        }

        _surface ??= new DesktopSurface(_mainVm, SaveAsyncFireAndForget);
        if (_surface.IsVisible)
        {
            return;
        }

        // Appear without stealing foreground: the re-show is triggered by an icon toggle
        // performed in Explorer, which must keep the foreground.
        _surface.ShowActivated = false;
        _surface.Show();
        var hwnd = new WindowInteropHelper(_surface).Handle;
        if (hwnd != IntPtr.Zero && Win32Apis.IsWindow(hwnd))
        {
            RequireWidgetLayerHost().AttachAboveIcons(hwnd);
            _surface.Relayout();
        }

        EnsureDesktopZOrder();
    }

    /// <summary>
    /// Applies the standing rule: desktop icons shown ⇒ surface hidden (native input flows
    /// to Explorer untouched, boxes stay); icons hidden ⇒ surface shown. Unknown shell
    /// state leaves the surface as-is; shell recovery re-syncs afterwards.
    /// </summary>
    public void SyncSurfaceWithIconVisibility()
    {
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface != false)
        {
            return;
        }

        var shown = AreDesktopIconsShownLive();
        if (shown == true)
        {
            HideSurface();
        }
        else if (shown == false)
        {
            ShowSurface();
        }
    }

    /// <summary>
    /// Re-asserts the desktop z-band once startup settles: every box directly above the
    /// surface (chained bottom-to-top in creation order), the surface itself already pinned
    /// above the desktop anchor by its own guard. Verified need: at startup the surface can
    /// lose the initial z-race and end up ABOVE its owned boxes; being hit-testable everywhere,
    /// it then swallows all box input (no hover, no chrome) until the first click/activation
    /// recomputes owned-above-owner and drops the boxes back on top. A deterministic re-assert
    /// removes the dead-hover window entirely. Never touches application windows (insert-after
    /// chain stays inside our own desktop band) and never activates (NOACTIVATE throughout).
    /// No-op without a live surface (global-hook path, teardown, dead shell).
    /// </summary>
    private void EnsureDesktopZOrder()
    {
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface != false)
        {
            return;
        }

        IntPtr surface;
        try
        {
            surface = Win32Apis.DesktopSurfaceHandle;
        }
        catch
        {
            return;
        }

        if (surface == IntPtr.Zero || !Win32Apis.IsWindow(surface))
        {
            return;
        }

        IntPtr after = surface;
        foreach (var window in _windows.Values)
        {
            IntPtr hwnd;
            try
            {
                hwnd = new WindowInteropHelper(window).Handle;
            }
            catch
            {
                continue;
            }

            if (hwnd == IntPtr.Zero || !Win32Apis.IsWindow(hwnd))
            {
                continue;
            }

            try
            {
                // Pure z-order change: no move/size (KeepBelowApps passes those through
                // untouched), no activation, no visibility change.
                User32.SetWindowPos(hwnd, after, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                after = hwnd;
            }
            catch
            {
                // Best effort per window; one bad handle must not break the rest.
            }
        }
    }

    /// <summary>
    /// Arms the event-driven watch for desktop-icon show/hide toggles (e.g. via Explorer's
    /// own context menu, which bypasses our services): SHOW/HIDE/DESTROY on the Explorer
    /// SysListView32, filtered to that exact window. The callback marshals the sync onto
    /// the UI thread. Idempotent; re-arm after shell restarts (new Explorer PID/handles).
    /// </summary>
    private void StartIconVisibilityWatch()
    {
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface != false)
        {
            return;
        }

        if (_iconWatchHook != IntPtr.Zero && Win32Apis.IsWindow(_iconWatchListView))
        {
            return;
        }

        StopIconVisibilityWatch();
        IntPtr listView;
        try
        {
            listView = Win32.Services.ExplorerDesktopService.FindDesktopListView();
            if (listView == IntPtr.Zero || !Win32Apis.IsWindow(listView))
            {
                return;
            }

            Win32Apis.GetWindowThreadProcessId(listView, out uint explorerPid);
            if (explorerPid == 0)
            {
                return;
            }

            _iconWatchProc = OnIconVisibilityEvent;
            _iconWatchListView = listView;
            _iconWatchHook = Win32Apis.SetWinEventHook(
                EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE,
                IntPtr.Zero, _iconWatchProc, explorerPid, 0, WINEVENT_OUTOFCONTEXT);
            if (_iconWatchHook == IntPtr.Zero)
            {
                _iconWatchProc = null;
                _iconWatchListView = IntPtr.Zero;
            }
        }
        catch
        {
            StopIconVisibilityWatch();
        }
    }

    private void StopIconVisibilityWatch()
    {
        if (_iconWatchHook != IntPtr.Zero)
        {
            try
            {
                Win32Apis.UnhookWinEvent(_iconWatchHook);
            }
            catch
            {
                // Best effort; a dead hook unhooks to false and that is fine.
            }

            _iconWatchHook = IntPtr.Zero;
        }

        _iconWatchProc = null;
        _iconWatchListView = IntPtr.Zero;
    }

    private void OnIconVisibilityEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        try
        {
            if (idObject != OBJID_WINDOW)
            {
                return;
            }

            if (hwnd == IntPtr.Zero || hwnd != _iconWatchListView)
            {
                return;
            }

            if (eventType == EVENT_OBJECT_DESTROY)
            {
                // List-view itself is going away (Explorer crash/restart): stop watching;
                // RecoverAfterShellRestart re-arms against the new shell.
                StopIconVisibilityWatch();
                return;
            }

            if (eventType is not (EVENT_OBJECT_SHOW or EVENT_OBJECT_HIDE))
            {
                return;
            }

            var app = Application.Current;
            if (app is null)
            {
                return;
            }

            // The hook fires on a system thread — the window Hide/Show must run on the UI thread.
            app.Dispatcher.BeginInvoke(new Action(SyncSurfaceWithIconVisibility));
        }
        catch
        {
            // Diagnostics must never break the hook pump.
        }
    }

    /// <summary>
    /// Rebuilds the desktop-layer bindings after Explorer crashed/restarted: the old Progman/WorkerW
    /// windows (and every handle we cached against them) are gone, so the surface must be recreated
    /// or re-glued above the NEW anchor and every box re-owned. Triggered from App's
    /// "TaskbarCreated" broadcast listener.
    /// </summary>
    public void RecoverAfterShellRestart()
    {
        Serilog.Log.Information("Explorer restarted — recovering desktop layer");

        var customSurface = GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == false;

        // Surface: it may have died together with its old owner window — recreate when needed,
        // otherwise just re-glue (owner + NOACTIVATE + insert-above-anchor) and refresh caches.
        // Visibility follows the live icon state (never show over visible icons).
        if (customSurface)
        {
            if (_surface is not null)
            {
                var old = new WindowInteropHelper(_surface).Handle;
                if (old == IntPtr.Zero || !Win32Apis.IsWindow(old))
                {
                    Serilog.Log.Information("Desktop surface died with Explorer — recreating");
                    _surface = null;
                }
            }

            _surface ??= new DesktopSurface(_mainVm, SaveAsyncFireAndForget);

            var sHwnd = new WindowInteropHelper(_surface).Handle;
            _surface.InvalidateShellHandles();
            if (sHwnd != IntPtr.Zero && Win32Apis.IsWindow(sHwnd))
            {
                RequireWidgetLayerHost().AttachAboveIcons(sHwnd);
                _surface.Relayout();
            }

            SyncSurfaceWithIconVisibility();
            StartIconVisibilityWatch();
        }
        else
        {
            Win32Apis.DesktopSurfaceHandle = IntPtr.Zero;
        }

        // Boxes & Widgets: their owner handle pointed at the dead explorer/surface window — re-own them all
        // (to the surface in custom-surface mode, Progman otherwise) so owned-above-owner holds again.
        IntPtr owner = customSurface ? Win32Apis.DesktopSurfaceHandle : IntPtr.Zero;
        foreach (var window in _windows.Values)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && Win32Apis.IsWindow(hwnd))
            {
                Win32Apis.GlueToDesktop(hwnd, owner);
            }
        }

        EnsureDesktopZOrder();
    }
    #endregion

    #region Desktop Manage

    /// <summary>
    /// Show Desktop icons
    /// </summary>
    public void RestoreIcons() => _explorer.SetDesktopIconsVisible(true);

    #endregion
}
public static class DesktopManagerExtensions
{
    public static void CloseWindowEx(this Window window, nint? handle = null)
    {
        if (handle is null) handle = new WindowInteropHelper(window).Handle;
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            // Teardown-time close failures (e.g. HwndHost "Notification Window is null" when hosted
            // content was already torn down) are benign: the window is going away and the OS reclaims
            // the rest. Never let one take down disable/reset/exit.
            Serilog.Log.Debug(ex, "CloseWindowEx: close failed for 0x{Handle:X}, continuing teardown", handle.Value.ToInt64());
        }
    }
}