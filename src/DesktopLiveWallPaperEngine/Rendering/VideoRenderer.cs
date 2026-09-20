using DesktopLiveWallPaperEngine.Config;
using System.IO;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace DesktopLiveWallPaperEngine.Rendering;

/// <summary>Hardware-decoded looping video via WinRT MediaPlayer in frame-server mode:
/// each decoded frame is copied onto the wallpaper's composition surface and presented.
/// Zero timers — fully event-driven, so a paused video costs nothing.</summary>
public sealed class VideoRenderer : IWallpaperRenderer
{
    private readonly CompositionHost _hostComposition;
    private readonly int _width;
    private readonly int _height;
    private readonly FitMode _fit;
    private readonly Lock _sync = new();

    private CompositionSurface _surfaceHost;
    private IDirect3DSurface? _surface;

    private MediaPlayer? _player;
    private MediaSource? _source;
    private Windows.Foundation.Rect? _targetRect;
    private bool _retriedAfterFailure;
    private int _loadGeneration;
    private bool _disposed;
    private string _path = "";

    /// <summary>Raised when playback has failed and the one retry has also failed. Until
    /// 2026-08-20 a codec Windows cannot decode produced a log line and a black desktop, with
    /// nothing shown to the user. Carries (path, codecOrError) so the caller can name the fix.</summary>
    public event Action<string, string>? PlaybackFailed;

    /// <summary>Always true: decoded frames are copied onto the host's composition surface.</summary>
    public bool IsGPURender => true;

    private readonly string? _staticFramePath;
    private readonly Action? _onStaticFrame;
    private readonly PreloadedMediaCache? _preload;
    private PreloadedMedia? _lease;
    private Windows.Storage.Streams.InMemoryRandomAccessStream? _memStream;
    private int _staticCropX;
    private int _staticCropY;
    private bool _staticCaptured;
    private int _frameCount;
    private bool _pauseDeferred;
    /// <summary>Explicit desired-state ( Pause() sets, Resume()/Load() clears). MediaPlayer.CurrentState
    /// transitions asynchronously through Opening/Buffering, so it cannot answer "should I pause now".</summary>
    private bool _paused;

    public VideoRenderer(CompositionHost host, int width, int height, FitMode fit, bool muted, double volume,
        string? staticFramePath = null, Action? onStaticFrame = null, PreloadedMediaCache? preload = null)
    {
        _hostComposition = host;
        _width = width;
        _height = height;
        _fit = fit;
        _staticFramePath = staticFramePath;
        _onStaticFrame = onStaticFrame;
        _preload = preload;

        _surfaceHost = host.CreateContent(width, height);
        WrapBackBuffer();

        _player = new MediaPlayer
        {
            IsVideoFrameServerEnabled = true,
            IsLoopingEnabled = true,
            IsMuted = muted,
            Volume = volume,
            RealTimePlayback = true,
            AutoPlay = false, // sources are set explicitly; upgrades swap without auto-starting
        };
        _player.CommandManager.IsEnabled = false; // keep media keys away from the wallpaper
        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
    }

    private void WrapBackBuffer()
    {
        if (_surfaceHost.IsDisposed) return;
        _surface?.Dispose();
        using var dxgiSurface = _surfaceHost.BackBuffer.QueryInterface<IDXGISurface>();
        _surface = Interop.Direct3DInterop.CreateSurfaceFromDxgi(dxgiSurface.NativePointer);
    }

    public bool IsMuted
    {
        get => _player?.IsMuted ?? true;
        set { if (_player is not null) _player.IsMuted = value; }
    }

    public double Volume
    {
        get => _player?.Volume ?? 0;
        set { if (_player is not null) _player.Volume = Math.Clamp(value, 0, 1); }
    }

