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

    void Start();

    void Stop();

    /// <summary>Suppresses change notifications (used while the app itself mutates desktop items, so the
    /// rule coordinator doesn't react to our own rename/delete). Resume restores normal behaviour.</summary>
    void Pause();

    /// <summary>Resumes change notifications after a <see cref="Pause"/>.</summary>
    void Resume();
}
