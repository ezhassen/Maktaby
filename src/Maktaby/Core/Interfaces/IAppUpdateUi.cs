using Maktaby.Core.Models;
using Maktaby.ViewModels;
using System.Threading.Tasks;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// The presentation surface of the update flow, so <c>UpdateManager</c> never touches WPF
/// types, windows or dialogs directly. Implemented in the app (see
/// <c>WPFServices.WpfUpdateUi</c>), which is the only layer allowed to know a prompt window exists.
/// </summary>
public interface IAppUpdateUi
{
    /// <summary>Shows the non-blocking prompt and returns what the user chose. Runs on the UI
    /// thread and completes when the window closes.</summary>
    Task<UpdatePromptChoice> ShowPromptAsync(UpdateInfo update, string currentVersion);

    /// <summary>Progress line for the window currently on screen (download/install).</summary>
    void ReportProgress(string status, int percent);

    /// <summary>Reported after a successful install, on the next launch.</summary>
    void ReportUpdated(string fromVersion, string toVersion);

    /// <summary>Interactive check only: the user asked and deserves an answer.</summary>
    void ReportUpToDate(string currentVersion);

    void ReportSkipped(string? version);

    void ReportUnavailable(string? diagnostic);

    void ReportError(string title, string detail);

    /// <summary>Asks the app to shut down so the installer can replace its files.</summary>
    void RequestAppExit();
}
