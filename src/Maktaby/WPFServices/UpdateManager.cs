using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Helpers;
using Maktaby.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.WPFServices;

/// <summary>
/// Owns the update lifecycle: the scheduled check, the prompt, and the install.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="System.Windows.Threading.DispatcherTimer"/> is a deliberate exception to the
/// no-polling rule in AGENTS.md. A release check is inherently periodic, cannot be driven by a
/// system event, and at four checks a day costs 4 of GitHub's 60 unauthenticated
/// requests-per-hour-per-IP budget. It is event-free but long-period, not a busy poll, and it
/// never overlaps the loading dialog.
/// </para>
/// <para>
/// <b>Requires the repository to be public.</b> The GitHub API is queried unauthenticated —
/// a shipped desktop app cannot embed a token — so a private repo answers 404 and every
/// check returns <see cref="UpdateCheckStatus.Unavailable"/>.
/// </para>
/// </remarks>
public sealed class UpdateManager : IDisposable
{
    private static readonly TimeSpan s_checkInterval = TimeSpan.FromHours(6);

    private readonly IUpdateService _updateService;
    private readonly ISettingsService _settingsService;
    private readonly IAppUpdateUi _ui;

    private System.Windows.Threading.DispatcherTimer? _timer;
    private CancellationTokenSource? _inFlight;
    private bool _promptOpen;
    private bool _disposed;

    public UpdateManager(IUpdateService updateService, ISettingsService settingsService, IAppUpdateUi ui)
    {
        _updateService = updateService;
        _settingsService = settingsService;
        _ui = ui;
    }

    /// <summary>Starts the periodic check. Safe to call twice.</summary>
    public void Start()
    {
        if (_disposed || _timer is not null) { return; }

        if (AppBuildInfo.IsPortable)
        {
            // A portable copy has no installer to update to. Offering one would download an
            // Inno Setup .exe that installs a SECOND copy under %ProgramFiles%, registers the
            // startup key and leaves the portable folder behind — silently turning a
            // portable install into an installed one. Portable users replace the folder.
            Logging.Log.Information(
                "Portable build: the in-app update checker is disabled (no installer to update to)");
            return;
        }

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = s_checkInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Fire-and-forget: a background check that fails must never surface an error window.
        _ = CheckAsync(interactive: false);
    }

    /// <summary>
    /// Runs a check. <paramref name="interactive"/> is true for an explicit user action, where
    /// a result (including "you are up to date") is always reported back.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckAsync(bool interactive)
    {
        if (_disposed) { return null; }

        if (AppBuildInfo.IsPortable)
        {
            // Reached by the tray "Check for updates…" and the Settings button, which both
            // bypass Start(). Say why rather than looking broken.
            if (interactive) { _ui.ReportUnavailable("Updates are not available in a portable build."); }
            return null;
        }

        // One check at a time. A slow network must not let the timer stack requests.
        if (_inFlight is not null) { return null; }

        var settings = _settingsService.UserSettings;
        var cts = new CancellationTokenSource();
        _inFlight = cts;

        try
        {
            var result = await _updateService.CheckAsync(
                ParseChannel(settings.UpdateChannel),
                AppVersion.Current,
                settings.UpdateSkippedVersion,
                cts.Token).ConfigureAwait(true);

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    await PromptAsync(result.Update!, interactive).ConfigureAwait(true);
                    break;

                case UpdateCheckStatus.UpToDate when interactive:
                    _ui.ReportUpToDate(AppVersion.Current);
                    break;

                case UpdateCheckStatus.Skipped when interactive:
                    _ui.ReportSkipped(result.Update?.Version);
                    break;

                case UpdateCheckStatus.Unavailable when interactive:
                    _ui.ReportUnavailable(result.Diagnostic);
                    break;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // The service already folds expected failures into a status; reaching here is a bug.
            // Still must not surface on a background check.
            Logging.Log.Warning(ex, "Update check failed unexpectedly");
            if (interactive) { _ui.ReportUnavailable(ex.Message); }
            return null;
        }
        finally
        {
            _inFlight = null;
            cts.Dispose();
        }
    }

    private async Task PromptAsync(UpdateInfo update, bool interactive)
    {
        // One prompt at a time. Also stops a background check from stealing focus while the
        // user is mid-task: if a prompt is already up, this update is not re-offered.
        if (_promptOpen) { return; }
        _promptOpen = true;

        try
        {
            var choice = await _ui.ShowPromptAsync(update, AppVersion.Current).ConfigureAwait(true);
            var settings = _settingsService.UserSettings;

            switch (choice)
            {
                case UpdatePromptChoice.Skip:
                    settings.UpdateSkippedVersion = update.Version;
                    _settingsService.Save();
                    _ui.ReportSkipped(update.Version);
                    break;

                case UpdatePromptChoice.Install:
                    await InstallAsync(update, silent: false).ConfigureAwait(true);
                    break;

                case UpdatePromptChoice.Later:
                default:
                    // Nothing recorded: the next scheduled check offers it again.
                    break;
            }
        }
        finally
        {
            _promptOpen = false;
        }
    }

    /// <summary>
    /// Downloads, verifies and launches the installer, then asks the app to exit.
    /// </summary>
    public async Task InstallAsync(UpdateInfo update, bool silent)
    {
        // Written BEFORE the installer starts: installer.iss's PrepareToInstall terminates this
        // process, so nothing may be written after that point.
        UpdateInstaller.WritePendingMarker(AppVersion.Current, update.Version);

        _ui.ReportProgress("Downloading update…", 0);

        string? path;
        try
        {
            var progress = new Progress<int>(p => _ui.ReportProgress($"Downloading update… {p}%", p));
            path = await UpdateInstaller
                .DownloadAndVerifyAsync(update, progress, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _ui.ReportError("The update could not be downloaded.", ex.Message);
            return;
        }

        if (path is null)
        {
            // Hash mismatch or an unverifiable signature. The file has been deleted.
            _ui.ReportError(
                "The downloaded update failed verification.",
                "It was deleted and not run. If this repeats, download the release manually from the Maktaby releases page.");
            return;
        }

        _ui.ReportProgress("Installing…", 100);

        try
        {
            UpdateInstaller.Launch(path, silent);

            // Clear the skip so the installed version is never re-offered, then exit. The
            // installer relaunches Maktaby (ShouldLaunchApp returns the captured WasRunning),
            // and that new process reports "updated from X to Y" from the marker.
            _settingsService.UserSettings.UpdateSkippedVersion = null;
            _settingsService.Save();

            _ui.RequestAppExit();
        }
        catch (Exception ex)
        {
            _ui.ReportError("The update could not be started.", ex.Message);
        }
    }

    /// <summary>
    /// Called once at startup, after the loading dialog. Reports a version transition left by a
    /// previous install, then kicks the first check if enabled.
    /// </summary>
    public void OnStartupComplete()
    {
        var pending = UpdateInstaller.ConsumePendingMarker();
        if (pending is { } transition)
        {
            _ui.ReportUpdated(transition.From, transition.To);
        }

        if (_settingsService.UserSettings.CheckForUpdatesOnStartup)
        {
            _ = CheckAsync(interactive: false);
        }
    }

    private static UpdateChannel ParseChannel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "stable" => UpdateChannel.Stable,
        "beta" => UpdateChannel.Beta,
        _ => UpdateChannel.Auto,
    };

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;

        Stop();

        try { _inFlight?.Cancel(); } catch { }
        _inFlight = null;
    }
}
