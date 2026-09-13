using DesktopLiveWallPaperEngine.Common;
using Microsoft.Web.WebView2.Core;
using WindowsNative;
using static WindowsNative.Win32Constants;

namespace DesktopLiveWallPaperEngine.Rendering;

/// <summary>HTML wallpapers (a single .html file whose sibling .css/.js resolve relatively,
/// or a folder whose index.html is used). Rendered with a classic WebView2 HWND controller
/// (<c>CreateCoreWebView2ControllerAsync</c>) filling the wallpaper window's client area —
/// the same architecture dedicated wallpaper apps (e.g. Lively) use.
///
/// A composition controller (<c>CreateCoreWebView2CompositionControllerAsync</c> + DComp
/// visual) was tried first and rejected with evidence: navigation completed and no errors
/// were raised, but the visual never presented a single frame — a black desktop — across
/// hidden/visible parents, both member orders and transparent/opaque backgrounds (verified
/// with per-pixel screen-capture analysis). Known caveat: on raised desktops
/// (Win11 24H2+) child redirection surfaces are documented as not composed; if this
/// renderer is black there while video wallpapers show, that layering — not WebView2 — is
/// the cause, and it needs a revisit with the machine's topology in hand.
///
/// Threading: WebView2 objects have strict thread affinity (creation thread must be STA with
/// a message pump, and every member access must happen there). So every WebView2 touch runs
/// on a dedicated, process-lifetime STA thread with its own Win32 message pump (the same
/// pattern as the playback hook thread), and async continuations flow back to it through a
/// custom <see cref="SynchronizationContext"/>. The instance itself is thread-agnostic like
/// the other renderers: Load/Pause/Resume/Dispose may be called from any thread; flags are
/// read under <see cref="_sync"/> and all controller work is queued FIFO onto that thread,
/// which also preserves pause-after-load ordering.
///
/// The controller's parent is the wallpaper window (engine thread, which pumps): WebView2
/// synchronously interacts with the parent during creation, so the parent thread must pump
/// — verified empirically (creation deadlocks when the owner thread is blocked).</summary>
public sealed class WebViewRenderer : IWallpaperRenderer
{
    private readonly IntPtr _hwnd; // wallpaper window: layer member (engine thread) + WebView parent
    private readonly string _userDataFolder;
    private readonly string? _staticFramePath;
    private readonly Action? _onStaticFrame;
    private readonly Lock _sync = new();

    // STA-thread-only. Touched exclusively via WebViewThread Post/Send.
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private EventHandler<CoreWebView2NavigationCompletedEventArgs>? _navigationHandler;

    private readonly int _width;
    private readonly int _height;
    private int _loadGeneration;
    private bool _loaded;
    private bool _paused;
    private bool _pauseDeferred;
    private bool _muted;
    private volatile bool _disposed;
    private string _path = "";

    private static Task<CoreWebView2Environment>? s_envTask;

    public WebViewRenderer(CompositionHost host, IntPtr hwnd, int width, int height,
        string userDataFolder, bool muted, string? staticFramePath = null, Action? onStaticFrame = null)
    {
        // host is unused: an HWND child paints the window client area itself, so unlike the
        // video/image renderers this one needs no composition surface. The parameter stays
        // so the Engine call site treats all renderers uniformly.
        _hwnd = hwnd;
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        _userDataFolder = userDataFolder;
        _muted = muted;
        _staticFramePath = staticFramePath;
        _onStaticFrame = onStaticFrame;
    }

    /// <summary>The shared WebView2 STA thread: a background STA thread running a plain
    /// <c>GetMessage</c> pump for a hidden wake-up window, plus a <see cref="SynchronizationContext"/>
    /// installed on that thread so async continuations marshal back to it. The WebView2
    /// controller itself is parented to the wallpaper window (engine thread, which pumps) —
    /// never to the hidden window: WebView2 drives frame production from the parent's
    /// visibility state, and an always-hidden parent yields a permanently black render.</summary>
    private static class WebViewThread
    {
        private const uint RunActionsMessage = WM_APP + 7;

        private static volatile IntPtr s_hostHwnd;
        private static volatile int s_threadId;
        private static HostWindow? s_host; // rooted process-lifetime; the thread never shuts down
        private static readonly WebSyncContext s_sync = new();
        private static readonly object s_gate = new();
        private static readonly ManualResetEventSlim s_ready = new(false);

        public static bool IsCurrentThread =>
            Environment.CurrentManagedThreadId == s_threadId;

