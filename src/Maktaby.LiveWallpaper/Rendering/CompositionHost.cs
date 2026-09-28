using Maktaby.LiveWallpaper.Common;
using Maktaby.Native;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Maktaby.LiveWallpaper.Rendering;

/// <summary>D3D11 + DirectComposition presentation for one wallpaper window, holding a
/// small visual tree: a content surface (video/image) plus any number of keyed overlay
/// surfaces (widgets) composed above it.
///
/// Why DirectComposition: on Windows 11 24H2+ "raised" desktops, Progman opts out of GDI
/// redirection (WS_EX_NOREDIRECTIONBITMAP) and the shell composes the wallpaper WorkerW via
/// the visual layer. Child-window redirection surfaces (GDI, blt-model presents, even
/// UpdateLayeredWindow content) are never composed there — verified empirically on build
/// 26200. Additionally, only render operations (Draw/Clear/video-processor blits) can write
/// flip-model backbuffers, so CPU bitmaps are drawn through Direct2D, and DWM composed only
/// the monitor-spanning window's target in testing — hence one hwnd with a visual tree
/// instead of one hwnd per element.
///
/// The GPU device and its factories are shared process-wide (one <c>ID3D11Device</c> for all
/// monitors): previously every wallpaper window created its own device, so a 3-monitor setup
/// held 3 devices plus their DXGI/D2D/DComp objects, and every re-apply churned all of them.
/// Only the per-HWND target and root visual remain per host. The shared state is
/// reference-counted and released when the last host is disposed, so a full teardown (which
/// disposes every host) still drops the device — correct across adapter changes such as
/// dock/undock — while steady state and re-apply hold exactly one.
///
/// Creation is lazy on top of that: the constructor stores only the HWND, and the device +
/// target + root visual are built on first surface demand (<see cref="CreateContent"/> /
/// <see cref="CreateOverlay"/>). Only the video and image renderers need a surface — the web
/// renderer paints its own child HWND — so a web-only session creates zero GPU objects.</summary>
public sealed class CompositionHost : IDisposable
{
    /// <summary>Borrowed from the shared state (see <see cref="SharedAcquire"/>): valid while
    /// this host is alive. Do NOT dispose — lifetime belongs to the last-host-wins release.</summary>
    public ID3D11Device Device { get; private set; } = null!;
    /// <summary>Borrowed from the shared state, like <see cref="Device"/>.</summary>
    public ID3D11DeviceContext Context { get; private set; } = null!;

    /// <summary>Borrowed from the shared state, like <see cref="Device"/>.</summary>
    private IDXGIFactory2 _dxgiFactory = null!;
    /// <summary>Borrowed from the shared state, like <see cref="Device"/>.</summary>
    private ID2D1Factory _d2dFactory = null!;
    /// <summary>Shared across hosts: one DComp device serves every HWND target.
    /// <c>Commit</c> applies all pending visual changes device-wide, which is harmless here —
    /// each host only ever has its own target's changes pending.</summary>
    private IDCompositionDevice _dcompDevice = null!;
    private IDCompositionTarget _dcompTarget = null!;
    private IDCompositionVisual _rootVisual = null!;
    private bool _disposed;

    /// <summary>Owning window. Stored at construction; the GPU objects for it are built
    /// lazily by <see cref="EnsureInitializedLocked"/>.</summary>
    private readonly IntPtr _hwnd;

    public CompositionSurface? Content { get; private set; }

    /// <summary>Overlays by key, so more than one widget can be composed above the content.
    /// A single field would mean each new widget disposed the previous one.</summary>
    private readonly Dictionary<string, CompositionSurface> _overlays = [];

    /// <summary>Guards the overlay dictionary and every mutation of the visual tree.
    ///
    /// One widget needed no lock here: the clock owned the only overlay and its own lock was
    /// enough. Two do. The clock renders on its timer thread while the info widget renders on
    /// whichever thread its source fired on, and their separate locks protect neither this
    /// dictionary nor the DirectComposition tree they both add and remove visuals from.</summary>
    private readonly Lock _tree = new();

    /// <summary>Raised when a surface reports DXGI device removal or reset. The device belongs
    /// to the host, so a single surface cannot recover on its own — the whole tree is rebuilt
    /// by the engine's re-apply path. Surfaces forward here rather than the engine subscribing
    /// to each one, because surfaces are recreated on every layout change.</summary>
    public event Action? DeviceLost;

