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

    public sealed class WebView2ProcessItem : ViewModelBase
    {
        private int _pid;
        public int Pid { get => _pid; set => SetField(ref _pid, value); }

        private string _kind = "";
        public string Kind { get => _kind; set => SetField(ref _kind, value); }

        private long _memoryMB;
        public long MemoryMB { get => _memoryMB; set => SetField(ref _memoryMB, value); }

        private double _cpu;
        public double Cpu { get => _cpu; set => SetField(ref _cpu, value); }
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

        private string _webProcess = "-";
        public string WebProcess { get => _webProcess; set => SetField(ref _webProcess, value); }

        private string _preloaded = "-";
        public string Preloaded { get => _preloaded; set => SetField(ref _preloaded, value); }

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

        /// <summary>Previous CPU stamp per WebView2 PID, for the 1 Hz CPU roll-up. Pruned to
        /// live PIDs on every refresh so restarts cannot grow it.</summary>
        private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _wvCpuPrev = new();

        private ulong _prevIoRead;
        private ulong _prevIoWrite;
        private bool _ioBaselineSet;

        public ObservableCollection<PerformanceItem> Items { get; } = new();

        public ObservableCollection<LiveWallpaperMonitorItem> LiveWallpaperMonitors { get; } = new();

        public ObservableCollection<WebView2ProcessItem> WebView2Processes { get; } = new();

        private string _liveWallpaperSummary = "";
        public string LiveWallpaperSummary { get => _liveWallpaperSummary; set => SetField(ref _liveWallpaperSummary, value); }

        private bool _liveWallpaperEnabled;
        public bool LiveWallpaperEnabled { get => _liveWallpaperEnabled; set => SetField(ref _liveWallpaperEnabled, value); }

        private long _totalMemoryMB;
        public long TotalMemoryMB { get => _totalMemoryMB; set => SetField(ref _totalMemoryMB, value); }

        /// <summary>Private bytes (commit) of the main process: committed virtual memory
        /// including paged-out pages, so always higher than resident readings — shown
        /// separately so the numbers are never confused.</summary>
        private long _totalPrivateMB;
        public long TotalPrivateMB { get => _totalPrivateMB; set => SetField(ref _totalPrivateMB, value); }

        /// <summary>Total working set (resident incl. shared pages). Reads higher than the
        /// headline private working set by the shared share (.NET/WPF/Edge DLLs).</summary>
        private long _workingSetMB;
        public long WorkingSetMB { get => _workingSetMB; set => SetField(ref _workingSetMB, value); }

        /// <summary>Main private working set + WebView2 private working sets: the whole
        /// app group in one number.</summary>
        private long _groupMemoryMB;
        public long GroupMemoryMB { get => _groupMemoryMB; set => SetField(ref _groupMemoryMB, value); }

        /// <summary>GDI / USER handle counts (10k GDI quota per process). Climbing GDI with
        /// flat managed heap = HBITMAP/HICON/DC leak; flat GDI with climbing private bytes =
        /// driver mappings or pools.</summary>
        private uint _gdiHandles;
        public uint GdiHandles { get => _gdiHandles; set => SetField(ref _gdiHandles, value); }

        private uint _userHandles;
        public uint UserHandles { get => _userHandles; set => SetField(ref _userHandles, value); }

        /// <summary>WPF render tier readout: HW (tiers 1-2, GPU composition) vs SW fallback
        /// (tier 0 — everything rasterized through GDI: high CPU, GDI churn, GBs native).</summary>
        private string _renderTier = "Render: ?";
        public string RenderTier { get => _renderTier; set => SetField(ref _renderTier, value); }

        private bool _isSoftwareRendering;
        public bool IsSoftwareRendering { get => _isSoftwareRendering; set => SetField(ref _isSoftwareRendering, value); }

        private static (string Text, bool IsSoftware) DescribeRenderTier()
        {
            try
            {
                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                return tier > 0 ? ($"Render: HW (tier {tier})", false) : ("Render: SW fallback! (tier 0)", true);
            }
            catch { return ("Render: ?", false); }
        }

        private double _groupCpu;
        public double GroupCpu { get => _groupCpu; set => SetField(ref _groupCpu, value); }

        private long _managedMemoryMB;
        public long ManagedMemoryMB { get => _managedMemoryMB; set => SetField(ref _managedMemoryMB, value); }

        private double _totalCpu;
        public double TotalCpu { get => _totalCpu; set => SetField(ref _totalCpu, value); }

        private string _totalIo = "I/O —";
        public string TotalIo { get => _totalIo; set => SetField(ref _totalIo, value); }

        private int _webView2ProcessCount;
        public int WebView2ProcessCount { get => _webView2ProcessCount; set => SetField(ref _webView2ProcessCount, value); }

        private long _webView2MemoryMB;
        public long WebView2MemoryMB { get => _webView2MemoryMB; set => SetField(ref _webView2MemoryMB, value); }

        private double _webView2Cpu;
        public double WebView2Cpu { get => _webView2Cpu; set => SetField(ref _webView2Cpu, value); }

        private bool _hasWebView2Processes;
        public bool HasWebView2Processes { get => _hasWebView2Processes; set => SetField(ref _hasWebView2Processes, value); }

        private bool _isWebView2Expanded = true;
        public bool IsWebView2Expanded { get => _isWebView2Expanded; set => SetField(ref _isWebView2Expanded, value); }

        private bool _isSummaryExpanded = true;
        public bool IsSummaryExpanded { get => _isSummaryExpanded; set => SetField(ref _isSummaryExpanded, value); }

        private bool _isWindowsExpanded = true;
        public bool IsWindowsExpanded { get => _isWindowsExpanded; set => SetField(ref _isWindowsExpanded, value); }

        private bool _isLiveWallpaperExpanded = true;
        public bool IsLiveWallpaperExpanded { get => _isLiveWallpaperExpanded; set => SetField(ref _isLiveWallpaperExpanded, value); }

        private bool _isIdleMode;
        public bool IsIdleMode { get => _isIdleMode; set => SetField(ref _isIdleMode, value); }

        private bool _isAppDisabled;
        public bool IsAppDisabled { get => _isAppDisabled; set => SetField(ref _isAppDisabled, value); }

        private bool _isLiveWallpaperDisabled;
        public bool IsLiveWallpaperDisabled { get => _isLiveWallpaperDisabled; set => SetField(ref _isLiveWallpaperDisabled, value); }

        private string _layoutAnchorStatus = "";
        public string LayoutAnchorStatus { get => _layoutAnchorStatus; set => SetField(ref _layoutAnchorStatus, value); }

        public PerformanceMonitorViewModel()
        {
            _prevTotalProcessorTime = _process.TotalProcessorTime;
            _prevTime = DateTime.UtcNow;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTimerTick;
            _timer.Start();
            Refresh();
        }

        private void OnTimerTick(object? sender, EventArgs e) => Refresh();

        private static string FormatBounds(double x, double y, double width, double height)
            => $"({x:0},{y:0} {width:0}x{height:0})";

        private static string FormatBytes(double bytes)
            => bytes switch
            {
                < 1024 => $"{bytes:0} B",
                < 1024 * 1024 => $"{bytes / 1024:0.0} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024 * 1024):0.0} MB",
                _ => $"{bytes / (1024L * 1024 * 1024):0.00} GB",
            };

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
                // Headline = private working set (EX2): resident RAM private to the process.
                // Total working set additionally includes shared pages (~100+ MB of
                // .NET/WPF/Edge DLLs), and commit (private bytes) additionally includes
                // paged-out memory — both read higher and are shown as separate lines so
                // the three are never confused.
                TotalMemoryMB = Win32.NativeMethods.Win32Apis.TryGetPrivateWorkingSet(_process.Handle, out ulong pws)
                    ? (long)(pws / 1024 / 1024)
                    : _process.WorkingSet64 / 1024 / 1024;
                try { WorkingSetMB = _process.WorkingSet64 / 1024 / 1024; } catch { }
                try { TotalPrivateMB = _process.PrivateMemorySize64 / 1024 / 1024; } catch { }
                // Managed heap split: if Total climbs while this stays flat, the growth is native
                // (Media Foundation, COM, GDI) rather than .NET objects.
                try { ManagedMemoryMB = System.GC.GetTotalMemory(false) / 1024 / 1024; } catch { }
                // Handle-leak forensics (cheap GetGuiResources, 1 Hz): separates GDI leaks
                // from driver-mapping / pool growth. See property docs.
                try { GdiHandles = Win32.NativeMethods.Win32Apis.GetGuiHandleCount(_process.Handle, false); } catch { }
                try { UserHandles = Win32.NativeMethods.Win32Apis.GetGuiHandleCount(_process.Handle, true); } catch { }
                // GPU-fallback sensor (UI thread here): a post-resume driver drop to tier 0
                // explains sawtooth GDI in the thousands + GBs native + idle CPU with flat heap.
                try { (RenderTier, IsSoftwareRendering) = DescribeRenderTier(); } catch { }

                // I/O throughput (read + write bytes/sec across disk/network/device).
                try
                {
                    if (Win32.NativeMethods.Win32Apis.TryGetProcessIoCounters(_process.Handle, out ulong ioRead, out ulong ioWrite))
                    {
                        if (_ioBaselineSet && deltaMs > 0)
                        {
                            double r = ioRead >= _prevIoRead ? (ioRead - _prevIoRead) / (deltaMs / 1000.0) : 0;
                            double w = ioWrite >= _prevIoWrite ? (ioWrite - _prevIoWrite) / (deltaMs / 1000.0) : 0;
                            TotalIo = $"R {FormatBytes(r)}/s · W {FormatBytes(w)}/s";
                        }
                        _prevIoRead = ioRead;
                        _prevIoWrite = ioWrite;
                        _ioBaselineSet = true;
                    }
                    else
                    {
                        TotalIo = "I/O —";
                    }
                }
                catch { }

                // WebView2: known browser PIDs (widget BrowserProcessId + live-wallpaper
                // monitor states) PLUS a full descendant scan: every msedgewebview2.exe whose
                // ancestor chain reaches our PID. BrowserProcessId only yields the top-level
                // browser process — renderer/GPU/utility children would otherwise be invisible
                // while the group roll-up below sums them. Memory uses private working set so
                // the roll-up stays consistent (falls back to total working set per process).
                var wvIds = new HashSet<int>();
                var browserIds = new HashSet<int>();
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
                                if (pid.HasValue) { wvIds.Add(pid.Value); browserIds.Add(pid.Value); }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                try
                {
                    var lw = App.Services?.GetService<LiveWallpaperManager>();
                    if (lw is not null)
                    {
                        foreach (var s in lw.GetMonitorStates())
                        {
                            if (s.WebProcessId != 0) { int pid = unchecked((int)s.WebProcessId); wvIds.Add(pid); browserIds.Add(pid); }
                        }
                    }
                }
                catch { }
                Dictionary<int, int> parentMap;
                try { parentMap = Win32.NativeMethods.Win32Apis.GetProcessParentMap(); }
                catch { parentMap = new Dictionary<int, int>(); }
                int ownPid = _process.Id;
                try
                {
                    foreach (var p in Process.GetProcessesByName("msedgewebview2"))
                    {
                        try
                        {
                            int pid = p.Id;
                            if (wvIds.Contains(pid)) continue;
                            if (Win32.NativeMethods.Win32Apis.IsDescendantOf(pid, ownPid, parentMap))
                                wvIds.Add(pid);
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
                int wvCount = 0;
                long wvMem = 0;
                double wvCpu = 0;
                var wvSeen = new HashSet<int>();
                var wvRows = new Dictionary<int, (string Kind, long MemMB, double Cpu)>();
                foreach (var pid in wvIds)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        p.Refresh();
                        long ws = Win32.NativeMethods.Win32Apis.TryGetPrivateWorkingSet(p.Handle, out ulong pwv)
                            ? (long)pwv
                            : p.WorkingSet64;
                        long memMB = ws / 1024 / 1024;
                        wvMem += ws;
                        wvCount++;
                        var cur = p.TotalProcessorTime;
                        double sample = 0;
                        if (_wvCpuPrev.TryGetValue(pid, out var prev))
                        {
                            double elMs = (now - prev.At).TotalMilliseconds;
                            if (elMs > 0) sample = (cur - prev.Cpu).TotalMilliseconds / elMs / _processorCount * 100.0;
                            sample = Math.Clamp(sample, 0, 100);
                        }
                        _wvCpuPrev[pid] = (cur, now);
                        wvSeen.Add(pid);
                        wvCpu += sample;
                        string kind = browserIds.Contains(pid) ? "Browser"
                            : parentMap.TryGetValue(pid, out int ppid) && ppid == ownPid ? "Browser" : "Child";
                        wvRows[pid] = (kind, memMB, sample);
                    }
                    catch { }
                }
                // Drop vanished PIDs so the table cannot grow across wallpaper/browser restarts.
                foreach (var dead in _wvCpuPrev.Keys.Where(k => !wvSeen.Contains(k)).ToList()) _wvCpuPrev.Remove(dead);
                WebView2ProcessCount = wvCount;
                HasWebView2Processes = wvCount > 0;
                WebView2MemoryMB = wvMem / 1024 / 1024;
                WebView2Cpu = Math.Clamp(wvCpu, 0, 100);
                // Group roll-up: main + WebView2 for the whole app group.
                GroupMemoryMB = TotalMemoryMB + WebView2MemoryMB;
                GroupCpu = Math.Clamp(TotalCpu + WebView2Cpu, 0, 100);
                try
                {
                    var stale = WebView2Processes.Where(r => !wvSeen.Contains(r.Pid)).ToList();
                    foreach (var r in stale) WebView2Processes.Remove(r);
                    foreach (var kv in wvRows.OrderBy(kv => kv.Key))
                    {
                        var existing = WebView2Processes.FirstOrDefault(r => r.Pid == kv.Key);
                        if (existing is null)
                            WebView2Processes.Add(new WebView2ProcessItem { Pid = kv.Key, Kind = kv.Value.Kind, MemoryMB = kv.Value.MemMB, Cpu = kv.Value.Cpu });
                        else
                        {
                            existing.Kind = kv.Value.Kind;
                            existing.MemoryMB = kv.Value.MemMB;
                            existing.Cpu = kv.Value.Cpu;
                        }
                    }
                }
                catch { }

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
                        type = "Widget (Web)";
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
                        type = "Widget (Native)";
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

                RefreshLiveWallpaper();

                // Idle badge covers all containers AND live wallpapers: idle only when every
                // container is hidden/suspended and no wallpaper is playing (Hide All counts).
                var dm = App.Services?.GetService<DesktopManager>();
                bool allHidden = dm?.AllBoxesHidden == true;
                bool anyActiveContainer = Items.Any(i => i.IsVisible && !i.IsSuspended);
                bool anyWallpaperPlaying = LiveWallpaperMonitors.Any(m => m.IsPlaying);
                IsIdleMode = allHidden || (!anyActiveContainer && !anyWallpaperPlaying);
                try { IsAppDisabled = dm?.IsDisabled == true; } catch { }
                try { LayoutAnchorStatus = dm?.GetLayoutAnchorStatus() ?? "DesktopManager unavailable"; } catch { }
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
                    IsLiveWallpaperDisabled = true;
                    LiveWallpaperSummary = "unavailable";
                    LiveWallpaperMonitors.Clear();
                    return;
                }
                LiveWallpaperEnabled = lw.IsEnabled;
                IsLiveWallpaperDisabled = !lw.IsEnabled;
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
                    existing.WebProcess = s.WebProcessId == 0 ? "-" : s.WebProcessId.ToString();
                    existing.Preloaded = s.PreloadedBytes > 0 ? $"{s.PreloadedBytes / 1024 / 1024} MB" : "-";
                    existing.Status = !lw.EngineIsLive ? "Engine down"
                        : s.IsPaused ? $"Paused"
                        : s.IsPlaying ? "Playing"
                        : s.IsLoaded ? "Loaded" : "Loading";
                }
                foreach (var r in toRemove) LiveWallpaperMonitors.Remove(r);
                long preloadedTotal = 0;
                try { preloadedTotal = lw.GetPreloadTotalBytes(); } catch { }
                string preloadedText = preloadedTotal > 0 ? $", preloaded {preloadedTotal / 1024 / 1024} MB" : "";
                var baseSummary = !lw.IsEnabled ? "Disabled"
                    : !lw.HasWallpaper ? "Enabled, no file"
                    : !lw.EngineIsLive ? $"Enabled, engine down ({states.Count} windows)"
                    : $"{states.Count} monitor(s), {playing} playing, {paused} paused{preloadedText}";

                // Append MediaPlayer pool stats + rebuild forensics when available.
                // Rebuild count climbing across resumes with settled-disagree/layer-lost names
                // the native strand multiplier (each full destroy strands ~32 MB/monitor).
                try
                {
                    var stats = DesktopLiveWallPaperEngine.Rendering.VideoRenderer.GetPoolStats();
                    string rebuilds = "";
                    try { rebuilds = $" · {lw.GetReapplyInfo()}"; } catch { }
                    LiveWallpaperSummary = $"{baseSummary} · MPPool: created={stats.Created}, pool={stats.PoolSize}, inuse={stats.Rented}, rentals={stats.Rentals}, returns={stats.Returns}, max={stats.MaxPool}{rebuilds}";
                }
                catch
                {
                    LiveWallpaperSummary = baseSummary;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            // The old code detached a freshly allocated lambda here, which never matched the
            // subscription — the 1 Hz refresh kept running after the window closed.
            try { _timer.Stop(); } catch { }
            try { _timer.Tick -= OnTimerTick; } catch { }
            try { _process.Dispose(); } catch { }
        }
    }
}
