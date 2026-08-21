namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Drives the file-system → rule → box pipeline. Started once by <see cref="DesktopManager"/> after
/// the containers and rules are loaded.
/// </summary>
public interface IFileRuleCoordinator
{
    void Start();

    void Stop();
}
