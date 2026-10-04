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

    /// <summary>Progress line for the window currently on screen (download). Also drives the
    /// prompt's in-progress state, so the bar appears, the buttons disable, and the window
    /// stops closing. The operation is still cancellable after this.</summary>
    void ReportProgress(string status, int percent);

    /// <summary>The download finished and the installer is about to run. This is the point of
    /// no return: the prompt stops offering Cancel and can no longer be closed.</summary>
    void ReportInstalling();

    /// <summary>Raised when the user cancels an in-progress download. Handlers are expected to
    /// cancel the download token and must be unsubscribed once it completes.</summary>
    event EventHandler? CancelRequested;

    /// <summary>Clears the in-progress state so the prompt becomes interactive again — used
    /// when a download is cancelled or an attempt ends.</summary>
    void ResetPromptState();

    /// <summary>After a failed or cancelled attempt the prompt stays open. Waits for the user's
    /// next decision from it: <see cref="UpdatePromptChoice.Install"/> to retry (also what the
    /// Retry button sends), another choice to switch to it, or null if the window was closed.
    /// Completes immediately if no prompt is open.</summary>
    Task<UpdatePromptChoice?> WaitForNextChoiceAsync();

    /// <summary>Reported after a successful install, on the next launch.</summary>
    void ReportUpdated(string fromVersion, string toVersion);

    /// <summary>Interactive check only: the user asked and deserves an answer.</summary>
    void ReportUpToDate(string currentVersion);

    /// <summary>Asks the user to confirm skipping <paramref name="version"/>. Returns true for
    /// Yes and false for No — the caller must NOT record the skip until this returns true.</summary>
    Task<bool> ReportSkipped(string version);

    /// <summary>Informational only: this release is already skipped, so nothing is offered.</summary>
    void ReportAlreadySkipped(string? version);

    void ReportUnavailable(string? diagnostic);

    void ReportError(string title, string detail);

    /// <summary>
    /// Reports a release that cannot be shown at all, as a modal OK dialog listing why.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReportError"/> on purpose: that one writes into the prompt
    /// window's view-model, which does not exist yet when the release is rejected — the
    /// message would be silently dropped. This is for problems found BEFORE the prompt opens.
    /// </remarks>
    void ReportInvalidUpdate(string reasons);

    /// <summary>Asks the app to shut down so the installer can replace its files.</summary>
    void RequestAppExit();
}
