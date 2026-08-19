namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Interaction with the Windows Explorer desktop: toggling desktop icon visibility and
/// detecting Explorer restarts (which reset the desktop window). Implemented in the Shell layer.
/// </summary>
public interface IExplorerDesktopService
{
    bool AreDesktopIconsVisible { get; }

    void SetDesktopIconsVisible(bool visible);

    /// <summary>Raised when Explorer (and therefore the desktop window) restarts.</summary>
    event System.EventHandler? ExplorerRestarted;
}