        /// <summary>Starts the thread on first use (idempotent) and reports whether its
        /// message pump is up. Call before Post/PostAsync/Send.</summary>
        public static bool Ensure()
        {
            if (s_hostHwnd != IntPtr.Zero) return true;
            lock (s_gate)
            {
                if (s_hostHwnd != IntPtr.Zero) return true;
                s_ready.Reset();
                var thread = new Thread(ThreadMain) { IsBackground = true, Name = "WebWallpaperUI" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            if (!s_ready.Wait(TimeSpan.FromSeconds(10)))
            {
                Serilog.Log.Error("Web wallpaper UI thread did not start.");
                return false;
            }
            return s_hostHwnd != IntPtr.Zero;
        }

        public static void Post(Action action) => s_sync.Post(_ => action(), null);

        public static void PostAsync(Func<Task> factory) => s_sync.Post(_ => { _ = factory(); }, null);

        /// <summary>Synchronous round-trip, bounded so a stuck WebView2 cannot hang engine
        /// teardown. Safe to call from the STA thread itself (runs inline).</summary>
        public static void Send(Action action, TimeSpan timeout) => s_sync.Send(_ => action(), null, timeout);

        private static void ThreadMain()
        {
            try
            {
                s_threadId = Environment.CurrentManagedThreadId;
                SynchronizationContext.SetSynchronizationContext(s_sync);
                var host = new HostWindow();
                s_host = host;
                s_hostHwnd = host.Hwnd;
                s_ready.Set();
                while (User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    User32.TranslateMessage(ref msg);
                    User32.DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error("Web wallpaper UI thread failed", ex);
                s_ready.Set();
            }
        }

        private sealed class WebSyncContext : SynchronizationContext
        {
            private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
            private readonly object _queueGate = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                lock (_queueGate) _queue.Enqueue((d, state));
                var hwnd = s_hostHwnd;
                if (hwnd != IntPtr.Zero)
                    User32.PostMessageW(hwnd, RunActionsMessage, IntPtr.Zero, IntPtr.Zero);
            }

            public void Send(SendOrPostCallback d, object? state, TimeSpan timeout)
            {
                if (IsCurrentThread)
                {
                    d(state);
                    return;
                }
                using var done = new ManualResetEventSlim(false);
                Exception? error = null;
                Post(_ =>
                {
                    try { d(state); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                }, null);
                if (!done.Wait(timeout))
                    throw new TimeoutException("Web wallpaper UI thread did not respond.");
                if (error is not null)
                    throw error;
            }

            public void Drain()
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) item;
                    lock (_queueGate)
                    {
                        if (_queue.Count == 0) return;
                        item = _queue.Dequeue();
                    }
                    try { item.Callback(item.State); }
                    catch (Exception ex) { Serilog.Log.Error("Web wallpaper action failed", ex); }
                }
            }
        }

        private sealed class HostWindow : Win32Window
        {
            public HostWindow()
            {
                // Hidden, never shown: purely a wake-up target for the queued-work message.
                CreateWindow("DLWEngineWebHost",
                    WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                    0, 0, 1, 1, IntPtr.Zero);
            }

            protected override IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
            {
                if (msg == RunActionsMessage)
                {
                    s_sync.Drain();
                    return IntPtr.Zero;
                }
                return base.HandleMessage(msg, wParam, lParam);
            }
        }
    }

    public bool IsMuted
    {
        get { lock (_sync) return _muted; }
        set
        {
            lock (_sync)
            {
                if (_disposed) return;
                _muted = value;
            }
            // The controller lives on the STA thread — marshal the apply.
            if (WebViewThread.Ensure())
                WebViewThread.Post(() =>
                {
                    try { if (_core is not null) _core.IsMuted = value; } catch { }
                });
        }
    }

