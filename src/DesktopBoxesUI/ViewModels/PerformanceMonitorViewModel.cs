using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;

namespace DesktopBoxesUI.ViewModels
{
    public sealed class PerformanceItem : ViewModelBase
    {
        private string _name = "";
        public string Name { get => _name; set => SetField(ref _name, value); }

        private string _type = "";
        public string Type { get => _type; set => SetField(ref _type, value); }

        private long _memoryMB;
        public long MemoryMB { get => _memoryMB; set => SetField(ref _memoryMB, value); }

        private double _cpu;
        public double Cpu { get => _cpu; set => SetField(ref _cpu, value); }

        private bool _isVisible;
        public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }

        private bool _isSuspended;
        public bool IsSuspended { get => _isSuspended; set => SetField(ref _isSuspended, value); }

        private string _status = "";
        public string Status { get => _status; set => SetField(ref _status, value); }

        private string _modelBounds = "";
        public string ModelBounds { get => _modelBounds; set => SetField(ref _modelBounds, value); }

        private string _appliedBounds = "";
        public string AppliedBounds { get => _appliedBounds; set => SetField(ref _appliedBounds, value); }

        private bool _boundsMismatch;
        public bool BoundsMismatch { get => _boundsMismatch; set => SetField(ref _boundsMismatch, value); }

