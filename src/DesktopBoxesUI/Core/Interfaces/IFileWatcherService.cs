namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Watches the Windows desktop folder for file-system changes so desktop items can be auto-added to
/// (or removed from) boxes. The concrete implementation is platform-specific (Win32).
/// </summary>
public interface IFileWatcherService
{
    /// <summary>Raised with the full path of a file/folder created (or renamed into) on the desktop.</summary>
    event System.Action<string>? FileCreated;

    /// <summary>Raised with the full path of a file/folder deleted (or renamed out of) the desktop.</summary>
    event System.Action<string>? FileDeleted;

    void Start();

    void Stop();
}
