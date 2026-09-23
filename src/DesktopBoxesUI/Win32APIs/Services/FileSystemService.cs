using System.IO;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Win32APIs.Services;

/// <summary>Filesystem implementation of <see cref="IFileSystemService"/> via <see cref="FileSystemWatcher"/>.</summary>
public sealed class FileSystemService : IFileSystemService
{
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public IEnumerable<string> Enumerate(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var e in Directory.EnumerateFileSystemEntries(directory))
            yield return e;
    }

    public IDisposable Watch(string path, Action<FileSystemChange> onChange)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new NoopDisposable();

        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            // 8 KB default overflows on post-resume floods (AV/indexer catch-up), silently
            // dropping events. 64 KB + an overflow signal below keeps the delta stream trusted.
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler created = (_, e) => onChange(new FileSystemChange(FileSystemChangeKind.Created, e.FullPath));
        FileSystemEventHandler deleted = (_, e) => onChange(new FileSystemChange(FileSystemChangeKind.Deleted, e.FullPath));
        FileSystemEventHandler changed = (_, e) => onChange(new FileSystemChange(FileSystemChangeKind.Changed, e.FullPath));
        RenamedEventHandler renamed = (_, e) => onChange(new FileSystemChange(FileSystemChangeKind.Renamed, e.FullPath, e.OldFullPath));
        ErrorEventHandler error = (_, e) =>
        {
            try { Serilog.Log.Warning(e.GetException(), "FileSystemWatcher overflow for {Path} — reconciling", path); } catch { }
            // Events were lost: Changed on the watched root reconciles (contents drain falls
            // back to a full refresh for unknown paths; root/desktop paths re-read).
            try { onChange(new FileSystemChange(FileSystemChangeKind.Changed, path)); } catch { }
        };

        watcher.Created += created;
        watcher.Deleted += deleted;
        watcher.Changed += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;

        return new WatcherDisposable(watcher, created, deleted, changed, renamed, error);
    }

    private sealed class WatcherDisposable : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly FileSystemEventHandler _created, _deleted, _changed;
        private readonly RenamedEventHandler _renamed;
        private readonly ErrorEventHandler _error;
        private bool _disposed;

        public WatcherDisposable(FileSystemWatcher watcher, FileSystemEventHandler c, FileSystemEventHandler d, FileSystemEventHandler ch, RenamedEventHandler r, ErrorEventHandler e)
        {
            _watcher = watcher;
            _created = c; _deleted = d; _changed = ch; _renamed = r; _error = e;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _watcher.Created -= _created; } catch { }
            try { _watcher.Deleted -= _deleted; } catch { }
            try { _watcher.Changed -= _changed; } catch { }
            try { _watcher.Renamed -= _renamed; } catch { }
            try { _watcher.Error -= _error; } catch { }
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
        }
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
