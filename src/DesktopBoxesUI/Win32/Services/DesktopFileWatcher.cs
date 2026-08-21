using System;
using System.IO;
using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Watches the real desktop folder (the user's Desktop directory) with a <see cref="FileSystemWatcher"/>
/// and forwards create/delete/rename events as full paths. Virtual shell items (This PC, Recycle Bin)
/// have no filesystem path and are therefore not observed here — they are only seeded at startup.
/// Degrades silently (no watching) if the desktop path is unavailable.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopFileWatcher : IFileWatcherService, IDisposable
{
    private FileSystemWatcher? _watcher;

    public event Action<string>? FileCreated;

    public event Action<string>? FileDeleted;

    public void Start()
    {
        if (_watcher != null)
        {
            return;
        }

        string? desktop = null;
        try
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        catch
        {
            desktop = null;
        }

        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(desktop)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            watcher.Created += (_, e) => FileCreated?.Invoke(e.FullPath);
            watcher.Deleted += (_, e) => FileDeleted?.Invoke(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                FileDeleted?.Invoke(e.OldFullPath);
                FileCreated?.Invoke(e.FullPath);
            };

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            _watcher = null;
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose() => Stop();
}
