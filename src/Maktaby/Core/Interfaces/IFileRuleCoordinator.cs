using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Drives the file-system → rule → box pipeline. Started once by <see cref="DesktopManager"/> after
/// the containers and rules are loaded. Also performs user-initiated item rename/delete (pausing the
/// underlying shell watcher so it doesn't react to the app's own mutation).
/// </summary>
public interface IFileRuleCoordinator
{
    void Start();

    void Stop();

    /// <summary>Renames the item on disk (if it has a path) and updates its model. Returns false if the
    /// operation was cancelled or failed.</summary>
    System.Threading.Tasks.Task<bool> RenameItemAsync(BoxItem item, string newName);

    /// <summary>Deletes the item (Recycle Bin unless <paramref name="permanent"/>), updating the model.
    /// Returns false if the operation was cancelled or failed.</summary>
    System.Threading.Tasks.Task<bool> DeleteItemAsync(BoxItem item, bool permanent);

    /// <summary>Deletes the items (Recycle Bin unless <paramref name="permanent"/>), updating the model.
    /// Returns false if the operation was cancelled or failed.</summary>
    System.Threading.Tasks.Task<bool> DeleteItemsAsync(IEnumerable<BoxItem> items, bool permanent);
}
