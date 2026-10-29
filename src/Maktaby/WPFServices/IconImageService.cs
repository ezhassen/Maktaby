using Maktaby.Core.Interfaces;
using Maktaby.Win32.NativeMethods;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Maktaby.Native;

namespace Maktaby.WPFServices;

/// <summary>
/// Converts shell HICON handles (returned by <see cref="IShellIconService"/>) into WPF
/// ImageSources, caching the result so the underlying GDI handle is extracted and destroyed
/// exactly once. Lives in the UI layer (not Core), so it is allowed to touch WPF and the native
/// wrapper. Keeps the icon GDI handle lifetime short to avoid exhausting GDI objects.
/// Caches are LRU-bounded (<see cref="MaxCacheEntries"/> each) so a long session that touches
/// many distinct files cannot grow memory without limit.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class IconImageService
{
    /// <summary>
    /// Memory ceiling for the path-keyed icon cache.
    /// </summary>
    /// <remarks>
    /// An icon is a frozen ARGB bitmap, and its cost scales with the size it was rendered at:
    /// 4 KB at 32x32, 9 KB at 48x48, 36 KB at 96x96, 64 KB at 128x128. A budget that only suits
    /// the small sizes would thrash the moment the user picks a large icon size - every icon
    /// evicted, then re-resolved, again and again. 16 MB covers the large sizes comfortably
    /// (~440 icons at 96 px) while still capping what a long browsing session can accumulate; the
    /// entry count stays as the secondary bound, so small icons cannot grow without limit either.
    /// </remarks>
    private const long MaxCacheBytes = 16L * 1024 * 1024;

    /// <summary>Secondary bound, so a cache of tiny icons still cannot grow without limit.</summary>
    private const int MaxCacheEntries = 1000;

    /// <summary>
    /// The size the shell's own icon path produces (32 px, or 48 px at 150% DPI). Anything above
    /// that has to come from the scalable factory, or it is an upscale and looks soft.
    /// </summary>
    private const int ShellNativeIconSize = 48;

    /// <summary>The size used when the caller does not care (the small tiles).</summary>
    private const int DefaultIconSize = 32;

    /// <summary>
    /// Normalises a requested pixel size to one the shell can actually produce.
    /// </summary>
    /// <remarks>
    /// The shell's own icons come in 16/32/48 steps, so a request inside that range is snapped to
    /// the step that answers it - asking for 40 px and getting 32 would be an upscale again, and
    /// snapping also keeps the cache keys stable. Above 48 the scalable factory renders any size,
    /// so the request is used as it is.
    /// </remarks>
    private static int NormalizeSize(int pixelSize)
    {
        int edge = Math.Clamp(pixelSize, 16, 256);
        if (edge <= 48)
        {
            foreach (int step in new[] { 16, 24, 32, 48 })
            {
                if (edge <= step)
                {
                    return step;
                }
            }

            return 48;
        }

        return edge;
    }

    /// <summary>
    /// The physical pixel size a display size of <paramref name="dips"/> needs on this machine.
    /// </summary>
    /// <remarks>
    /// The icon has to be as many pixels as the slot is physically wide, or WPF upscales it and the
    /// large icon sizes look soft. The conversion lives here so every caller speaks in display units
    /// and only this one knows about pixels.
    /// </remarks>
    private static int PhysicalSize(int dips)
    {
        double scale = 1.0;
        try
        {
            uint dpi = Maktaby.Native.User32.GetDpiForSystem();
            if (dpi > 0)
            {
                scale = dpi / 96.0;
            }
        }
        catch { }

        return (int)Math.Round(dips * scale);
    }

    /// <summary>
    /// Memory ceiling for the PIDL-keyed cache. It only ever holds *virtual* items (This PC, the
    /// Recycle Bin, Store apps) — a handful — so it gets a small budget of its own.
    /// </summary>
    private const long MaxPidlCacheBytes = 512L * 1024;

    /// <summary>Secondary bound for the PIDL cache.</summary>
    private const int MaxPidlCacheEntries = 64;

    /// <summary>
    /// How many shell icon extractions may be in flight at once.
    /// </summary>
    /// <remarks>
    /// Every tracked item that gets realized issues one shell call, and a desktop with 100+
    /// shortcuts issues them all at once: the Icons view uses a plain <c>WrapPanel</c> (the
    /// virtualizing panel is not in use), so a shown box realizes every one of its items. Unbounded,
    /// that floods the thread pool — starving the startup work — and hammers the shell, which is a
    /// single shared COM resource (Explorer lives in it too). The waiters are released in arrival
    /// order, so the box that is shown first still gets its icons first.
    /// </remarks>
    private const int MaxConcurrentLoads = 8;

    /// <summary>How long one icon load may take before it is abandoned.</summary>
    /// <remarks>
    /// The shell call is synchronous COM: <c>SHGetFileInfo</c> against a path the shell cannot
    /// answer (an unreachable network share, a wedged Explorer) never returns and cannot be
    /// cancelled. Without a deadline the caller waits forever and the item never gets an icon.
    /// </remarks>
    private static readonly TimeSpan IconLoadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many abandoned loads may still be outstanding. Past this the shell is treated as
    /// unresponsive and no further calls are made.
    /// </summary>
    /// <remarks>
    /// An abandoned call keeps its thread-pool thread for as long as the shell stays wedged, so
    /// without a ceiling every new request would add another thread that never comes back.
    /// </remarks>
    private const int MaxAbandonedOutstandings = 8;

    /// <summary>Abandoned loads whose shell call has not returned yet.</summary>
    private int _abandonedOutstandings;

    private readonly SemaphoreSlim _loadGate = new(MaxConcurrentLoads, MaxConcurrentLoads);

    /// <summary>Icon loads issued since startup - the fan-out that the gate now bounds.</summary>
    private int _issuedLoads;

    /// <summary>Icon loads that finished (successfully or not) since startup.</summary>
    private int _completedLoads;

    /// <summary>Number of icon loads issued since startup.</summary>
    public int IssuedLoads => Volatile.Read(ref _issuedLoads);

    /// <summary>Number of icon loads finished since startup.</summary>
    public int CompletedLoads => Volatile.Read(ref _completedLoads);

    /// <summary>
    /// Raised when no icon load is in flight any more - i.e. the desktop has stopped asking the
    /// shell for icons. Fires again if a later box (or a later refresh) starts more.
    /// </summary>
    public event Action? LoadsSettled;

    private readonly IShellIconService _shellIcon;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<LruEntry>> _cacheIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LruEntry> _cacheOrder = new();
    private long _cacheBytes;

    private readonly Dictionary<string, LinkedListNode<LruEntry>> _pidlIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LruEntry> _pidlOrder = new();
    private long _pidlBytes;

    private sealed record LruEntry(string Key, ImageSource? Value, long Bytes, int Size);

    public IconImageService(IShellIconService shellIcon) => _shellIcon = shellIcon;

    private bool TryGet(Dictionary<string, LinkedListNode<LruEntry>> index, LinkedList<LruEntry> order, string key, int size, out ImageSource? value)
    {
        lock (_cacheLock)
        {
            if (index.TryGetValue(key, out var node))
            {
                // A cached icon at the wrong resolution is a blurry icon: the shell only ever hands
                // back 32 or 48 px, so a tile displayed at 96 was an upscale. Treat a size mismatch
                // as a miss and re-resolve at the size that was asked for.
                if (node.Value.Size != size)
                {
                    value = null;
                    return false;
                }

                // Refresh recency; the frozen ImageSource itself is immutable and shareable.
                order.Remove(node);
                order.AddLast(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Inserts into an LRU and evicts until the cache is inside both of its bounds.
    /// </summary>
    /// <remarks>
    /// The byte budget is the real constraint: an icon is a frozen ARGB bitmap, so a 48x48 one
    /// (the large icon at 150% DPI) costs 9 KB while a 32x32 costs 4 KB - an entry count is only a
    /// proxy for memory. The entry count stays as a secondary bound so a cache of tiny icons cannot
    /// grow without limit either.
    /// </remarks>
    private void Add(
        Dictionary<string, LinkedListNode<LruEntry>> index,
        LinkedList<LruEntry> order,
        ref long totalBytes,
        long maxBytes,
        int maxEntries,
        string key,
        ImageSource value,
        int size)
    {
        lock (_cacheLock)
        {
            if (index.TryGetValue(key, out var existing))
            {
                // Replaced rather than duplicated: one entry per path, at the resolution last
                // asked for. Two resolutions of the same icon would double the memory for no gain.
                order.Remove(existing);
                totalBytes -= existing.Value.Bytes;
                index.Remove(key);
            }

            long bytes = EstimateBytes(value);
            var node = order.AddLast(new LruEntry(key, value, bytes, size));
            index[key] = node;
            totalBytes += bytes;

            while (order.First != null &&
                   (totalBytes > maxBytes || index.Count > maxEntries))
            {
                var oldest = order.First;
                order.RemoveFirst();
                index.Remove(oldest.Value.Key);
                totalBytes -= oldest.Value.Bytes;
            }
        }
    }

    /// <summary>
    /// Approximate managed bytes held by the two icon caches, and their entry counts.
    /// </summary>
    /// <remarks>
    /// An icon is a frozen ARGB bitmap, so its cost is pixels x 4: a 32x32 shell icon is 4 KB, a
    /// 48x48 one (the large icon at 150% DPI) is 9 KB. The LRU is bounded by entry count, which
    /// is a proxy for memory rather than a budget - this is the real number.
    /// </remarks>
    public (long PathBytes, int PathEntries, long PidlBytes, int PidlEntries) CacheStats()
    {
        lock (_cacheLock)
        {
            return (_cacheBytes, _cacheIndex.Count, _pidlBytes, _pidlIndex.Count);
        }
    }

    private static long EstimateBytes(ImageSource? source)
    {
        if (source is not BitmapSource bitmap)
        {
            return 0;
        }

        // 32 bpp ARGB, plus a small constant for the object itself.
        return (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 + 128;
    }

    public async Task<ImageSource?> GetIconAsync(string path, CancellationToken cancellationToken = default) =>
        await GetIconAsync(path, DefaultIconSize, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Loads the icon for a path at a display size.
    /// </summary>
    /// <param name="size">The size the icon is displayed at, in device-independent pixels. The
    /// physical pixel count is derived from the system DPI, because the icon has to be as many
    /// pixels as the slot is physically wide or WPF upscales it — that upscale is what made the
    /// large icon sizes look soft.</param>
    public async Task<ImageSource?> GetIconAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        int edge = NormalizeSize(PhysicalSize(size));
        if (TryGet(_cacheIndex, _cacheOrder, path, edge, out var cached))
        {
            return cached;
        }

        if (Volatile.Read(ref _abandonedOutstandings) >= MaxAbandonedOutstandings)
        {
            // The shell has stopped answering. Each abandoned call is still stuck in a
            // non-cancellable COM call, so piling more onto it would only burn thread-pool threads
            // that never come back. A missing icon is the better failure.
            Serilog.Log.Warning("Skipping icon load for {Path}: {Stuck} previous loads are still stuck", path, _abandonedOutstandings);
            return null;
        }

        // One shell call at a time per slot, so a desktop full of shortcuts cannot flood the
        // thread pool and the shell. Waiters are served in arrival order, which keeps the
        // first-shown box's icons at the head of the queue.
        Interlocked.Increment(ref _issuedLoads);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The shell call is synchronous COM and cannot be cancelled, so a path the shell
            // cannot answer (an unreachable network share, a wedged Explorer) would hang the
            // caller forever. Race it against a deadline instead and walk away: the abandoned
            // call keeps its thread, but the slot is freed and the caller gets no icon.
            using var timeoutCts = new CancellationTokenSource();
            var load = LoadIconCoreAsync(path, edge, cancellationToken);
            var finished = await Task.WhenAny(load, Task.Delay(IconLoadTimeout, timeoutCts.Token)).ConfigureAwait(false);
            if (finished != load)
            {
                Interlocked.Increment(ref _abandonedOutstandings);
                _ = load.ContinueWith(t =>
                {
                    // Observe the outcome so an abandoned failure is never an unobserved exception,
                    // and drop the stuck count once the call finally gives up.
                    _ = t.Exception;
                    Interlocked.Decrement(ref _abandonedOutstandings);
                }, TaskScheduler.Default);

                Serilog.Log.Warning("Icon load for {Path} exceeded {Seconds}s and was abandoned", path, IconLoadTimeout.TotalSeconds);
                return null;
            }

            // The load won the race: release the deadline timer instead of leaving it pending.
            timeoutCts.Cancel();
            return await load.ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
            int completed = Interlocked.Increment(ref _completedLoads);
            if (completed >= Volatile.Read(ref _issuedLoads))
            {
                // Nothing in flight: the desktop has stopped asking the shell for icons.
                try { LoadsSettled?.Invoke(); } catch { }
            }
        }
    }

    /// <summary>
    /// The actual extraction: shell call, GDI handle to bitmap, cache.
    /// </summary>
    private async Task<ImageSource?> LoadIconCoreAsync(string path, int size, CancellationToken cancellationToken)
    {
        // Above the shell's own icon sizes, the scalable factory renders the icon at the size we
        // ask for. SHGetFileInfo only ever hands back 32/48 px, and WPF upscaling those is exactly
        // the blur the large tiles showed.
        if (size > ShellNativeIconSize)
        {
            var scaled = await LoadScaledIconAsync(path, size, cancellationToken).ConfigureAwait(false);
            if (scaled is not null)
            {
                Add(_cacheIndex, _cacheOrder, ref _cacheBytes, MaxCacheBytes, MaxCacheEntries, path, scaled, size);
                return scaled;
            }

            // The factory could not render it (a broken link, a virtual item): fall through to the
            // shell's own icon, which is small but always present.
        }

        object? token = await _shellIcon.GetIconAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);

        ImageSource? source = null;
        await Task.Run(() =>
        {
            if (token is IntPtr hicon && hicon != IntPtr.Zero)
            {
                try
                {
                    source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }
                finally
                {
                    Win32Apis.DestroyIcon(hicon);
                }

                return;
            }

            // SHGetFileInfo couldn't resolve a PIDL-only link (e.g. a .lnk we created for a Start Menu app).
            // IShellItemImageFactory follows the link and returns a proper alpha icon for the target.
            IntPtr hbitmap = Win32Apis.GetIconBitmapForPath(path, size);
            if (hbitmap != IntPtr.Zero)
            {
                try
                {
                    source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, new Int32Rect(0, 0, 0, 0), BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }
                finally
                {
                    Win32Apis.DeleteObject(hbitmap);
                }
            }
        }, cancellationToken);

        // Only cache successes: a transient failure (shell busy, overlay handler glitch, startup race)
        // must stay retryable instead of being frozen as a permanent blank icon.
        if (source is not null)
        {
            Add(_cacheIndex, _cacheOrder, ref _cacheBytes, MaxCacheBytes, MaxCacheEntries, path, source, size);
        }

        return source;
    }

    /// <summary>Renders the icon through <c>IShellItemImageFactory</c> at an exact pixel size.</summary>
    private static async Task<ImageSource?> LoadScaledIconAsync(string path, int size, CancellationToken cancellationToken)
    {
        ImageSource? source = null;
        await Task.Run(() =>
        {
            IntPtr hbitmap = Win32Apis.GetIconBitmapForPath(path, size);
            if (hbitmap == IntPtr.Zero)
            {
                return;
            }

            try
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, new Int32Rect(0, 0, 0, 0), BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            }
            finally
            {
                Win32Apis.DeleteObject(hbitmap);
            }
        }, cancellationToken);

        return source;
    }

    /// <summary>
    /// Drops the cached icon for a shell PIDL so the next request re-reads it from the shell.
    /// </summary>
    /// <remarks>
    /// Needed for the Recycle Bin, whose icon is derived from its contents and therefore changes
    /// without the PIDL - or anything about the item - changing. Every other icon is stable for the
    /// life of the path, which is exactly why the cache is safe for them.
    /// </remarks>
    public void InvalidatePidl(string pidlBase64)
    {
        if (string.IsNullOrEmpty(pidlBase64)) { return; }

        lock (_cacheLock)
        {
            if (_pidlIndex.Remove(pidlBase64, out var node))
            {
                _pidlOrder.Remove(node);
                _pidlBytes -= node.Value.Bytes;
            }
        }
    }

    /// <summary>Drops the cached icon for a path, for the same reason as <see cref="InvalidatePidl"/>.</summary>
    public void InvalidatePath(string path)
    {
        if (string.IsNullOrEmpty(path)) { return; }

        lock (_cacheLock)
        {
            if (_cacheIndex.Remove(path, out var node))
            {
                _cacheOrder.Remove(node);
                _cacheBytes -= node.Value.Bytes;
            }
        }
    }

    /// <summary>Loads an icon for a stored shell PIDL (virtual items such as UWP/Store apps). When
    /// <paramref name="filePath"/> is supplied (a desktop <c>.lnk</c> we created for the item) it is used as
    /// an additional resolution source so the icon follows PIDL-only link targets.</summary>
    public async Task<ImageSource?> GetIconFromPidlAsync(string pidlBase64, string? filePath = null, CancellationToken cancellationToken = default) =>
        await GetIconFromPidlAsync(pidlBase64, filePath, DefaultIconSize, cancellationToken).ConfigureAwait(false);

    /// <summary>Loads the icon for a stored shell PIDL at a requested pixel size.</summary>
    public async Task<ImageSource?> GetIconFromPidlAsync(string pidlBase64, string? filePath, int size, CancellationToken cancellationToken = default)
    {
        int edge = NormalizeSize(PhysicalSize(size));
        if (TryGet(_pidlIndex, _pidlOrder, pidlBase64, edge, out var cached))
        {
            return cached;
        }

        if (Volatile.Read(ref _abandonedOutstandings) >= MaxAbandonedOutstandings)
        {
            Serilog.Log.Warning("Skipping PIDL icon load: {Stuck} previous loads are still stuck", _abandonedOutstandings);
            return null;
        }

        // Same bound and same deadline as the path-based load: this path also ends in a synchronous
        // COM call to the shell, so it can hang exactly the same way.
        Interlocked.Increment(ref _issuedLoads);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = new CancellationTokenSource();
            var load = LoadIconFromPidlCoreAsync(pidlBase64, filePath, edge, cancellationToken);
            var finished = await Task.WhenAny(load, Task.Delay(IconLoadTimeout, timeoutCts.Token)).ConfigureAwait(false);
            if (finished != load)
            {
                Interlocked.Increment(ref _abandonedOutstandings);
                _ = load.ContinueWith(t =>
                {
                    _ = t.Exception;
                    Interlocked.Decrement(ref _abandonedOutstandings);
                }, TaskScheduler.Default);

                Serilog.Log.Warning("PIDL icon load exceeded {Seconds}s and was abandoned", IconLoadTimeout.TotalSeconds);
                return null;
            }

            timeoutCts.Cancel();
            return await load.ConfigureAwait(false);
        }
        finally
        {
            _loadGate.Release();
            int completed = Interlocked.Increment(ref _completedLoads);
            if (completed >= Volatile.Read(ref _issuedLoads))
            {
                try { LoadsSettled?.Invoke(); } catch { }
            }
        }
    }

    private async Task<ImageSource?> LoadIconFromPidlCoreAsync(string pidlBase64, string? filePath, int size, CancellationToken cancellationToken)
    {
        ImageSource? source = null;
        await Task.Run(() =>
        {
            // Preferred: IShellItemImageFactory yields a proper alpha icon for any shell item (incl. virtual
            // Start Menu apps). Falls back to SHGetFileInfo for compatibility.
            IntPtr hbitmap = Win32Apis.GetIconBitmapForPidl(pidlBase64);
            if (hbitmap != IntPtr.Zero)
            {
                try
                {
                    source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }
                finally
                {
                    Win32Apis.DeleteObject(hbitmap);
                }

                return;
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                IntPtr hbmpPath = Win32Apis.GetIconBitmapForPath(filePath, size);
                if (hbmpPath != IntPtr.Zero)
                {
                    try
                    {
                        source = Imaging.CreateBitmapSourceFromHBitmap(hbmpPath, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                    }
                    finally
                    {
                        Win32Apis.DeleteObject(hbmpPath);
                    }

                    return;
                }
            }

            IntPtr hicon = Win32Apis.GetIconForPidl(pidlBase64);
            if (hicon != IntPtr.Zero)
            {
                try
                {
                    source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }
                finally
                {
                    Win32Apis.DestroyIcon(hicon);
                }
            }
        }, cancellationToken);


        // Successes only — see GetIconAsync.
        if (source is not null)
        {
            Add(_pidlIndex, _pidlOrder, ref _pidlBytes, MaxPidlCacheBytes, MaxPidlCacheEntries, pidlBase64, source, size);
        }

        return source;
    }
}