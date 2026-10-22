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
    private const int MaxCacheEntries = 1000;

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
    private readonly Dictionary<string, LinkedListNode<LruEntry>> _pidlIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LruEntry> _pidlOrder = new();

    private sealed record LruEntry(string Key, ImageSource? Value);

    public IconImageService(IShellIconService shellIcon) => _shellIcon = shellIcon;

    private bool TryGet(Dictionary<string, LinkedListNode<LruEntry>> index, LinkedList<LruEntry> order, string key, out ImageSource? value)
    {
        lock (_cacheLock)
        {
            if (index.TryGetValue(key, out var node))
            {
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

    private void Add(Dictionary<string, LinkedListNode<LruEntry>> index, LinkedList<LruEntry> order, string key, ImageSource value)
    {
        lock (_cacheLock)
        {
            if (index.TryGetValue(key, out var existing))
            {
                order.Remove(existing);
                order.AddLast(existing);
                return;
            }

            var node = order.AddLast(new LruEntry(key, value));
            index[key] = node;
            while (index.Count > MaxCacheEntries && order.First != null)
            {
                var oldest = order.First;
                order.RemoveFirst();
                index.Remove(oldest.Value.Key);
            }
        }
    }

    public async Task<ImageSource?> GetIconAsync(string path, CancellationToken cancellationToken = default)
    {
        if (TryGet(_cacheIndex, _cacheOrder, path, out var cached))
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
            var load = LoadIconCoreAsync(path, cancellationToken);
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
    private async Task<ImageSource?> LoadIconCoreAsync(string path, CancellationToken cancellationToken)
    {
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
            IntPtr hbitmap = Win32Apis.GetIconBitmapForPath(path);
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
            Add(_cacheIndex, _cacheOrder, path, source);
        }

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
            }
        }
    }

    /// <summary>Loads an icon for a stored shell PIDL (virtual items such as UWP/Store apps). When
    /// <paramref name="filePath"/> is supplied (a desktop <c>.lnk</c> we created for the item) it is used as
    /// an additional resolution source so the icon follows PIDL-only link targets.</summary>
    public async Task<ImageSource?> GetIconFromPidlAsync(string pidlBase64, string? filePath = null, CancellationToken cancellationToken = default)
    {
        if (TryGet(_pidlIndex, _pidlOrder, pidlBase64, out var cached))
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
            var load = LoadIconFromPidlCoreAsync(pidlBase64, filePath, cancellationToken);
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

    private async Task<ImageSource?> LoadIconFromPidlCoreAsync(string pidlBase64, string? filePath, CancellationToken cancellationToken)
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
                IntPtr hbmpPath = Win32Apis.GetIconBitmapForPath(filePath);
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
            Add(_pidlIndex, _pidlOrder, pidlBase64, source);
        }

        return source;
    }
}