    public CompositionHost(IntPtr hwnd)
    {
        // Lightweight: no GPU objects yet. The shared device, this host's target and its
        // root visual are built on first surface demand — a web-only session (whose renderer
        // paints its own child HWND and never calls CreateContent) never touches the GPU.
        _hwnd = hwnd;
    }

    /// <summary>Builds the shared device and this host's target/visual on first surface
    /// demand. Caller holds <see cref="_tree"/>. A failed attempt releases its reference
    /// and leaves the host uninitialized so the next demand retries cleanly.</summary>
    private void EnsureInitializedLocked()
    {
        if (_dcompDevice is not null) return;
        var shared = SharedAcquire();
        bool constructed = false;
        try
        {
            Device = shared.Device;
            Context = shared.Context;
            _dxgiFactory = shared.Dxgi;
            _d2dFactory = shared.D2d;
            _dcompDevice = shared.DComp;
            _dcompDevice.CreateTargetForHwnd(_hwnd, true, out _dcompTarget);
            _rootVisual = _dcompDevice.CreateVisual();

            // DirectComposition composes this target in physical pixels, 1:1 with the window — DWM
            // applies no DPI scale to the visual tree. Everything downstream (window bounds, swapchain
            // sizes, widget offsets) is already physical, so the root transform stays identity.
            // Verified by covering the OS wallpaper with a solid colour and measuring what the live
            // layer actually paints: identity covers 100% of a 2560x1600 display at 150% scaling,
            // while a 96/dpi counter-scale leaves 55.7% of the screen bare.
            LogWindowDpi(_hwnd);

            _dcompTarget.SetRoot(_rootVisual);
            _dcompDevice.Commit();
            constructed = true;
        }
        finally
        {
            if (!constructed)
            {
                try { _rootVisual?.Dispose(); } catch { /* never constructed — best effort */ }
                _rootVisual = null!;
                try { _dcompTarget?.Dispose(); } catch { /* never constructed — best effort */ }
                _dcompTarget = null!;
                // Back to uninitialized: borrowed references must not outlive the release.
                _dcompDevice = null!;
                _d2dFactory = null!;
                _dxgiFactory = null!;
                Context = null!;
                Device = null!;
                SharedRelease();
            }
        }
    }

    /// <summary>Process-wide GPU state shared by every host. Created once on first use,
    /// released when the last host goes away. All members are owned by this object;
    /// <see cref="SharedAcquire"/> transfers ownership into <c>s_shared</c> on success and
    /// disposes every partial allocation on failure.</summary>
    private sealed class SharedState(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIFactory2 dxgi,
        ID2D1Factory d2d,
        IDCompositionDevice dcomp)
    {
        public ID3D11Device Device { get; } = device;
        public ID3D11DeviceContext Context { get; } = context;
        public IDXGIFactory2 Dxgi { get; } = dxgi;
        public ID2D1Factory D2d { get; } = d2d;
        public IDCompositionDevice DComp { get; } = dcomp;
    }

    private static readonly object s_sharedGate = new();
    private static SharedState? s_shared;
    private static int s_sharedRefs;

