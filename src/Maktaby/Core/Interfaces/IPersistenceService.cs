using Maktaby.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Loads and saves the desktop layout (the snapshot / "db file"). The snapshot captures the desktop
/// resolution and the set of <see cref="DesktopItemContainer"/>s so the layout can be rescaled when the
/// resolution changes between runs.
/// </summary>
public interface IPersistenceService
{
    /// <summary>The on-disk path of the live snapshot file.</summary>
    string SnapshotFilePath { get; }

    Task<DesktopSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a snapshot from an arbitrary file (used by Settings "Restore" from a backup).</summary>
    Task<DesktopSnapshot?> LoadFromFileAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>
    /// Delete the snapshot file
    /// </summary>
    void DeleteSnapshotFile();
    /// <summary>
    /// Sync save for app exit
    /// </summary>
    void SaveSnapshot(DesktopSnapshot snapshot);
    Task SaveSnapshotAsync(DesktopSnapshot snapshot, CancellationToken cancellationToken = default);
}
