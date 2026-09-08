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

        public PerformanceMonitorViewModel()
        {
            _prevTotalProcessorTime = _process.TotalProcessorTime;
            _prevTime = DateTime.UtcNow;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
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
                    foreach (var win in System.Windows.Application.Current.Windows.OfType<Views.Containers.CssWidgetWindow>())
                    {
                        try
                        {
                            var field = typeof(Views.Containers.CssWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field?.GetValue(win) is Controls.ContainersControls.CssWidgetControl ctrl)
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

                // Per-window items
                var toRemove = Items.ToList();
                foreach (var win in System.Windows.Application.Current.Windows.OfType<System.Windows.Window>())
                {
                    string name, type;
                    bool isVisible = win.IsVisible;
                    bool isSuspended = false;
                    if (win is Views.Containers.BoxContainerWindow bcw)
                    {
                        name = bcw.DataContext is ContainerViewModel cvm ? cvm.Title ?? "BoxContainer" : "BoxContainer";
                        type = "Box";
                        // Box has no WebView, suspended when hidden
                        isSuspended = !isVisible || win.WindowState == System.Windows.WindowState.Minimized;
                    }
                    else if (win is Views.Containers.CssWidgetWindow cww)
                    {
                        name = cww.Title ?? "Widget";
                        type = "Widget";
                        // Check if widget's WebView is suspended (via control)
                        try
                        {
                            var field = typeof(Views.Containers.CssWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (field?.GetValue(cww) is Controls.ContainersControls.CssWidgetControl ctrl)
                            {
                                isSuspended = ctrl.IsSuspended;
                            }
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
                    // Approximate per-window memory via process is not per-window, use total divided
                }
                foreach (var r in toRemove) Items.Remove(r);

                // Idle detection: if all widget windows hidden/minimized or AllBoxesHidden, we are idle
                var dm = App.Services?.GetService<DesktopManager>();
                bool allHidden = dm?.AllBoxesHidden == true;
                bool anyVisibleWidget = Items.Any(i => i.Type == "Widget" && i.IsVisible && !i.IsSuspended);
                IsIdleMode = allHidden || !anyVisibleWidget;

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
