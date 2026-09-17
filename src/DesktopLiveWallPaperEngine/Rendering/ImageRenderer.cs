using DesktopLiveWallPaperEngine.Config;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DesktopLiveWallPaperEngine.Rendering;

/// <summary>Static images and animated GIFs. The scaled frame is drawn once onto the
/// composition surface (via Direct2D) — zero ongoing CPU/GPU for static images.</summary>
public sealed class ImageRenderer : IWallpaperRenderer
{
    private readonly int _width;
    private readonly int _height;
    private readonly FitMode _fit;
    private readonly Lock _sync = new();

    private readonly CompositionSurface _surface;
    private readonly string? _staticFramePath;
    private readonly Action? _onStaticFrame;
    private Bitmap? _canvas;            // reused draw target sized to the window
    private Image? _animated;           // original image when it is an animated GIF
    private FitRect _rect;
    private bool _animating;
    private bool _disposed;

    public ImageRenderer(CompositionHost host, int width, int height, FitMode fit,
        string? staticFramePath = null, Action? onStaticFrame = null)
    {
        _width = width;
        _height = height;
        _fit = fit;
        _staticFramePath = staticFramePath;
        _onStaticFrame = onStaticFrame;
        _surface = host.CreateContent(width, height);
    }
    bool loaded;
    public void Load(string path)
    {
        // Deliberately FromFile, not a memory buffer: the OS file lock keeps the displayed
        // wallpaper stable — the file cannot be swapped or deleted out from under the live
        // wallpaper. Static images release it immediately after drawing (see below);
        // animated GIFs hold it while displayed and stream frames from disk on demand
        // instead of pinning the whole file in memory.
        var image = Image.FromFile(path);
        lock (_sync)
        {
            if (_disposed)
            {
                image.Dispose();
                return;
            }
            // A re-Load replaces everything: free the previous selection first instead of
            // stranding its bitmaps (and its animator).
            ClearImagesLocked();
            _canvas = new Bitmap(_width, _height, PixelFormat.Format32bppPArgb);
            _rect = FitCalculator.Compute(image.Width, image.Height, _width, _height, _fit);

            if (ImageAnimator.CanAnimate(image))
            {
                _animated = image;
                ImageAnimator.Animate(image, OnFrameChanged);
                _animating = true;
                DrawAndPresent(image, highQuality: false);
                CaptureStatic();
                loaded = true;
                return;
            }

            using (image)
            {
                DrawAndPresent(image, highQuality: true);
            }
            CaptureStatic();
            loaded = true;
        }
    }
    public bool IsLoaded()
    {
        lock (_sync) return loaded;
    }

    private void CaptureStatic()
    {
        if (_staticFramePath is null || _canvas is null) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_staticFramePath)!);
            if (_animated is not null)
            {
                // Animated: animator callbacks keep mutating the canvas — snapshot it.
                // _canvas is already the monitor-sized composed frame (fit applied).
                using var copy = new Bitmap(_canvas);
                copy.Save(_staticFramePath, ImageFormat.Png);
            }
            else
            {
                // Static: the canvas is immutable after Load — save it directly instead of
                // a transient full-size copy (~33 MB at 4K) on every apply.
                _canvas.Save(_staticFramePath, ImageFormat.Png);
            }
            _onStaticFrame?.Invoke();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Static image capture failed", ex);
        }
    }

    private void OnFrameChanged(object? sender, EventArgs e)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || _animated is null) return;
                ImageAnimator.UpdateFrames(_animated);
                DrawAndPresent(_animated, highQuality: false);
            }
        }
        catch (Exception ex)
        {
            // Runs on ImageAnimator's thread — an exception here would kill the process.
            Serilog.Log.Error("GIF frame update failed", ex);
        }
    }

    private void DrawAndPresent(Image image, bool highQuality)
    {
        if (_canvas is null) return;
        using (var g = Graphics.FromImage(_canvas))
        {
            g.Clear(Color.Black);
            if (highQuality)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }
            g.DrawImage(image, new Rectangle(_rect.X, _rect.Y, _rect.Width, _rect.Height));
        }
        try
        {
            _surface.PresentBitmap(_canvas);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error("Image present failed", ex);
        }
    }

    public void Paint(IntPtr hdc) { /* composition-presented */ }

    /// <summary>True while holding an animated GIF (its animator runs and pause supervision
    /// applies). Static images never need the pause supervisor — the engine uses this to
    /// run no hook thread at all for all-static sessions.</summary>
    public bool IsAnimated
    {
        get { lock (_sync) return _animated is not null; }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_animated is not null && _animating)
            {
                ImageAnimator.StopAnimate(_animated, OnFrameChanged);
                _animating = false;
            }
        }
    }
    public bool IsPaused()
    {
        lock (_sync)
        {
            // A static image is neither playing nor pausable — only an animated GIF has a
            // stoppable timeline. Reporting "paused" for statics spammed Resume calls/logs.
            return _animated is not null && !_animating;
        }
    }
    public void Resume()
    {
        lock (_sync)
        {
            if (_animated is not null && !_animating)
            {
                ImageAnimator.Animate(_animated, OnFrameChanged);
                _animating = true;
            }
        }
    }
    public bool IsPlaying()
    {
        lock (_sync)
        {
            return _animated is not null && _animating;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ClearImagesLocked();
            // The content surface and host belong to the WallpaperWindow.
        }
    }

    /// <summary>Frees the current selection's images. Caller holds <see cref="_sync"/> —
    /// shared by Load (re-selection) and Dispose so a re-Load cannot strand bitmaps
    /// or a running animator.</summary>
    private void ClearImagesLocked()
    {
        if (_animated is not null)
        {
            if (_animating) ImageAnimator.StopAnimate(_animated, OnFrameChanged);
            _animated.Dispose();
            _animated = null;
            _animating = false;
        }
        _canvas?.Dispose();
        _canvas = null;
    }
}
