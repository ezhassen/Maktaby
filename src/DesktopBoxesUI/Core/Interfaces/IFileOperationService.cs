namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Performs shell file operations (rename / delete) through Explorer, so behaviour matches the desktop
/// (undo support, native confirmation dialogs). Platform-agnostic contract; the concrete implementation
/// lives in the Win32 layer.
/// </summary>
public interface IFileOperationService
{
    /// <summary>Renames the item at <paramref name="path"/> to <paramref name="newName"/> in the same
    /// directory. Returns false if the user cancelled or the operation failed.</summary>
    bool Rename(string path, string newName);

    /// <summary>Deletes the item at <paramref name="path"/>. When <paramref name="permanent"/> is false the
    /// item is moved to the Recycle Bin silently; when true the native Explorer confirmation is shown and the
    /// item is deleted permanently. Returns false if cancelled or failed.</summary>
    [Obsolete("Use DeleteAsync instead", error: true)]
    bool Delete(string path, bool permanent);

    /// <summary>Copies the item at <paramref name="source"/> to <paramref name="destination"/> (file or
    /// directory), matching Explorer behaviour. Returns false if cancelled or failed.</summary>
    [Obsolete("Use CopyAsync instead", error: true)]
    bool Copy(string source, string destination);

    /// <summary>Deletes multiple items at <paramref name="paths"/>. Uses a single shell operation so the
    /// progress dialog and undo are batched. Returns false if cancelled or failed.</summary>
    Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken cancellationToken = default);

    /// <summary>Copies multiple items from <paramref name="sources"/> to <paramref name="destinations"/>
    /// (one-to-one mapping). Uses a single shell operation. Returns false if cancelled or failed.</summary>
    Task<bool> CopyAsync(IReadOnlyList<string> sources, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default);
}
