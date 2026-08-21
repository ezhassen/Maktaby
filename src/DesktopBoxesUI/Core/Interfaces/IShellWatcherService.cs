using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

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
}
