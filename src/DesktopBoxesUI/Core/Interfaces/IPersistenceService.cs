using DesktopBoxesUI.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Loads and saves the desktop layout (the snapshot / "db file"). The snapshot captures the desktop
/// resolution and the set of <see cref="DesktopItemContainer"/>s so the layout can be rescaled when the
/// resolution changes between runs.
/// </summary>
public interface IPersistenceService
{
    Task<DesktopSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sync save for app exit
    /// </summary>
    void SaveSnapshot(DesktopSnapshot snapshot);
    Task SaveSnapshotAsync(DesktopSnapshot snapshot, CancellationToken cancellationToken = default);
}
