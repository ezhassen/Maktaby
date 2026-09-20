namespace DesktopLiveWallPaperEngine;

/// <summary>A file's bytes pinned in memory, shared by every renderer showing it.
/// Held by reference count: the cache never evicts a referenced entry, and renderers
/// release on re-Load/Dispose.</summary>
public sealed class PreloadedMedia
{
    internal PreloadedMedia(string key, string path, byte[] bytes, long length, DateTime lastWriteUtc)
    {
        Key = key;
        Path = path;
        Bytes = bytes;
        Length = length;
        LastWriteUtc = lastWriteUtc;
        LastUsedAt = DateTime.UtcNow;
    }

    public string Key { get; }
    public string Path { get; }
    public byte[] Bytes { get; }
    public long Length { get; }
    public DateTime LastWriteUtc { get; }
    internal DateTime LastUsedAt { get; set; }
    internal int Refs;
}

/// <summary>Process-wide in-memory store for small wallpaper media (videos, animated GIFs).
/// Files at or under the cap are read once and shared by all monitors showing the same source,
/// so looping playback performs zero disk I/O. Larger files bypass the cache (stream from disk).
/// Thread-safe; all file I/O happens off the calling thread.</summary>
public sealed class PreloadedMediaCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PreloadedMedia> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;
    private long _maxBytes;

    public PreloadedMediaCache(long maxBytes) => _maxBytes = Math.Max(0, maxBytes);

    /// <summary>Cap in bytes (live-updatable). Lowering it evicts unreferenced entries past the cap.</summary>
    public long MaxBytes
    {
        get { lock (_gate) return _maxBytes; }
        set
        {
            lock (_gate)
            {
                _maxBytes = Math.Max(0, value);
                EvictForLocked(0);
            }
        }
    }

    public long TotalBytes { get { lock (_gate) return _totalBytes; } }

    public int EntryCount { get { lock (_gate) return _entries.Count; } }

    private static string NormalizeKey(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Synchronous hit check only (no I/O): validated entry with a raised refcount,
    /// or <c>null</c>. Callers that miss use <see cref="AcquireAsync"/>.</summary>
    public PreloadedMedia? TryGet(string path)
    {
        string key = NormalizeKey(path);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && ValidateLocked(entry))
            {
                entry.Refs++;
                entry.LastUsedAt = DateTime.UtcNow;
                return entry;
            }

            return null;
        }
    }

    /// <summary>Hit (refcounted) or read-and-insert. Returns <c>null</c> when the file is missing,
    /// empty, larger than the cap, unreadable, changed mid-read, or nothing evictable fits it.</summary>
    public async Task<PreloadedMedia?> AcquireAsync(string path)
    {
        string key = NormalizeKey(path);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit) && ValidateLocked(hit))
            {
                hit.Refs++;
                hit.LastUsedAt = DateTime.UtcNow;
                return hit;
            }
        }

        long length;
        DateTime writeUtc;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            length = info.Length;
            writeUtc = info.LastWriteTimeUtc;
        }
        catch
        {
            return null;
        }

        long cap;
        lock (_gate) cap = _maxBytes;
        if (length <= 0 || length > cap) return null;

        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => File.ReadAllBytes(path)).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (bytes.LongLength != length) return null; // changed mid-read; caller retries later
        try
        {
            var recheck = new FileInfo(path);
            if (!recheck.Exists || recheck.Length != length || recheck.LastWriteTimeUtc != writeUtc)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        lock (_gate)
        {
            // A concurrent fill may have won while we read.
            if (_entries.TryGetValue(key, out var existing) && ValidateLocked(existing))
            {
                existing.Refs++;
                existing.LastUsedAt = DateTime.UtcNow;
                return existing;
            }

            EvictForLocked(bytes.LongLength);
            if (_totalBytes + bytes.LongLength > _maxBytes) return null;
            var entry = new PreloadedMedia(key, path, bytes, length, writeUtc) { Refs = 1 };
            _entries[key] = entry;
            _totalBytes += bytes.LongLength;
            return entry;
        }
    }

    public void Release(PreloadedMedia media)
    {
        lock (_gate)
        {
            media.Refs = Math.Max(0, media.Refs - 1);
        }
    }

    /// <summary>Bytes currently pinned for <paramref name="path"/> (0 when not preloaded).
    /// No refcount change — for diagnostics.</summary>
    public long PreloadedBytesFor(string path)
    {
        string key = NormalizeKey(path);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && ValidateLocked(entry))
            {
                return entry.Length;
            }

            return 0;
        }
    }

    /// <summary>Snapshot for diagnostics: (path, bytes, refs) per entry.</summary>
    public IReadOnlyList<(string Path, long Bytes, int Refs)> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .Select(e => (e.Path, e.Length, e.Refs))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _totalBytes = 0;
        }
    }

    private bool ValidateLocked(PreloadedMedia entry)
    {
        // Stale (overwritten/deleted on disk) or over a lowered cap with no users: drop.
        if (entry.Refs == 0 && entry.Length > _maxBytes)
        {
            _entries.Remove(entry.Key);
            _totalBytes = Math.Max(0, _totalBytes - entry.Length);
            return false;
        }

        try
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteUtc)
            {
                if (entry.Refs == 0)
                {
                    _entries.Remove(entry.Key);
                    _totalBytes = Math.Max(0, _totalBytes - entry.Length);
                }

                return false;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    /// <summary>Evicts least-recently-used unreferenced entries until <paramref name="need"/>
    /// fits. Caller holds <see cref="_gate"/>.</summary>
    private void EvictForLocked(long need)
    {
        if (_totalBytes + need <= _maxBytes) return;
        foreach (var victim in _entries.Values
                     .Where(e => e.Refs <= 0)
                     .OrderBy(e => e.LastUsedAt)
                     .ToList())
        {
            _entries.Remove(victim.Key);
            _totalBytes = Math.Max(0, _totalBytes - victim.Length);
            if (_totalBytes + need <= _maxBytes) break;
        }
    }
}
