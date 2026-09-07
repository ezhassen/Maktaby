using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Views.Containers;
using DesktopBoxesUI.Win32.NativeMethods;
using DesktopBoxesUI.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

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

    private readonly Dictionary<System.Guid, Window> _windows = new();
    private DesktopSurface? _surface;
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
        }

        _ = StreamRemainingContainersAsync();

        await ReconcileItemsAsync();

        _appliedResolution = GetPrimaryWorkAreaDip();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        //
        _coordinator.Start();
        if (GlobalFeaturesSwitches.UseGlobalMouseHookInsteadOfCustomSurface == true) _mouseMonitor.Start();
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
    /// <see cref="DesktopSnapshot.DesktopResolution"/> and for rescaling — never the raw physical pixels
    /// from <see cref="IMonitorService.GetPrimaryWorkArea"/>.
    /// </summary>
    private static RectD GetPrimaryWorkAreaDip()
    {
        var wa = SystemParameters.WorkArea;
        return RectD.FromXYWH(wa.X, wa.Y, wa.Width, wa.Height);
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

    private void OnWindowDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e) => ScheduleRescale();

    private void RescaleToCurrent()
    {
        // Never rescale mid-gesture: a native move/size loop owns the geometry until it exits.
        if (Helpers.WindowDragController.IsNativeSizing)
        {
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

        if (current.Width == _appliedResolution.Width && current.Height == _appliedResolution.Height)
        {
            // Same size — re-baseline anyway so rounding drift can never accumulate.
            _appliedResolution = current;
            return;
        }

        double sx = current.Width / _appliedResolution.Width;
        double sy = current.Height / _appliedResolution.Height;

        foreach (var vm in _mainVm.Containers)
        {
            // Minimums must match each window type (BoxContainerWindow 160x120,
            // CssWidgetWindow 120x80) — otherwise a shrink would inflate small widgets.
            double minW = vm.Type == DesktopItemContainerType.CssWidget ? 120 : MinContainerWidth;
            double minH = vm.Type == DesktopItemContainerType.CssWidget ? 80 : MinContainerHeight;
            vm.Width = ClampRescaledExtent(vm.Width * sx, current.Width, minW);
            vm.Height = ClampRescaledExtent(vm.Height * sy, current.Height, minH);
            vm.Left = ClampRescaledOrigin(vm.Left * sx, current.X, current.Right, vm.Width);
            vm.Top = ClampRescaledOrigin(vm.Top * sy, current.Y, current.Bottom, vm.Height);
        }

        _appliedResolution = current;

        foreach (var window in _windows.Values)
        {
            if (window is WidgetWindow widgetWindow)
            {
                widgetWindow.ApplyGeometry();
            }
        }

        _surface?.Relayout();
        SyncSurfaceWithIconVisibility();
        StartIconVisibilityWatch();

        _ = SaveAsync();
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

        _containers.CreateContainer(DesktopItemContainerType.BoxContainer, 60, 60, 300, 460, childContainer: boxContainer);

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

            // Analog clock widget directly under the Downloads box
            const string clockSlug = "AnalogClock";
            double clockW = 220;
            double clockH = 220;
            try
            {
                var svc = App.Services?.GetService<ICssWidgetService>();
                var info = svc?.TryGetWidget(clockSlug, CssWidgetSource.App);
                if (info?.Manifest.Width is int mw && mw > 0) clockW = mw;
                if (info?.Manifest.Height is int mh && mh > 0) clockH = mh;
                else
                {
                    // Fallback: read manifest directly if service not yet available
                    var appBase = AppContext.BaseDirectory;
                    var clockManifestPath = Path.Combine(appBase, "CSSWidgets", clockSlug, "widget.json");
                    if (!File.Exists(clockManifestPath))
                    {
                        var devPath = Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "src", "DesktopBoxesUI", "CSSWidgets", clockSlug, "widget.json"));
                        if (File.Exists(devPath)) clockManifestPath = devPath;
                    }
                    if (File.Exists(clockManifestPath))
                    {
                        var json = File.ReadAllText(clockManifestPath);
                        var manifest = System.Text.Json.JsonSerializer.Deserialize<CssWidgetManifest>(json);
                        if (manifest?.Width is int fmw && fmw > 0) clockW = fmw;
                        if (manifest?.Height is int fmh && fmh > 0) clockH = fmh;
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
                _containers.CreateCssWidgetContainer(clockLeft, clockTop, clockW, clockH, clockSlug, CssWidgetSource.App);
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
        return wind is BoxContainerWindow || wind is CssWidgetWindow;//|| wind is CssWidgetChromeOverlay;
    }

    public bool IsDesktopWindow(IntPtr hWnd, bool checkSurfaceToo = true)
    {
        if (checkSurfaceToo && hWnd == Win32Apis.DesktopSurfaceHandle) return true;
        // Check if hwnd belongs to our app's BoxContainerWindow / CssWidgetWindow
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
        if (vm.Type == DesktopItemContainerType.CssWidget)
        {
            window = new CssWidgetWindow(vm)
            {
                ShowActivated = showActivated
            };
        }
        else
        {
            window = new BoxContainerWindow(vm, _mainVm, _positioning, SaveAsyncFireAndForget)
            {
                ShowActivated = showActivated
            };
        }

        _windows[vm.Id] = window;
        // Per-window DPI changes never raise DisplaySettingsChanged — observe them directly so
        // DPI-only switches (same resolution, different scale) also funnel into the rescale path.
        window.DpiChanged += OnWindowDpiChanged;
        // RegisterBoxWindow is done inside each window's OnLoaded for CssWidget; keep for BoxContainer compat
        try { Win32Apis.RegisterBoxWindow(new WindowInteropHelper(window).Handle); } catch { }
        //IntPtr? foregroundWindowHwnd = null;
        //if (!showActivated && focusWorkaround)
        //{
        //    foregroundWindowHwnd = ManualApis.GetForegroundWindow();
        //}
        window.Show();

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
        //var previousHwnd = ManualApis.GetForegroundWindow();

        //Debug.WriteLine($"Previous: 0x{previousHwnd.ToInt64():X}");

        ManualApis.SetForegroundWindow(widgetHwnd);
        //window.Activate();

        //Debug.WriteLine(
        //    $"Widget active: {ManualApis.GetForegroundWindow() == widgetHwnd}");

        if (prevForegroundWindow != IntPtr.Zero &&
            prevForegroundWindow != widgetHwnd)
        {
            //Win32.NativeMethods.Win32Apis.GlueToDesktop(widgetHwnd, Win32Apis.DesktopSurfaceHandle);
            ManualApis.SetActiveWindow(prevForegroundWindow);
            ManualApis.SetForegroundWindow(prevForegroundWindow);
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
    private async Task StreamRemainingContainersAsync()
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
            //await Task.Delay(TimeSpan.FromSeconds(1));
            //_surface!.Focus();
            //_surface!.Activate();
        }
        catch
        {
            // Best-effort background streaming; a window failure must never break startup.
        }
    }

    private void RemoveWindow(System.Guid id)
    {
        if (_windows.TryGetValue(id, out var window))
        {
            try { window.DpiChanged -= OnWindowDpiChanged; } catch { }
            var handle = new WindowInteropHelper(window).Handle;
            Win32Apis.UnregisterBoxWindow(handle);
            //Win32Apis.AllowHide(handle);
            //window.Close();
            window.CloseWindowEx(handle);
            _windows.Remove(id);
        }
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
        StopIconVisibilityWatch();
        _coordinator.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        foreach (var window in _windows)
        {
            //Win32Apis.AllowHide(new WindowInteropHelper(window).Handle);
            //window.Close();
            RemoveWindow(window.Key);
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

    public void NewCssWidget(string slug, CssWidgetSource source)
    {
        var offset = _mainVm.Containers.Count * 24;
        // Use manifest default size if available
        var svc = App.Services.GetRequiredService<ICssWidgetService>();
        var info = svc.TryGetWidget(slug, source);
        double w = info?.Manifest.Width ?? 300;
        double h = info?.Manifest.Height ?? 220;
        _mainVm.CreateCssWidgetAt(slug, source, 60 + offset, 60 + offset, w, h);
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
        }
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
            ManualApis.ReleaseCapture();
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
            Win32Apis.GlueToDesktopSurface(hwnd);
            _surface.Relayout();
        }
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
                ManualApis.EVENT_OBJECT_DESTROY, ManualApis.EVENT_OBJECT_HIDE,
                IntPtr.Zero, _iconWatchProc, explorerPid, 0, ManualApis.WINEVENT_OUTOFCONTEXT);
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
            if (idObject != ManualApis.OBJID_WINDOW)
            {
                return;
            }

            if (hwnd == IntPtr.Zero || hwnd != _iconWatchListView)
            {
                return;
            }

            if (eventType == ManualApis.EVENT_OBJECT_DESTROY)
            {
                // List-view itself is going away (Explorer crash/restart): stop watching;
                // RecoverAfterShellRestart re-arms against the new shell.
                StopIconVisibilityWatch();
                return;
            }

            if (eventType is not (ManualApis.EVENT_OBJECT_SHOW or ManualApis.EVENT_OBJECT_HIDE))
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
                Win32Apis.GlueToDesktopSurface(sHwnd);
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
        window.Close();
    }

}