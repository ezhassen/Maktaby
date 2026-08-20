using System.Threading;
using System.Threading.Tasks;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Loads and saves the desktop layout (the snapshot / "db file"). The snapshot captures the desktop
/// resolution and the set of <see cref="DesktopItemContainer"/>s so the layout can be rescaled when the
/// resolution changes between runs.
/// </summary>
public interface IPersistenceService
{
    Task<DesktopSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default);

    Task SaveSnapshotAsync(DesktopSnapshot snapshot, CancellationToken cancellationToken = default);
}
