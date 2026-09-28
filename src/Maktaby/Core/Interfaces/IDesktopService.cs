using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Discovers the items currently living on the Windows desktop so they can be
/// grouped into <see cref="Box"/>es. Implemented using Shell/Explorer integration.
/// </summary>
public interface IDesktopService
{
    /// <summary>Enumerates the desktop items asynchronously (eventually event-driven).</summary>
    IAsyncEnumerable<BoxItem> GetDesktopItemsAsync(System.Threading.CancellationToken cancellationToken = default);
}
