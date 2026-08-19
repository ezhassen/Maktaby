using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Loads and saves the set of <see cref="Box"/>es (the snapshot / "db file"). Implementations
/// decide the storage format (JSON file, etc.) without the rest of the app knowing.
/// </summary>
public interface IPersistenceService
{
    Task<IReadOnlyList<Box>?> LoadBoxesAsync(CancellationToken cancellationToken = default);

    Task SaveBoxesAsync(IEnumerable<Box> boxes, CancellationToken cancellationToken = default);
}