        public System.Windows.Window? WindowRef { get; set; }
    }

    public sealed class LiveWallpaperMonitorItem : ViewModelBase
    {
        private string _device = "";
        public string Device { get => _device; set => SetField(ref _device, value); }

        private string _bounds = "";
        public string Bounds { get => _bounds; set => SetField(ref _bounds, value); }

        private string _file = "";
        public string File { get => _file; set => SetField(ref _file, value); }

        private string _renderer = "";
        public string Renderer { get => _renderer; set => SetField(ref _renderer, value); }

        private bool _isLoaded;
        public bool IsLoaded { get => _isLoaded; set => SetField(ref _isLoaded, value); }

        private bool _isPaused;
        public bool IsPaused { get => _isPaused; set => SetField(ref _isPaused, value); }

        private bool _isPlaying;
        public bool IsPlaying { get => _isPlaying; set => SetField(ref _isPlaying, value); }

        private string _pauseReason = "";
        public string PauseReason { get => _pauseReason; set => SetField(ref _pauseReason, value); }

        private string _status = "";
        public string Status { get => _status; set => SetField(ref _status, value); }
    }

    public sealed class PerformanceMonitorViewModel : ViewModelBase, IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Process _process = Process.GetCurrentProcess();
        private TimeSpan _prevTotalProcessorTime;
        private DateTime _prevTime;
        private readonly int _processorCount = Environment.ProcessorCount;

        public ObservableCollection<PerformanceItem> Items { get; } = new();

        public ObservableCollection<LiveWallpaperMonitorItem> LiveWallpaperMonitors { get; } = new();

        private string _liveWallpaperSummary = "";
        public string LiveWallpaperSummary { get => _liveWallpaperSummary; set => SetField(ref _liveWallpaperSummary, value); }

        private bool _liveWallpaperEnabled;
        public bool LiveWallpaperEnabled { get => _liveWallpaperEnabled; set => SetField(ref _liveWallpaperEnabled, value); }

        private long _totalMemoryMB;
        public long TotalMemoryMB { get => _totalMemoryMB; set => SetField(ref _totalMemoryMB, value); }

        private double _totalCpu;
        public double TotalCpu { get => _totalCpu; set => SetField(ref _totalCpu, value); }

        private int _webView2ProcessCount;
        public int WebView2ProcessCount { get => _webView2ProcessCount; set => SetField(ref _webView2ProcessCount, value); }

        private long _webView2MemoryMB;
        public long WebView2MemoryMB { get => _webView2MemoryMB; set => SetField(ref _webView2MemoryMB, value); }

        private bool _isIdleMode;
        public bool IsIdleMode { get => _isIdleMode; set => SetField(ref _isIdleMode, value); }

        private string _layoutAnchorStatus = "";
        public string LayoutAnchorStatus { get => _layoutAnchorStatus; set => SetField(ref _layoutAnchorStatus, value); }

        public PerformanceMonitorViewModel()
        {
            _prevTotalProcessorTime = _process.TotalProcessorTime;
            _prevTime = DateTime.UtcNow;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
        }

        private static string FormatBounds(double x, double y, double width, double height)
            => $"({x:0},{y:0} {width:0}x{height:0})";

        private const double BoundsMismatchToleranceDip = 1.0;

        private static bool BoundsDiffer(double x1, double y1, double w1, double h1,
            double x2, double y2, double w2, double h2)
            => Math.Abs(x1 - x2) > BoundsMismatchToleranceDip
                || Math.Abs(y1 - y2) > BoundsMismatchToleranceDip
                || Math.Abs(w1 - w2) > BoundsMismatchToleranceDip
                || Math.Abs(h1 - h2) > BoundsMismatchToleranceDip;

        /// <summary>
        /// Live window rect in DIPs, read from the native window rect (device pixels) converted
        /// with the window's own DPI scale. This is the ground truth: WPF's cached
        /// Left/Top/Width/Height can disagree with where the window actually is (stale DPI
        /// context, native moves), which is exactly what the mismatch flag must catch.
        /// Falls back to the WPF properties when no handle exists yet.
        /// </summary>
        private static (double X, double Y, double Width, double Height) GetLiveBounds(System.Windows.Window win)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (hwnd != IntPtr.Zero
                    && Win32.NativeMethods.Win32Apis.GetWindowRect(hwnd, out var r))
                {
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
                    double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                    double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                    return (r.Left / sx, r.Top / sy, (r.Right - r.Left) / sx, (r.Bottom - r.Top) / sy);
                }
            }
            catch { }
            return (win.Left, win.Top, win.Width, win.Height);
        }

        private static double MonitorScale()
        {
            try
            {
                uint dpi = DesktopManager.GetPrimaryDpi();
                if (dpi > 0) return dpi / 96.0;
            }
            catch { }
            return 1.0;
        }

        private static bool TryGetNativeRect(System.Windows.Window win, out double l, out double t, out double w, out double h)
        {
            l = t = w = h = 0;
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (hwnd != IntPtr.Zero
                    && Win32.NativeMethods.Win32Apis.GetWindowRect(hwnd, out var r))
                {
                    l = r.Left; t = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Applied-bounds text (physical truth expressed in live monitor DIPs) plus the mismatch
        /// flag. Flags when EITHER the window-space rect or the monitor-space rect diverges from
        /// expected — the two disagree exactly when the window's DPI context is stale.
        /// </summary>
        private static (string Text, bool Mismatch) DescribeLive(
            System.Windows.Window win, double monScale,
            (double X, double Y, double Width, double Height) liveWin,
            Core.Models.RectD expected)
        {
            if (monScale <= 0) monScale = 1.0;
            if (TryGetNativeRect(win, out double nl, out double nt, out double nw, out double nh))
            {
                double ax = nl / monScale, ay = nt / monScale, aw = nw / monScale, ah = nh / monScale;
                bool mismatch =
                    BoundsDiffer(expected.X, expected.Y, expected.Width, expected.Height,
                        liveWin.X, liveWin.Y, liveWin.Width, liveWin.Height)
                    || BoundsDiffer(expected.X, expected.Y, expected.Width, expected.Height,
                        ax, ay, aw, ah);
                return (FormatBounds(ax, ay, aw, ah), mismatch);
            }
            bool fallbackMismatch = BoundsDiffer(expected.X, expected.Y, expected.Width, expected.Height,
                liveWin.X, liveWin.Y, liveWin.Width, liveWin.Height);
            return (FormatBounds(liveWin.X, liveWin.Y, liveWin.Width, liveWin.Height), fallbackMismatch);
        }

        public void Refresh()
        {
            try
            {
                var now = DateTime.UtcNow;
                var total = _process.TotalProcessorTime;
                var deltaMs = (now - _prevTime).TotalMilliseconds;
                var cpuDelta = (total - _prevTotalProcessorTime).TotalMilliseconds;
                var cpu = deltaMs > 0 ? (cpuDelta / deltaMs) / _processorCount * 100.0 : 0;
                TotalCpu = Math.Clamp(cpu, 0, 100);
                _prevTotalProcessorTime = total;
                _prevTime = now;

                _process.Refresh();
                // Use PrivateMemorySize to match Task Manager's "Memory (Private Working Set)" (WorkingSet includes shared pages)
                TotalMemoryMB = _process.PrivateMemorySize64 / 1024 / 1024;

                // WebView2: only our app's widgets (via BrowserProcessId), not all system msedgewebview2
                long wvMem = 0;
                var wvIds = new HashSet<int>();
                try
                {
                    foreach (var win in System.Windows.Application.Current.Windows.OfType<Views.Containers.WebWidgetWindow>())
                    {
                        try
                        {
                            var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field?.GetValue(win) is Controls.ContainersControls.WebWidgetControl ctrl)
                            {
                                var pid = ctrl.BrowserProcessId;
                                if (pid.HasValue) wvIds.Add(pid.Value);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                int wvCount = 0;
                foreach (var pid in wvIds)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        p.Refresh();
                        wvMem += p.PrivateMemorySize64;
                        wvCount++;
                    }
                    catch { }
                }
                // Fallback: if no BrowserProcessId yet (WebView not initialized), show 0
                WebView2ProcessCount = wvCount;
                WebView2MemoryMB = wvMem / 1024 / 1024;

                // Per-window items. Monitor-space rects (native pixels ÷ live monitor DPI)
                // are the physical truth; window-space rects catch WPF-side drift. A stuck
                // window DPI shows up as a mismatch between the two.
                double monScale = MonitorScale();
                var toRemove = Items.ToList();
                foreach (var win in System.Windows.Application.Current.Windows.OfType<System.Windows.Window>())
                {
                    string name, type, modelBounds, appliedBounds;
                    bool isVisible = win.IsVisible;
                    bool isSuspended = false;
                    bool boundsMismatch = false;
                    if (win is Views.Containers.BoxContainerWindow bcw)
                    {
                        name = bcw.DataContext is ContainerViewModel cvm ? cvm.Title ?? "BoxContainer" : "BoxContainer";
                        type = "Box";
                        // Box has no WebView, suspended when hidden
                        isSuspended = !isVisible || win.WindowState == System.Windows.WindowState.Minimized;
                        var model = bcw.DataContext is ContainerViewModel cvm2 ? cvm2.Bounds : default;
                        modelBounds = FormatBounds(model.X, model.Y, model.Width, model.Height);
                        // Live window rect: for a rolled box this is the title strip, while the
                        // model keeps the full home bounds — the difference is expected.
                        var live = GetLiveBounds(bcw);
                        var expected = bcw.GetExpectedDisplayRect();
                        (appliedBounds, boundsMismatch) = DescribeLive(bcw, monScale, live, expected);
                    }
                    else if (win is Views.Containers.WebWidgetWindow cww)
                    {
                        name = cww.Title ?? "Widget";
                        type = "Web";
                        var model = cww.ContainerViewModel.Bounds;
                        modelBounds = FormatBounds(model.X, model.Y, model.Width, model.Height);
                        var live = GetLiveBounds(cww);
                        var expected = cww.GetExpectedDisplayRect();
                        (appliedBounds, boundsMismatch) = DescribeLive(cww, monScale, live, expected);
                        // Check if widget's WebView is suspended (via control)
                        try
                        {
                            var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field?.GetValue(cww) is Controls.ContainersControls.WebWidgetControl ctrl)
                            {
                                isSuspended = ctrl.IsSuspended;
                            }
                        }
                        catch { }
                    }
                    else if (win is Views.Containers.NativeWidgetWindow nww)
                    {
                        name = nww.Title ?? "Widget";
                        type = "Native";
                        var model = nww.ContainerViewModel.Bounds;
                        modelBounds = FormatBounds(model.X, model.Y, model.Width, model.Height);
                        var live = GetLiveBounds(nww);
                        var expected = nww.GetExpectedDisplayRect();
                        (appliedBounds, boundsMismatch) = DescribeLive(nww, monScale, live, expected);
                        try
                        {
                            var plugin = nww.Widget;
                            if (plugin is not null) isSuspended = plugin.IsSuspended;
                        }
                        catch { }
                    }
                    else
                    {
                        continue;
                    }

                    var existing = Items.FirstOrDefault(i => i.WindowRef == win);
                    if (existing == null)
                    {
                        existing = new PerformanceItem { Name = name, Type = type, WindowRef = win };
                        Items.Add(existing);
                        toRemove.Remove(existing);
                    }
                    else
                    {
                        toRemove.Remove(existing);
                    }
                    existing.Name = name;
                    existing.Type = type;
                    existing.WindowRef = win;
                    existing.IsVisible = isVisible;
                    existing.IsSuspended = isSuspended;
                    existing.Status = !isVisible ? "Hidden" : isSuspended ? "Suspended" : "Active";
                    existing.ModelBounds = modelBounds;
                    existing.AppliedBounds = appliedBounds;
                    existing.BoundsMismatch = boundsMismatch;
                    // Approximate per-window memory via process is not per-window, use total divided
                }
                foreach (var r in toRemove) Items.Remove(r);

                // Idle detection: if all widget windows hidden/minimized or AllBoxesHidden, we are idle
                var dm = App.Services?.GetService<DesktopManager>();
                bool allHidden = dm?.AllBoxesHidden == true;
                bool anyVisibleWidget = Items.Any(i => (i.Type == "Web" || i.Type == "Native") && i.IsVisible && !i.IsSuspended);
                IsIdleMode = allHidden || !anyVisibleWidget;
                try { LayoutAnchorStatus = dm?.GetLayoutAnchorStatus() ?? "DesktopManager unavailable"; } catch { }

                RefreshLiveWallpaper();
            }
            catch { }
        }

        private void RefreshLiveWallpaper()
        {
            try
            {
                var lw = App.Services?.GetService<LiveWallpaperManager>();
                if (lw is null)
                {
                    LiveWallpaperEnabled = false;
                    LiveWallpaperSummary = "unavailable";
                    LiveWallpaperMonitors.Clear();
                    return;
                }
                LiveWallpaperEnabled = lw.IsEnabled;
                int paused = 0, playing = 0;
                var states = lw.GetMonitorStates();
                var toRemove = LiveWallpaperMonitors.ToList();
                foreach (var s in states)
                {
                    if (s.IsPaused) paused++;
                    if (s.IsPlaying) playing++;
                    var existing = LiveWallpaperMonitors.FirstOrDefault(i => string.Equals(i.Device, s.Device, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        existing = new LiveWallpaperMonitorItem { Device = s.Device };
                        LiveWallpaperMonitors.Add(existing);
                    }
                    toRemove.Remove(existing);
                    existing.Bounds = s.Bounds;
                    existing.File = s.File;
                    existing.Renderer = s.Renderer;
                    existing.IsLoaded = s.IsLoaded;
                    existing.IsPaused = s.IsPaused;
                    existing.IsPlaying = s.IsPlaying;
                    existing.PauseReason = s.PauseReason;
                    existing.Status = !lw.EngineIsLive ? "Engine down"
                        : s.IsPaused ? $"Paused"
                        : s.IsPlaying ? "Playing"
                        : s.IsLoaded ? "Loaded" : "Loading";
                }
                foreach (var r in toRemove) LiveWallpaperMonitors.Remove(r);
                LiveWallpaperSummary = !lw.IsEnabled ? "Disabled"
                    : !lw.HasWallpaper ? "Enabled, no file"
                    : !lw.EngineIsLive ? $"Enabled, engine down ({states.Count} windows)"
                    : $"{states.Count} monitor(s), {playing} playing, {paused} paused";
            }
            catch { }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= (s, e) => Refresh();
        }
    }
}