    /// <summary>Borrows the shared device, creating it for the first host. Callers that fail
    /// to finish constructing after acquiring MUST call <see cref="SharedRelease"/> to balance
    /// the reference — see the constructor's <c>finally</c>.</summary>
    private static SharedState SharedAcquire()
    {
        lock (s_sharedGate)
        {
            if (s_shared is not null)
            {
                s_sharedRefs++;
                return s_shared;
            }

            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            IDXGIFactory2? dxgi = null;
            ID2D1Factory? d2d = null;
            IDCompositionDevice? dcomp = null;
            try
            {
                D3D11.D3D11CreateDevice(null, DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    null, out device).CheckError();
                context = device!.ImmediateContext;

                using (var multithread = context.QueryInterfaceOrNull<ID3D11Multithread>())
                    multithread?.SetMultithreadProtected(true);

                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                dxgi = adapter.GetParent<IDXGIFactory2>();
                d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.MultiThreaded);

                dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

                s_shared = new SharedState(device, context, dxgi, d2d, dcomp);
                s_sharedRefs = 1;
                // Ownership transferred — the finally below must not dispose them.
                device = null;
                context = null;
                dxgi = null;
                d2d = null;
                dcomp = null;
                Serilog.Log.Information("Composition: shared D3D11 device created (first wallpaper window)");
                return s_shared;
            }
            finally
            {
                // Failure path only: success nulled every local above.
                try { dcomp?.Dispose(); } catch { }
                try { d2d?.Dispose(); } catch { }
                try { dxgi?.Dispose(); } catch { }
                try { context?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Releases one host's reference; the last release disposes the shared device
    /// outside the gate (a concurrent <see cref="SharedAcquire"/> then simply creates a new
    /// instance — the two never alias). Idempotent only via the host's <c>_disposed</c> guard:
    /// every <see cref="Dispose"/> path must call this exactly once per successful acquire.</summary>
    private static void SharedRelease()
    {
        SharedState? dead;
        lock (s_sharedGate)
        {
            if (s_shared is null) return;
            s_sharedRefs--;
            if (s_sharedRefs > 0) return;
            dead = s_shared;
            s_shared = null;
            s_sharedRefs = 0;
        }
        Serilog.Log.Information("Composition: shared D3D11 device released (last wallpaper window gone)");
        try { dead.DComp.Dispose(); } catch { }
        try { dead.D2d.Dispose(); } catch { }
        try { dead.Dxgi.Dispose(); } catch { }
        try { dead.Context.Dispose(); } catch { }
        try { dead.Device.Dispose(); } catch { }
    }

    /// <summary>Recorded for bug reports: DPI scaling is where wallpaper layers usually go wrong.</summary>
    private static void LogWindowDpi(IntPtr hwnd)
    {
        try { Serilog.Log.Information($"Composition: window DPI={User32.GetDpiForWindow(hwnd)}, visual tree composed 1:1 in physical pixels"); }
        catch { /* diagnostic only */ }
    }

    /// <summary>The main wallpaper surface (opaque). Recreatable for cover-crop layouts.
    /// First call also builds the GPU objects (see <see cref="EnsureInitializedLocked"/>).</summary>
    public CompositionSurface CreateContent(int width, int height, int offsetX = 0, int offsetY = 0)
    {
        lock (_tree)
        {
            EnsureInitializedLocked();
            return CreateContentCore(width, height, offsetX, offsetY);
        }
    }

    /// <summary>Atomically replaces the content surface only if <paramref name="current"/> is still
    /// the live one; otherwise returns <paramref name="current"/> untouched.
    ///
    /// A wallpaper change installs a newer renderer while the old player's MediaOpened may still
    /// be in flight. A plain check-then-replace races: the stale handler can pass the currency
    /// check, be preempted by the newer install, then resume and yank the new surface out from
    /// under the live video — freezing that monitor on a wrong-sized frame (seen as a
    /// zoomed/cropped wallpaper that a disable/re-enable heals). The check and the replace must
    /// be one critical section, which is what this method is.</summary>
    internal CompositionSurface ReplaceContentIfCurrent(CompositionSurface current, int width, int height, int offsetX, int offsetY)
    {
        lock (_tree)
        {
            // Volatile read on purpose: IsDisposed takes no lock (see the flag) so there is no
            // tree -> surface-gate ordering to invert against Dispose's gate -> tree path.
            if (!ReferenceEquals(Content, current) || current.VolatileDisposed) return current;
            return CreateContentCore(width, height, offsetX, offsetY);
        }
    }

    /// <summary>Caller holds <see cref="_tree"/>.</summary>
    private CompositionSurface CreateContentCore(int width, int height, int offsetX, int offsetY)
    {
        Content?.Dispose();
        Content = new CompositionSurface(this, width, height, premultipliedAlpha: false, offsetX, offsetY);
        Content.DeviceLost += RaiseDeviceLost;
        _rootVisual.AddVisual(Content.Visual, false, null);

        // Then lift every overlay back above it. A null reference visual does not mean
        // "bottom-most" — it makes the flag a position in the child list — so content added
        // that way lands in front of the widgets and hides them. Verified: the clock vanished.
        // Naming one overlay as the reference would only order content against that one, so
        // each is re-inserted explicitly against the new content.
        foreach (var overlay in _overlays.Values)
        {
            _rootVisual.RemoveVisual(overlay.Visual);
            _rootVisual.AddVisual(overlay.Visual, true, Content.Visual);
        }

        _dcompDevice.Commit();
        return Content;
    }

    public CompositionSurface? GetOverlay(string key)
    {
        lock (_tree) return _overlays.TryGetValue(key, out var surface) ? surface : null;
    }

    /// <summary>Drops the content surface without replacement. A switch to a renderer that
    /// owns no surface (web) must call this first — otherwise the previous renderer's last
    /// frame stays in the tree, opaque and on top, forever hiding the new content.</summary>
    public void ClearContent()
    {
        lock (_tree)
        {
            if (Content is null) return;
            try { Content.Dispose(); } catch { /* tearing down */ }
            Content = null;
            try { _dcompDevice.Commit(); } catch { /* tearing down */ }
        }
    }

    /// <summary>A transparent surface composed above the content (clock and friends). Keyed, so
    /// each widget owns its own visual and creating one does not destroy another's.
    /// First call also builds the GPU objects (see <see cref="EnsureInitializedLocked"/>).</summary>
    public CompositionSurface CreateOverlay(string key, int width, int height, int offsetX, int offsetY)
    {
        lock (_tree)
        {
            EnsureInitializedLocked();
            RemoveOverlayCore(key);
            var surface = new CompositionSurface(this, width, height, premultipliedAlpha: true, offsetX, offsetY);
            surface.DeviceLost += RaiseDeviceLost;
            // Above the content. Order among the overlays themselves is unspecified and does not
            // matter — each widget owns a separate rectangle and they do not intersect.
            _rootVisual.AddVisual(surface.Visual, true, Content?.Visual);
            _overlays[key] = surface;
            _dcompDevice.Commit();
            return surface;
        }
    }

    private void RaiseDeviceLost() => DeviceLost?.Invoke();

    public void RemoveOverlay(string key)
    {
        lock (_tree)
        {
            if (RemoveOverlayCore(key)) _dcompDevice.Commit();
        }
    }

    /// <summary>Caller holds <see cref="_tree"/>. Split out so CreateOverlay can replace an
    /// existing overlay without releasing the lock in between.</summary>
    private bool RemoveOverlayCore(string key)
    {
        if (!_overlays.Remove(key, out var surface)) return false;
        _rootVisual.RemoveVisual(surface.Visual);
        surface.Dispose();
        return true;
    }

    internal (IDXGIFactory2 Dxgi, ID2D1Factory D2d, IDCompositionDevice DComp) Factories =>
        (_dxgiFactory, _d2dFactory, _dcompDevice);

    internal void RemoveVisual(IDCompositionVisual visual)
    {
        lock (_tree) _rootVisual.RemoveVisual(visual);
    }

    /// <summary>Returns this host to the uninitialized state when nothing needs its GPU
    /// objects anymore: drops the target/visual and this host's shared-device reference.
    /// The engine calls this after switching to a renderer that owns no surface (web) —
    /// windows outlive wallpaper changes, so without it the shared device would stay pinned
    /// after the last video/image is gone. A later video/image switch re-initializes lazily.
    ///
    /// No-op when never initialized, or while surfaces still exist (content or widget
    /// overlays): tearing down live visuals here would black out content that still needs
    /// them, so such callers keep the device.</summary>
    public void ReleaseDeviceIfUnused()
    {
        lock (_tree)
        {
            if (_dcompDevice is null) return; // never initialized — nothing held
            if (Content is not null || _overlays.Count != 0) return; // still presenting
            try { _rootVisual?.Dispose(); } catch { /* tearing down */ }
            _rootVisual = null!;
            try { _dcompTarget?.Dispose(); } catch { /* tearing down */ }
            _dcompTarget = null!;
            _dcompDevice = null!;
            _d2dFactory = null!;
            _dxgiFactory = null!;
            Context = null!;
            Device = null!;
            SharedRelease();
        }
        Serilog.Log.Information("Composition: host GPU released (switched to a non-GPU renderer)");
    }

    internal void Commit()
    {
        lock (_tree) _dcompDevice.Commit();
    }

    /// <summary>Runs <paramref name="mutate"/> under the visual-tree lock and commits it.
    ///
    /// The commit is part of this method rather than the caller's business: DirectComposition
    /// batches property changes until Commit, so a mutation without one is invisible, and the
    /// first version of this left SetOffset committing nowhere.</summary>
    internal void MutateTree(Action mutate)
    {
        lock (_tree)
        {
            mutate();
            _dcompDevice.Commit();
        }
    }

    public void Dispose()
    {
        lock (_tree)
        {
            // The _disposed guard also guarantees SharedRelease runs exactly once per
            // successful acquire (previously no guard existed: harmless with per-host
            // resources, fatal once the device became reference-counted).
            if (_disposed) return;
            _disposed = true;
            Content?.Dispose();
            Content = null;
            foreach (var surface in _overlays.Values) surface.Dispose();
            _overlays.Clear();
        }
        // Per-HWND resources only — the device and factories are borrowed from the shared
        // state and released (not disposed) below. All three are null when no surface was
        // ever demanded, in which case there is nothing to release.
        try { _rootVisual?.Dispose(); } catch { /* tearing down */ }
        _rootVisual = null!;
        try { _dcompTarget?.Dispose(); } catch { /* tearing down */ }
        _dcompTarget = null!;
        if (_dcompDevice is not null)
        {
            _dcompDevice = null!;
            SharedRelease();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>One flip-model composition swapchain bound to a DComp visual, with a D2D
/// render target for CPU-bitmap content.</summary>
public sealed class CompositionSurface : IDisposable
{
    private readonly CompositionHost _host;
    private readonly bool _premultiplied;
    private ID2D1RenderTarget? _d2dTarget;

    /// <summary>Serializes Present/draw calls against Dispose. MediaPlayer frame callbacks
    /// arrive on a worker thread while the engine tears the host down on the main thread —
    /// without this, a frame landing mid-teardown called Present on a disposed swapchain
    /// (Vortice reports it as a NULL native pointer → NullReferenceException).</summary>
    private readonly Lock _gate = new();
    private volatile bool _disposed;

    public bool IsDisposed => _disposed;

    /// <summary>Lock-free disposed read for use under the host's tree lock.
    /// IsDisposed takes no lock (see above) so there is no lock-ordering issue.</summary>
    internal bool VolatileDisposed => _disposed;

    public IDXGISwapChain1 SwapChain { get; private set; }
    public ID3D11Texture2D BackBuffer { get; private set; }
    public ID3D11RenderTargetView Rtv { get; private set; }
    public IDCompositionVisual Visual { get; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    internal CompositionSurface(CompositionHost host, int width, int height, bool premultipliedAlpha, int offsetX, int offsetY)
    {
        _host = host;
        _premultiplied = premultipliedAlpha;
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);

        SwapChain = CreateSwapChain();
        BackBuffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
        Rtv = host.Device.CreateRenderTargetView(BackBuffer);

        Visual = host.Factories.DComp.CreateVisual();
        Visual.SetContent(SwapChain);
        Visual.SetOffsetX(offsetX);
        Visual.SetOffsetY(offsetY);
    }

    private IDXGISwapChain1 CreateSwapChain() =>
        _host.Factories.Dxgi.CreateSwapChainForComposition(_host.Device, new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = _premultiplied ? AlphaMode.Premultiplied : AlphaMode.Ignore,
        });

    /// <summary>Both offsets and the commit under one lock, so another widget's visual change
    /// cannot land between them.</summary>
    public void SetOffset(int x, int y) => _host.MutateTree(() =>
    {
        Visual.SetOffsetX(x);
        Visual.SetOffsetY(y);
    });

    public void ClearBlack()
    {
        lock (_gate)
        {
            if (_disposed || Rtv.NativePointer == IntPtr.Zero) return;
            _host.Context.ClearRenderTargetView(Rtv, new Color4(0f, 0f, 0f, 1f));
        }
    }

    public void Present()
    {
        int code;
        lock (_gate)
        {
            if (_disposed || SwapChain.NativePointer == IntPtr.Zero) return;
            try
            {
                code = SwapChain.Present(0, PresentFlags.None).Code;
            }
            catch (ObjectDisposedException) { return; } // lost the race with Dispose — shutting down
            catch (NullReferenceException) { return; } // Vortice NULL native pointer — shutting down
        }
        if (code == Vortice.DXGI.ResultCode.DeviceRemoved.Code ||
            code == Vortice.DXGI.ResultCode.DeviceReset.Code)
        {
            // GPU reset / driver update. The whole host owns the device; rebuilding just this
            // swapchain against the (also dead) device won't help — the engine's re-apply
            // path rebuilds everything. Log loudly.
            Serilog.Log.Warning($"Present reported device loss (0x{code:X8}) — wallpaper re-apply required");
            DeviceLost?.Invoke();
        }
    }

    public event Action? DeviceLost;

    /// <summary>Draws a premultiplied-BGRA bitmap over the whole surface via Direct2D.
    /// (Flip-model backbuffers only accept render operations — CPU copies are ignored.)</summary>
    public void PresentBitmap(System.Drawing.Bitmap bitmap)
    {
        lock (_gate)
        {
            if (_disposed || SwapChain.NativePointer == IntPtr.Zero) return;
            if (_d2dTarget is null)
            {
                using var dxgiSurface = BackBuffer.QueryInterface<IDXGISurface>();
                _d2dTarget = _host.Factories.D2d.CreateDxgiSurfaceRenderTarget(dxgiSurface, new RenderTargetProperties(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
            }

            var bits = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                using var d2dBitmap = _d2dTarget.CreateBitmap(
                    new SizeI(bitmap.Width, bitmap.Height), bits.Scan0, (uint)bits.Stride,
                    new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                _d2dTarget.BeginDraw();
                _d2dTarget.Clear(new Color4(0f, 0f, 0f, 0f));
                _d2dTarget.DrawBitmap(d2dBitmap, new Rect(0, 0, Width, Height), 1f, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
                _d2dTarget.EndDraw();
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
        }
        Present();
    }

    /// <summary>Reads back a region of the current backbuffer into BGRA bytes — the GPU half
    /// of the static desktop-switch snapshot. Call before Present (flip-model backbuffer
    /// contents are undefined afterward).
    ///
    /// Split out of the old encode-and-save-in-one Bs: the PNG encode and file write now run
    /// on the caller's thread outside every lock. Staging readback already stalls the GPU
    /// pipeline; holding the renderer's lock across GDI+ encode + disk IO on top of that
    /// stalled Pause/Resume/Dispose behind the frame callback.</summary>
    /// <returns>The BGRA pixels (<c>actualW * actualH * 4</c> bytes), or null when there is
    /// nothing to read (tearing down, empty region).</returns>
    public byte[]? ReadRegionBytes(int cropX, int cropY, int w, int h, out int actualW, out int actualH)
    {
        lock (_gate)
        {
            actualW = 0;
            actualH = 0;
            if (_disposed || SwapChain.NativePointer == IntPtr.Zero) return null;
            cropX = Math.Clamp(cropX, 0, Math.Max(Width - 1, 0));
            cropY = Math.Clamp(cropY, 0, Math.Max(Height - 1, 0));
            w = Math.Min(w, Width - cropX);
            h = Math.Min(h, Height - cropY);
            if (w <= 0 || h <= 0) return null;

            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            };
            try
            {
                using var staging = _host.Device.CreateTexture2D(desc);
                var box = new Box(cropX, cropY, 0, cropX + w, cropY + h, 1);
                _host.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, BackBuffer, 0, box);

                var map = _host.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    // One file-sized contiguous copy out of the mapped staging texture, then
                    // Unmap immediately so the GPU is never held across managed allocation.
                    var bytes = new byte[w * h * 4];
                    unsafe
                    {
                        fixed (byte* dst = bytes)
                        {
                            byte* src = (byte*)map.DataPointer;
                            byte* row = dst;
                            for (int y = 0; y < h; y++, row += w * 4)
                                Buffer.MemoryCopy(src + (long)y * map.RowPitch, row, w * 4, w * 4);
                        }
                    }
                    actualW = w;
                    actualH = h;
                    return bytes;
                }
                finally
                {
                    _host.Context.Unmap(staging, 0);
                }
            }
            catch (ObjectDisposedException) { return null; } // lost the race with host teardown
            catch (NullReferenceException) { return null; } // Vortice NULL native pointer — shutting down
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Publisher-side release: the host subscribes per surface — drop the invocation
            // list so dead surfaces release the host instead of carrying it.
            DeviceLost = null;
            _d2dTarget?.Dispose();
            _d2dTarget = null;
            try { _host.RemoveVisual(Visual); } catch { /* host may be tearing down */ }
            try { Visual.Dispose(); } catch { }
            try { Rtv.Dispose(); } catch { }
            try { BackBuffer.Dispose(); } catch { }
            try { SwapChain.Dispose(); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