    public void Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_sync)
        {
            if (_disposed) return;
            _paused = false;
            _pauseDeferred = false;
            _loaded = false;
        }
        _path = path;
        int generation = Interlocked.Increment(ref _loadGeneration);
        if (!WebViewThread.Ensure())
            return;
        // Queued FIFO: overlapping Loads serialize here and the generation guard inside
        // drops every stale one before it can create a controller.
        WebViewThread.PostAsync(() => InitializeAsync(generation, path));
    }

    public bool IsLoaded()
    {
        lock (_sync) return _loaded && !_disposed && _controller is not null;
    }

    private static Task<CoreWebView2Environment> GetEnvironmentAsync(string userDataFolder)
    {
        // No lock needed: first write wins, and the engine always passes the same folder.
        s_envTask ??= CoreWebView2Environment.CreateAsync(null, userDataFolder);
        return s_envTask;
    }

    /// <summary>Runs on the WebView STA thread (queued from <see cref="Load"/>); every await
    /// below captures the thread's context and continues on it. The parent window lives on
    /// the engine thread, which must be pumping — WebView2 interacts with it synchronously
    /// during creation (verified: creation deadlocks when the owner thread is blocked).</summary>
    private async Task InitializeAsync(int generation, string path)
    {
        try
        {
            try { Directory.CreateDirectory(_userDataFolder); }
            catch (Exception ex)
            {
                Serilog.Log.Error($"Web wallpaper user-data dir failed: {_userDataFolder}", ex);
                return;
            }

            var env = await GetEnvironmentAsync(_userDataFolder);
            if (!CheckGeneration(generation)) return;

            if (!WindowsNative.User32.IsWindow(_hwnd))
            {
                Serilog.Log.Warning("Web wallpaper parent window is gone; aborting load.");
                return;
            }

            CoreWebView2Controller controller;
            try
            {
                controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error("WebView2 controller failed — is the Evergreen runtime installed?", ex);
                return;
            }
            if (!CheckGeneration(generation))
            {
                try { controller.Close(); } catch { }
                return;
            }

            lock (_sync)
            {
                if (_disposed || generation != Volatile.Read(ref _loadGeneration))
                {
                    try { controller.Close(); } catch { }
                    return;
                }
                // A re-Load while a previous init was in flight: drop the stale controller.
                DetachControllerLocked();
                _controller = controller;
                _core = controller.CoreWebView2;
            }

            try
            {
                // Fill the wallpaper window's client area (physical pixels; the window is
                // per-monitor DPI aware). The window is never resized afterwards — a layout
                // change recreates the window and this renderer with it.
                controller.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                controller.Bounds = new System.Drawing.Rectangle(0, 0, _width, _height);
                controller.IsVisible = true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error("Web wallpaper controller setup failed", ex);
                DetachControllerLocked();
                return;
            }

            var core = controller.CoreWebView2;
            try
            {
                var settings = core.Settings;
#if DEBUG
                settings.AreDevToolsEnabled = true;
#else
                settings.AreDevToolsEnabled = false;
#endif
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                core.IsMuted = IsMuted;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning($"Web wallpaper settings failed: {ex.Message}");
            }

            _navigationHandler = (_, args) => OnNavigationCompleted(generation, args);
            core.NavigationCompleted += _navigationHandler;

            if (TryBuildNavigation(path, out string? url, out string? html))
            {
                if (url is not null)
                    core.Navigate(url);
                else
                    core.NavigateToString(html!);
            }
            else
            {
                Serilog.Log.Warning($"Web wallpaper has no entry page: {path}");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Web wallpaper init failed", ex);
        }
    }

    private bool CheckGeneration(int generation) =>
        generation == Volatile.Read(ref _loadGeneration) && !_disposed;

    /// <summary>Resolves the entry point: a folder's index page, an .html file navigated by
    /// file:// URL (so sibling .css/.js resolve relatively), or a lone .css/.js wrapped in a
    /// minimal document.</summary>
    private static bool TryBuildNavigation(string path, out string? url, out string? html)
    {
        url = null;
        html = null;
        try
        {
            string entry = path;
            if (Directory.Exists(path))
            {
                entry = ResolveFolderEntry(path);
                if (entry == "") return false;
            }
            if (!File.Exists(entry)) return false;

            string ext = Path.GetExtension(entry);
            if (ext.Equals(".css", StringComparison.OrdinalIgnoreCase))
            {
                string css = File.ReadAllText(entry);
                html = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{css}</style></head><body></body></html>";
                return true;
            }
            if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
            {
                string js = File.ReadAllText(entry);
                html = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><script>{js}</script></body></html>";
                return true;
            }
            url = new Uri(Path.GetFullPath(entry)).AbsoluteUri;
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Web wallpaper entry resolve failed: {ex.Message}");
            return false;
        }
    }

    private static string ResolveFolderEntry(string folder)
    {
        string[] preferred = ["index.html", "index.htm", "main.html", "wallpaper.html"];
        foreach (var name in preferred)
        {
            string candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        try
        {
            return Directory.EnumerateFiles(folder, "*.html").OrderBy(f => f).FirstOrDefault() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Raised on the WebView STA thread (the controller's creation thread).</summary>
    private void OnNavigationCompleted(int generation, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            if (!args.IsSuccess)
            {
                Serilog.Log.Warning($"Web wallpaper navigation failed ({args.WebErrorStatus}): {_path}");
                return;
            }
            lock (_sync)
            {
                if (generation != _loadGeneration || _disposed) return;
                _loaded = true;
            }
            Serilog.Log.Information($"Web wallpaper loaded: {_path}");
            WebViewThread.PostAsync(() => CaptureStaticAsync(generation));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Web navigation handler failed", ex);
        }
    }

    /// <summary>Snapshots the settled page for the static desktop-switch fallback (Task View /
    /// virtual-desktop slide paints the OS wallpaper, not our layer). Runs on the WebView STA
    /// thread; delayed so JS animations reach a representative frame; honors a pause that
    /// landed meanwhile.</summary>
    private async Task CaptureStaticAsync(int generation)
    {
        try
        {
            await Task.Delay(1500);
            CoreWebView2? core;
            string? staticPath;
            Action? onStatic;
            lock (_sync)
            {
                if (_disposed || generation != _loadGeneration || !_loaded) return;
                core = _core;
                staticPath = _staticFramePath;
                onStatic = _onStaticFrame;
            }
            if (core is null || staticPath is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(staticPath)!);
                using var stream = File.Create(staticPath);
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                onStatic?.Invoke();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning($"Web static capture failed: {ex.Message}");
            }
            lock (_sync)
            {
                if (_pauseDeferred && generation == _loadGeneration && !_disposed)
                {
                    _pauseDeferred = false;
                    try { if (_controller is not null) _controller.IsVisible = false; } catch { }
                    _ = SuspendAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Web static capture failed: {ex.Message}");
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _paused = true;
            // Defer hiding until the static fallback is captured — same contract as video.
            if (!_loaded && _staticFramePath is not null)
            {
                _pauseDeferred = true;
                return;
            }
        }
        if (WebViewThread.Ensure())
            WebViewThread.Post(() =>
            {
                try
                {
                    lock (_sync)
                    {
                        if (_disposed) return;
                        try { if (_controller is not null) _controller.IsVisible = false; } catch { }
                    }
                    _ = SuspendAsync();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning($"Web pause failed: {ex.Message}");
                }
            });
    }

    /// <summary>Runs on the WebView STA thread.</summary>
    private async Task SuspendAsync()
    {
        try
        {
            var core = _core;
            if (core is null) return;
            try { await core.TrySuspendAsync(); }
            catch
            {
                try { await core.ExecuteScriptAsync("window.__suspended=true; window.dispatchEvent(new Event('suspend'));"); } catch { }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Web suspend failed: {ex.Message}");
        }
    }

    public bool IsPaused()
    {
        lock (_sync) return _paused;
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _paused = false;
            _pauseDeferred = false;
        }
        if (WebViewThread.Ensure())
            WebViewThread.Post(() =>
            {
                try
                {
                    lock (_sync)
                    {
                        if (_disposed) return;
                        try { if (_controller is not null) _controller.IsVisible = true; } catch { }
                    }
                    try { _core?.Resume(); } catch { }
                    try { _ = _core?.ExecuteScriptAsync("window.__suspended=false; window.dispatchEvent(new Event('resume'));"); } catch { }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning($"Web resume failed: {ex.Message}");
                }
            });
    }

    public bool IsPlaying()
    {
        lock (_sync) return _loaded && !_paused && !_disposed && _controller is not null;
    }

    public void Paint(IntPtr hdc) { /* HWND child paints itself; nothing to do on WM_PAINT */ }

    /// <summary>Caller holds <see cref="_sync"/> and runs on the WebView STA thread.</summary>
    private void DetachControllerLocked()
    {
        if (_navigationHandler is not null && _core is not null)
        {
            try { _core.NavigationCompleted -= _navigationHandler; } catch { }
            _navigationHandler = null;
        }
        if (_controller is not null)
        {
            try { _controller.IsVisible = false; } catch { }
            try { _controller.Close(); } catch { }
            _controller = null;
        }
        _core = null;
    }

    public void Dispose()
    {
        // Invalidate in-flight inits first so a late continuation drops its controller.
        Interlocked.Increment(ref _loadGeneration);
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Teardown must run on the STA thread that owns the controller.
        if (!WebViewThread.Ensure())
            return;
        try
        {
            WebViewThread.Send(() =>
            {
                lock (_sync) DetachControllerLocked();
            }, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Web wallpaper teardown timed out: {ex.Message}");
        }
    }
}
