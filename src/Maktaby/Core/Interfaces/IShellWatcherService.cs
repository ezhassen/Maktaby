using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Watches the desktop Shell namespace for changes to ALL items — both real filesystem files and
/// virtual shell items (This PC, Recycle Bin, …) — via Shell change notifications. Raises resolved
/// <see cref="BoxItem"/>s so the rule coordinator can add/remove them. The concrete implementation is
/// platform-specific (Shell).
/// </summary>
public interface IShellWatcherService
{
    /// <summary>An item appeared (created or renamed into) on the desktop.</summary>
    event System.Action<BoxItem>? ItemCreated;

    /// <summary>An item disappeared (deleted or renamed out of) the desktop.</summary>
    event System.Action<BoxItem>? ItemDeleted;

    /// <summary>
    /// An item that already exists changed in the shell - its icon, or the state its icon encodes.
    /// </summary>
    /// <remarks>
    /// The Recycle Bin is the case that matters: its icon is recomputed from the bin's contents, so
    /// it changes while the PIDL, the path and the name all stay identical. There is no way to spot
    /// that from the item alone, so the shell says it happened and the item identifies what changed.
    /// Matching is by <see cref="BoxItem.RefersToSame"/> against the affected entries.
    /// </remarks>
    event System.Action<BoxItem>? ItemUpdated;

    void Start();

    void Stop();

    /// <summary>Suppresses change notifications (used while the app itself mutates desktop items, so the
    /// rule coordinator doesn't react to our own rename/delete). Resume restores normal behaviour.</summary>
    void Pause();

    /// <summary>Resumes change notifications after a <see cref="Pause"/>.</summary>
    void Resume();

    /// <summary>
    /// Announces that this app's own delete changed the Recycle Bin's appearance.
    /// </summary>
    /// <remarks>
    /// The shell's bin notification covers external changes; this covers ours, which can arrive while
    /// the watcher is suppressing other notifications. Delivered as an <see cref="ItemUpdated"/> for
    /// the bin itself, so there is a single consumer path either way.
    /// </remarks>
    void NotifyRecycleBinUpdated();
}