    public void Load(string path)
    {
        if (_player is null) return;
        lock (_sync) { _paused = false; _pauseDeferred = false; }
        _path = path;
        // Identifies this selection for the delayed retry below. Without it, a retry armed for the
        // previous file lands a second later and reinstates a source that Load has already disposed
        // and replaced — either an error on a dead source or the new wallpaper silently reverting.
        int generation = Interlocked.Increment(ref _loadGeneration);
        _retriedAfterFailure = false; // a new file deserves its own retry
        lock (_sync) DropLeaseLocked();
        var previous = _source;
        _source = MediaSource.CreateFromUri(new Uri(path));
        _player.Source = _source;
        previous?.Dispose(); // a re-Load would otherwise strand the old source
        _player.Play();
        Serilog.Log.Information($"Video loaded: {path}");
        // Small files play better from RAM: read on the pool, then swap the file source for the
        // memory one (at most one restart, invisible on a looping wallpaper).
        _ = UpgradeToPreloadedAsync(path, generation);
    }

    /// <summary>Content types for stream-backed playback. Unknown extensions skip preloading
    /// (the URI path handles them).</summary>
    private static readonly IReadOnlyDictionary<string, string> StreamContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".m4v"] = "video/x-m4v",
            [".mov"] = "video/quicktime",
            [".avi"] = "video/x-msvideo",
            [".wmv"] = "video/x-ms-wmv",
            [".webm"] = "video/webm",
            [".mkv"] = "video/x-matroska",
        };

    private async Task UpgradeToPreloadedAsync(string path, int generation)
    {
        try
        {
            if (_preload is null)
            {
                if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                    Serilog.Log.Debug("Video preload skipped (no cache) for {Path}", path);
                return;
            }
            if (!StreamContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
            {
                if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                    Serilog.Log.Debug("Video preload skipped by design for {Path} (extension not preloaded; streaming from disk)", path);
                return;
            }
            // Explicit hit check first so the log answers "is the cache working?": AcquireAsync
            // hides hit-vs-fill, which previously made that unanswerable. TryGet already
            // refcounts; the miss branch fills through AcquireAsync (exactly one ref either way,
            // released on every abort path below as before).
            var entry = _preload.TryGet(path);
            bool hit = entry is not null;
            entry ??= await _preload.AcquireAsync(path).ConfigureAwait(false);
            if (entry is null)
            {
                if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Warning))
                    Serilog.Log.Warning("Video preload unavailable for {Path} (over the {CapMb} MB cap, unreadable, or changed on disk) — looping playback will keep streaming from disk", path, _preload.MaxBytes / 1024 / 1024);
                return;
            }
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            try
            {
                using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                {
                    writer.WriteBytes(entry.Bytes);
                    await writer.StoreAsync().AsTask().ConfigureAwait(false);
                    await writer.FlushAsync().AsTask().ConfigureAwait(false);
                    // Detach BEFORE the using disposes the writer: disposing a DataWriter
                    // closes its underlying stream, so without this every swap died at the
                    // Seek below with ObjectDisposedException (swallowed by the catch) and
                    // playback never left disk — i.e. preloading never worked.
                    writer.DetachStream();
                }

                stream.Seek(0);
            }
            catch (Exception ex)
            {
                if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Warning))
                    Serilog.Log.Warning("Video preload fill failed for {Path} ({Error}); keeping disk stream", path, ex.Message);
                try { stream.Dispose(); } catch { }
                _preload.Release(entry);
                return;
            }

            MediaSource? mem = null;
            lock (_sync)
            {
                if (_disposed || _player is null
                    || generation != Volatile.Read(ref _loadGeneration)
                    || !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                        Serilog.Log.Debug("Video preload swap skipped for {Path} (superseded by a newer load or tearing down)", path);
                    try { stream.Dispose(); } catch { }
                    _preload.Release(entry);
                    return;
                }

                try
                {
                    mem = MediaSource.CreateFromStream(stream, contentType);
                }
                catch (Exception ex)
                {
                    if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Warning))
                        Serilog.Log.Warning("Memory-source swap failed ({Error}); keeping disk stream", ex.Message);
                    try { stream.Dispose(); } catch { }
                    _preload.Release(entry);
                    return;
                }

                var previous = _source;
                try
                {
                    _player.Source = mem;
                }
                catch (Exception ex)
                {
                    // Roll back: the file source stays live, the memory objects die here.
                    if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                        Serilog.Log.Debug("Video preload swap failed for {Path} ({Error}); keeping disk stream", path, ex.Message);
                    try { mem.Dispose(); } catch { }
                    try { stream.Dispose(); } catch { }
                    _preload.Release(entry);
                    return;
                }

                _source = mem;
                DropLeaseLocked();
                _lease = entry;
                _memStream = stream;
                previous?.Dispose();
                if (!_paused)
                {
                    try { _player.Play(); } catch { }
                }

                if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
                    Serilog.Log.Information("Video preload cache {Outcome} ({SharedMb} MB shared): {Path}", hit ? "HIT" : "MISS + filled", entry.Length / 1024 / 1024, path);
            }
        }
        catch (Exception ex)
        {
            if (Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                Serilog.Log.Debug(ex, "Video preload upgrade failed for {Path}", path);
        }
    }

    /// <summary>Drops the preloaded lease and its stream. Caller holds <see cref="_sync"/>.</summary>
    private void DropLeaseLocked()
    {
        var stream = _memStream;
        _memStream = null;
        try { stream?.Dispose(); } catch { }
        var lease = _lease;
        _lease = null;
        if (lease != null)
        {
            try { _preload?.Release(lease); } catch { }
        }
    }
    public bool IsLoaded()
    {
        lock (_sync) return _player is not null && _player.Source is not null;
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        try
        {
            uint videoW = sender.PlaybackSession.NaturalVideoWidth;
            uint videoH = sender.PlaybackSession.NaturalVideoHeight;
            var fit = FitCalculator.Compute((int)videoW, (int)videoH, _width, _height, _fit);
            lock (_sync)
            {
                if (_disposed) return;
                if (fit is { X: 0, Y: 0 } && fit.Width == _width && fit.Height == _height)
                {
                    _targetRect = null;
                }
                else if (fit.X < 0 || fit.Y < 0)
                {
                    // Cover-crop: content surface sized to the scaled video, the window
                    // clips the overflow. (CopyFrameToVideoSurface mishandles rects that
                    // overflow the surface, so never pass those.)
                    //
                    // The replace is atomic against newer wallpaper installs sharing this host:
                    // a superseded (or dead) surface is returned untouched, and this stale
                    // renderer then drops out without disturbing the live video.
                    var previous = _surfaceHost;
                    var replacement = _hostComposition.ReplaceContentIfCurrent(previous, fit.Width, fit.Height, fit.X, fit.Y);
                    if (ReferenceEquals(replacement, previous))
                        return;
                    _surface?.Dispose();
                    _surface = null;
                    _surfaceHost = replacement;
                    WrapBackBuffer();
                    _targetRect = null;
                    _staticCropX = -fit.X; // visible monitor region within the oversized surface
                    _staticCropY = -fit.Y;
                }
                else
                {
                    // Letterbox: draw into a centered sub-rect over a black clear.
                    _targetRect = new Windows.Foundation.Rect(fit.X, fit.Y, fit.Width, fit.Height);
                }
                // A Pause that landed while the player was still opening would otherwise be lost
                // (Pause defers only for static capture, and Play() already ran in Load).
                // The deferred path captures first and pauses after; re-assert only the rest.
                if (_paused && !_pauseDeferred)
                {
                    try { _player?.Pause(); } catch { }
                }
            }
            Serilog.Log.Information($"Media opened {videoW}x{videoH} for {_width}x{_height}, fit: {fit}, surface: {_surfaceHost.Width}x{_surfaceHost.Height}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("MediaOpened handler failed", ex);
        }
    }

    /// <summary>Loop safety net: IsLoopingEnabled handles most files internally (MediaEnded
    /// never fires then), but frame-server playback of some sources ends without looping —
    /// so restart explicitly when it does.</summary>
    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
            }
            sender.PlaybackSession.Position = TimeSpan.Zero;
            sender.Play();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Video loop restart failed", ex);
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Serilog.Log.Error($"Video playback failed: {args.Error} / {args.ErrorMessage} (0x{args.ExtendedErrorCode.HResult:X8}). " +
                  "If this is HEVC/VP9/AV1, the matching (free) codec extension from the Microsoft Store may be missing.");
        if (!_retriedAfterFailure)
        {
            _retriedAfterFailure = true;
            var source = sender.Source;
            int failedGeneration = Volatile.Read(ref _loadGeneration);
            _ = Task.Delay(1000).ContinueWith(_ =>
            {
                try
                {
                    if (_disposed || _player is null) return;
                    if (failedGeneration != Volatile.Read(ref _loadGeneration)) return; // superseded
                    _player.Source = source;
                    _player.Play();
                }
                catch (Exception ex) { Serilog.Log.Error("Video retry failed", ex); }
            });
            return;
        }

        // Second failure: the retry did not help, so stop logging into the void and say so.
        PlaybackFailed?.Invoke(_path, DescribeFailure(args));
    }

    /// <summary>Best available codec name. MediaPlayerFailedEventArgs does not carry the FourCC,
    /// so an unsupported-format error is reported as exactly that rather than guessed at — a
    /// wrong codec name in an error message is worse than an honest "unsupported".</summary>
    private static string DescribeFailure(MediaPlayerFailedEventArgs args) =>
        args.Error == MediaPlayerError.DecodingError || args.Error == MediaPlayerError.SourceNotSupported
            ? "unsupported or missing decoder"
            : $"{args.Error}";

    private void OnVideoFrameAvailable(MediaPlayer sender, object args)
    {
        // The static snapshot's pixels are read under the lock (backbuffer access) but
        // encoded + written outside it — GDI+ PNG encode and disk IO must never stall
        // Pause/Resume/Dispose/OnMediaOpened behind the frame callback.
        byte[]? staticPixels = null;
        int staticW = 0, staticH = 0;
        bool captureAttempted = false;
        lock (_sync)
        {
            if (_disposed || _surface is null || _surfaceHost.IsDisposed) return;
            try
            {
                if (_targetRect is { } rect)
                {
                    _surfaceHost.ClearBlack();
                    sender.CopyFrameToVideoSurface(_surface, rect);
                }
                else
                {
                    sender.CopyFrameToVideoSurface(_surface);
                }

                // Snapshot a settled frame for the static desktop-switch fallback, before
                // Present (flip-model backbuffer is undefined afterward). Skip the first few
                // frames — the earliest can predate the video dimensions settling.
                if (!_staticCaptured && _staticFramePath is not null && ++_frameCount >= 3)
                {
                    _staticCaptured = true;
                    captureAttempted = true;
                    staticPixels = _surfaceHost.ReadRegionBytes(_staticCropX, _staticCropY, _width, _height, out staticW, out staticH);
                }

                _surfaceHost.Present();
            }
            catch (ObjectDisposedException) { return; } // lost the race with teardown — shutting down
            catch (NullReferenceException) { return; } // Vortice NULL native pointer — shutting down
            catch (Exception ex)
            {
                Serilog.Log.Error("Frame present failed", ex);
            }
        }

        if (staticPixels is not null)
        {
            try
            {
                SaveStaticPng(staticPixels, staticW, staticH, _staticFramePath!);
                _onStaticFrame?.Invoke();
            }
            catch (Exception ex) { Serilog.Log.Error("Static frame capture failed", ex); }
        }

        if (captureAttempted)
        {
            // Honor a pause that was deferred so this frame could be captured — independent
            // of the save/present outcome above, as before.
            lock (_sync)
            {
                if (_pauseDeferred && !_disposed)
                {
                    _pauseDeferred = false;
                    try { _player?.Pause(); } catch { }
                }
            }
        }
    }

    /// <summary>Lock-free: encodes BGRA pixels captured above into the static fallback PNG.
    /// Runs on the MF callback thread but outside the renderer lock.</summary>
    private static void SaveStaticPng(byte[] bgra, int w, int h, string path)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 32bpp stride is always exactly w*4 (DWORD-aligned), matching the packed source.
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bits.Scan0, bgra.Length);
        }
        finally
        {
            bmp.UnlockBits(bits);
        }
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void Pause()
    {
        lock (_sync)
        {
            _paused = true;
            // Defer pausing until the static desktop-switch frame is captured — otherwise a
            // wallpaper applied while a fullscreen app is active would never capture one.
            if (!_staticCaptured && _staticFramePath is not null)
            {
                _pauseDeferred = true;
                return;
            }
            try { _player?.Pause(); } catch { }
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
            _paused = false;
            _pauseDeferred = false;
            try { _player?.Play(); } catch { }
        }
    }

    public bool IsPlaying()
    {
        lock (_sync) return !_paused && _player is not null && _player.Source is not null;
    }

    public void Paint(IntPtr hdc) { /* composition-presented; nothing to do on WM_PAINT */ }

    /// <summary>Releases the Media Foundation pipeline left behind by a disposed
    /// <see cref="MediaPlayer"/>.
    ///
    /// Disposing the player is not enough: every projected child object touched along the way
    /// (<c>PlaybackSession</c>, <c>CommandManager</c>) creates its own RCW holding a COM
    /// reference to the native player, and those RCWs have no Close/Dispose to call. The player
    /// therefore survives its own Dispose and its ~27 worker threads with it, until finalization
    /// runs. Measured directly: threads before=86, after Dispose=86, after a forced collect=56.
    ///
    /// Left alone this compounds — every display change, monitor hot-plug or explorer restart
    /// stranded ~27 threads and ~24 MB, taking a long-running instance past 500 MB and 129
    /// threads. Background teardown calls this directly (it owns no live pipeline then); every
    /// other caller must use <see cref="ScheduleReclaimMediaPipeline"/> instead: this blocks on
    /// the finalizer queue by design and must never run on the UI thread, run concurrently
    /// with a rebuild, or pile up one stall per wallpaper switch.</summary>
    public static void ReclaimMediaPipeline()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Single-flight gate for <see cref="ReclaimMediaPipeline"/>. A forced full GC is
    /// stop-the-world, so overlapping requests — display-change storms, multi-monitor refresh
    /// loops, rapid wallpaper switches — used to queue a pile of them, each suspending every
    /// managed thread including the one constructing the replacement pipeline. Concurrent
    /// requests now collapse into at most one in-flight collection plus one trailing pass.
    /// Callers schedule it <em>after</em> the replacements are installed and rooted, so the
    /// collect only ever reaps the dead pipeline.</summary>
    private static readonly object s_reclaimGate = new();
    private static Task? s_reclaimTask;
    private static bool s_reclaimQueued;

    /// <summary>Queues a pipeline reclaim without blocking the caller. Safe to call per
    /// wallpaper switch — rapid switches cost one trailing collection, not one each.</summary>
    public static void ScheduleReclaimMediaPipeline()
    {
        lock (s_reclaimGate)
        {
            if (s_reclaimTask is { IsCompleted: false })
            {
                s_reclaimQueued = true;
                return;
            }
            s_reclaimTask = Task.Run(ReclaimLoop);
        }
    }

    private static void ReclaimLoop()
    {
        while (true)
        {
            try
            {
                ReclaimMediaPipeline();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error("Pipeline reclaim failed", ex);
            }
            lock (s_reclaimGate)
            {
                if (!s_reclaimQueued)
                {
                    s_reclaimTask = null;
                    return;
                }
                s_reclaimQueued = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_player is not null)
            {
                _player.VideoFrameAvailable -= OnVideoFrameAvailable;
                _player.MediaOpened -= OnMediaOpened;
                _player.MediaFailed -= OnMediaFailed;
                _player.MediaEnded -= OnMediaEnded;
                try { _player.Pause(); } catch { }

                // Detach the source BEFORE disposing anything. Disposing a MediaSource while it
                // is still assigned leaves the Media Foundation pipeline referenced, and its
                // worker threads are never reclaimed: every re-apply (display change, explorer
                // restart, monitor hot-plug) stranded ~27 threads and ~24 MB, so a laptop that
                // docks a few times a day climbed past 500 MB. Measured, not theorised.
                try { _player.Source = null; } catch { }
                _source?.Dispose();
                _source = null;
                DropLeaseLocked();
                _player.Dispose();
                _player = null;
            }
            _surface?.Dispose();
            _surface = null;
            // Publisher-side release: the engine subscribes per renderer without unsubscribing.
            // Dropping the invocation list here means no in-flight MF callback can invoke into
            // a dead session after teardown, and the dead renderer releases its subscriber.
            PlaybackFailed = null;
            // The content surface and host belong to the WallpaperWindow.
        }
        GC.SuppressFinalize(this);
    }
}
