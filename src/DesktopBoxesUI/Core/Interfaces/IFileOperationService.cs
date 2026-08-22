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
    bool Delete(string path, bool permanent);
}
