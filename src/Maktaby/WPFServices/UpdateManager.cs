using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Core.Services;
using Maktaby.Helpers;
using Maktaby.ViewModels;
using Maktaby.Win32.Services;
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
    private readonly IDispatcher _dispatcher;
    private readonly IToastNotificationService? _notifications;

    private System.Windows.Threading.DispatcherTimer? _timer;
    private CancellationTokenSource? _inFlight;
    private bool _promptOpen;
    private string? _notifiedVersion;
    private bool _disposed;

    public UpdateManager(
        IUpdateService updateService,
        ISettingsService settingsService,
        IAppUpdateUi ui, IDispatcher dispatcher,
        IToastNotificationService? notifications = null)
    {
        _updateService = updateService;
        _settingsService = settingsService;
        _ui = ui;
        _dispatcher = dispatcher;

        // Optional: a build without a notification service must still update, it just cannot
        // announce a background result.
        _notifications = notifications;
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

        // DEBUG: the simulation stands in for the network, and also skips the portable gate —
        // a dev build run from the repo has no uninstaller, and the prompt/notification paths
        // being tested are exactly what that gate suppresses.
        var simulating = GlobalFeaturesSwitches.SimulateUpdateAvailable;

        if (!simulating && AppBuildInfo.IsPortable)
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
            // The simulated release is fed in as a RESULT rather than short-circuiting to
            // PromptAsync, so the switch below is the real one: a simulated BACKGROUND check
            // therefore exercises the notification path exactly as a live check would.
            UpdateCheckResult result;
            if (simulating)
            {
                var simulated = BuildSimulatedUpdate();
                result = new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpdateAvailable,
                    Update = simulated,
                };
            }
            else
            {
                result = await _updateService.CheckAsync(
                    ParseChannel(settings.UpdateChannel),
                    AppVersion.Current,
                    settings.UpdateSkippedVersion,
                    cts.Token).ConfigureAwait(true);
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    // An explicit "check for updates" opens the prompt directly. A background
                    // check must not steal focus, so it only raises a notification and the
                    // prompt waits for the user to click it.
                    if (interactive)
                    {
                        await PromptAsync(result.Update!, interactive: true).ConfigureAwait(true);
                    }
                    else
                    {
                        NotifyAsync(result.Update!);
                    }
                    break;

                case UpdateCheckStatus.UpToDate when interactive:
                    _ui.ReportUpToDate(AppVersion.Current);
                    break;

                case UpdateCheckStatus.Skipped when interactive:
                    // Not a confirmation: this release is ALREADY on the skip list, so there
                    // is nothing for the user to decide.
                    _ui.ReportAlreadySkipped(result.Update?.Version);
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

    /// <summary>
    /// Announces a background-discovered update as a tray notification instead of opening the
    /// prompt, and opens the prompt when the user clicks it.
    /// </summary>
    private void NotifyAsync(UpdateInfo update)
    {
        if (_disposed) { return; }

        if (_notifications is null)
        {
            Logging.Log.Warning("Update available but no notification service is registered; not announcing {Version}", update.Version);
            return;
        }

        // Once per version: the check runs every 6 hours and would otherwise re-notify the
        // same release indefinitely, as long as the user keeps ignoring it.
        //if (!string.IsNullOrEmpty(_notifiedVersion) && string.Equals(_notifiedVersion, update.Version, StringComparison.Ordinal)) { return; }
        _notifiedVersion = update.Version;

        var message = update.IsPreRelease
            ? $"Maktaby {update.Version} (pre-release) is available. Click to install."
            : $"Maktaby {update.Version} is available. Click to install.";

        try
        {
            var outcome = _notifications.Show("Update available", message, () => OnNotificationClicked(update));

            switch (outcome)
            {
                case ToastShowResult.Shown:
                    Logging.Log.Information("Update available: notified for {Version}", update.Version);
                    break;

                case ToastShowResult.Disabled:
                    // Notifications are off for Maktaby — by the user, by policy, or by our
                    // own manifest. That is a deliberate decision, so stay silent and let the
                    // next scheduled check look again. Announcing it another way would
                    // override the setting.
                    Logging.Log.Information(
                        "Update available for {Version}, but notifications are disabled - staying silent",
                        update.Version);
                    break;

                default:
                    // This machine cannot show toasts at all, so the update would go
                    // unannounced. Fall back to the prompt, unless that would also be
                    // intrusive: nothing pops over a full-screen app.
                    Logging.Log.Information(
                        "Update available: toasts unavailable, falling back to the prompt ({Version})",
                        update.Version);
                    PromptFromBackgroundAsync(update);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Show() swallows its own failures, so reaching here means the notification
            // service itself threw. Same outcome, same fallback: never let an available update
            // go unannounced, and never surface an error nobody asked for.
            Logging.Log.Warning(ex, "Could not notify about the available update; falling back to the prompt");
            PromptFromBackgroundAsync(update);
        }
    }

    /// <summary>
    /// Opens the prompt in response to the user clicking the update toast.
    /// </summary>
    /// <remarks>
    /// <see cref="ToastNotification.Activated"/> fires on a THREADPOOL thread, not the UI
    /// thread, so the prompt — a WPF <c>Window</c> — has to be marshalled or it cannot be
    /// created at all. This is the only path into <see cref="PromptAsync"/> that does not
    /// already arrive on the UI thread: the tray item and Settings button post through
    /// <c>Dispatcher.BeginInvoke</c>, the 6-hourly check is a <c>DispatcherTimer</c>, and the
    /// startup check runs on the UI thread.
    /// </remarks>
    private void OnNotificationClicked(UpdateInfo update)
    {
        // A toast sits in Action Center until the user dismisses it, which can be days. By
        // the time it is clicked the app may already BE that version, or newer — and acting
        // on what the toast captured would offer, or worse INSTALL, an already-superseded
        // build. The captured UpdateInfo is a snapshot from whenever the notification was
        // posted, so it is only a hint about what to look at, never a decision to act on.
        if (!IsStillNewer(update))
        {
            Logging.Log.Information(
                "Stale update notification clicked ({Version}) but {Current} is already running; re-checking instead",
                update.Version, AppVersion.Current);

            _ = RecheckAfterStaleNotificationAsync();
            return;
        }

        // Fire-and-forget: the toast callback gives nowhere to await. Wrapped rather than
        // left as a bare '_ =' because an escaping exception would otherwise land on the
        // dispatcher as an unobserved task exception, which the app treats as fatal.
        _ = PromptFromToastAsync(update);
    }

    /// <summary>
    /// True when <paramref name="update"/> is still worth offering. Unparseable versions are
    /// allowed through: the check is a safety net against downgrades, not a gate on the
    /// prompt, and refusing to show anything would be the worse failure.
    /// </summary>
    private static bool IsStillNewer(UpdateInfo update)
    {
        if (SemVersion.TryParse(update.Version, out var candidate)
            && SemVersion.TryParse(AppVersion.Current, out var current))
        {
            return candidate.CompareTo(current) > 0;
        }

        return true;
    }

    /// <summary>
    /// A click on a superseded notification must still do something useful, so re-query rather
    /// than ignoring it: the user asked about updates and gets a current answer — a prompt if
    /// something newer exists, or an explicit "you are up to date".
    /// </summary>
    private async Task RecheckAfterStaleNotificationAsync()
    {
        try
        {
            await _dispatcher
                .InvokeAsync(async () => await CheckAsync(interactive: true))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Re-check after a stale update notification failed");
        }
    }

    /// <summary>
    /// Opens the prompt for a BACKGROUND discovery (toast unavailable). Suppressed while a
    /// full-screen app is in front: the user asked for nothing, and a window popping over a
    /// video or a game is worse than waiting for the next scheduled check.
    /// </summary>
    /// <remarks>
    /// Only this path is gated. A toast click is a deliberate user action, and an interactive
    /// "Check for updates…" is a request for an answer — neither should be swallowed.
    /// </remarks>
    private void PromptFromBackgroundAsync(UpdateInfo update)
    {
        if (_disposed) { return; }

        if (FullscreenDetector.IsForegroundFullScreen())
        {
            Logging.Log.Information(
                "Update available for {Version} but a full-screen app is in front - not interrupting",
                update.Version);
            return;
        }

        OnNotificationClicked(update);
    }

    private async Task PromptFromToastAsync(UpdateInfo update)
    {
        try
        {
            // Marshal HERE, once, and stay on the UI thread for the rest of the flow. This runs
            // on the toast's THREADPOOL thread, which has no SynchronizationContext, so every
            // await after it resumed back on the pool - and anything touching WPF state (the
            // prompt Window, and DialogService.ResolveOwner enumerating Application.Windows)
            // threw "a different thread owns this object". Entering here means the awaits inside
            // PromptAsync resume on the WPF dispatcher instead.
            await _dispatcher
                .InvokeAsync(async () => await PromptAsync(update, interactive: true))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Opening the update prompt from the toast failed");
        }
    }

    private async Task PromptAsync(UpdateInfo update, bool interactive)
    {
        // Validate BEFORE anything is shown. An UpdateInfo arrives from JSON over the network,
        // and a malformed one would otherwise surface as an empty window, a broken percentage,
        // or a download that can never be verified - far from the cause.
        if (DescribeUpdateProblems(update) is { Count: > 0 } problems)
        {
            var reasons = string.Join(Environment.NewLine, problems);
            Logging.Log.Error("Refusing to show the update prompt for {Version}: {Reasons}",
                update.Version, string.Join(" | ", problems));
            _ui.ReportInvalidUpdate(reasons);
            return;
        }

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
                    // Ask BEFORE recording. Saving first and confirming afterwards meant "No"
                    // still left the version permanently skipped — the exact opposite of what
                    // answering "No" means.
                    if (await _ui.ReportSkipped(update.Version).ConfigureAwait(true))
                    {
                        settings.UpdateSkippedVersion = update.Version;
                        _settingsService.Save();
                    }
                    break;

                case UpdatePromptChoice.Install:
                    // The prompt stays open for the whole attempt, so a failure or a cancel can
                    // be driven from it rather than re-running the whole check.
                    await DriveInstallAsync(update).ConfigureAwait(true);
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
    /// <returns>
    /// True when there is nothing left to drive: the installer ran, or the app is on its way
    /// out. False means the attempt ended early (cancelled or failed) and the prompt is still
    /// open, waiting for the user's next decision.
    /// </returns>
    public async Task<bool> InstallAsync(UpdateInfo update, bool silent)
    {
        // DEBUG ONLY, and first: faking the download keeps the simulation off the network and
        //, crucially, skips WritePendingMarker and UpdateInstaller.Launch. The real installer
        // terminates this process from PrepareToInstall, which would kill the app mid-test.
        if (GlobalFeaturesSwitches.SimulateUpdateAvailable)
        {
            return await SimulateInstallAsync().ConfigureAwait(true);
        }

        // Written BEFORE the installer starts: installer.iss's PrepareToInstall terminates this
        // process, so nothing may be written after that point.
        UpdateInstaller.WritePendingMarker(AppVersion.Current, update.Version);

        _ui.ReportProgress("Downloading update…", 0);

        // Cancel is wired for the download only. Past ReportInstalling the subscription is
        // removed, so a late click can never reach a token that is already spent.
        using var downloadCts = new CancellationTokenSource();
        void OnCancelRequested(object? sender, EventArgs e)
        {
            try { downloadCts.Cancel(); } catch { /* already cancelled or disposed */ }
        }
        _ui.CancelRequested += OnCancelRequested;

        string? path;
        try
        {
            var progress = new Progress<int>(p => _ui.ReportProgress($"Downloading update… {p}%", p));
            path = await UpdateInstaller
                .DownloadAndVerifyAsync(update, progress, downloadCts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The user chose to cancel. Not an error, and not a failure to report: hand the
            // prompt back so the three choices work again. A partial file may remain; the
            // next attempt verifies it first and re-downloads if it does not match.
            _ui.ResetPromptState();
            Logging.Log.Information("Update download cancelled by the user ({Version})", update.Version);
            return false;
        }
        catch (Exception ex)
        {
            _ui.ReportError("The update could not be downloaded.", ex.Message);
            return false;
        }
        finally
        {
            _ui.CancelRequested -= OnCancelRequested;
        }

        if (path is null)
        {
            // Hash mismatch or an unverifiable signature. The file has been deleted.
            _ui.ReportError(
                "The downloaded update failed verification.",
                "It was deleted and not run. You can try again, or download the release manually from the Maktaby releases page.");
            return false;
        }

        // Point of no return: the installer is about to terminate this process.
        _ui.ReportInstalling();

        try
        {
            UpdateInstaller.Launch(path, silent);

            // Clear the skip so the installed version is never re-offered, then exit. The
            // installer relaunches Maktaby (ShouldLaunchApp returns the captured WasRunning),
            // and that new process reports "updated from X to Y" from the marker.
            _settingsService.UserSettings.UpdateSkippedVersion = null;
            _settingsService.Save();

            _ui.RequestAppExit();
            return true;
        }
        catch (Exception ex)
        {
            _ui.ReportError("The update could not be started.", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Everything wrong with <paramref name="update"/> that would stop the prompt or the
    /// install from working, as user-readable sentences. Empty when it is fine.
    /// </summary>
    /// <remarks>
    /// Covers shape (missing/malformed fields) AND relevance: a release that is already
    /// installed, or older than what is running, is rejected here too. The toast click path
    /// checks the same thing first and re-queries instead of showing this dialog, so the
    /// user gets an answer rather than an error — this is the backstop for every other route.
    /// </remarks>
    /// <remarks>
    /// Only fields the flow actually depends on are checked, and each one names the
    /// consequence — "required" on its own tells the user nothing actionable. Optional
    /// fields (release notes, publish date) are left alone: their absence is legitimate and
    /// the window already handles it.
    /// </remarks>
    private static List<string> DescribeUpdateProblems(UpdateInfo update)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(update.Version))
        {
            problems.Add("• Version is missing, so there is nothing to compare against the installed build.");
        }
        else if (SemVersion.TryParse(update.Version, out var offered))
        {
            // A release that is the one already running, or an OLDER one, must never reach the
            // prompt: the first would install over itself, the second would silently DOWNGRADE
            // the app. Compared with SemVersion, not text - "1.0.9" sorts above "1.0.33" as a
            // string, and "beta.2" above "beta.10".
            if (SemVersion.TryParse(AppVersion.Current, out var installed))
            {
                int order = offered.CompareTo(installed);

                if (order == 0)
                {
                    problems.Add(
                        $"• Version {update.Version} is the version already running, so there is nothing to install.");
                }
                else if (order < 0)
                {
                    problems.Add(
                        $"• Version {update.Version} is OLDER than the installed {AppVersion.Current}. "
                        + "Installing it would downgrade Maktaby.");
                }
            }
        }
        else
        {
            problems.Add($"• Version \"{update.Version}\" is not a recognisable version number.");
        }

        if (string.IsNullOrWhiteSpace(update.TagName))
        {
            problems.Add("• The release tag is missing.");
        }

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            problems.Add("• The installer download URL is missing.");
        }
        else if (!TryGetWebUri(update.DownloadUrl, out _))
        {
            problems.Add($"• The installer URL \"{update.DownloadUrl}\" is not a valid http/https address.");
        }

        if (string.IsNullOrWhiteSpace(update.Sha256))
        {
            problems.Add("• No SHA-256 was published for the installer, so the download could never be verified and would be refused.");
        }
        else if (!IsSha256Hex(update.Sha256))
        {
            problems.Add($"• The published SHA-256 \"{Shorten(update.Sha256)}\" is not 64 hex characters.");
        }

        if (update.SizeBytes <= 0)
        {
            problems.Add("• The installer size is unknown, so download progress cannot be shown.");
        }

        if (!string.IsNullOrWhiteSpace(update.ReleasePageUrl) && !TryGetWebUri(update.ReleasePageUrl, out _))
        {
            problems.Add($"• The release page URL \"{update.ReleasePageUrl}\" is not a valid http/https address.");
        }

        return problems;
    }

    private static bool TryGetWebUri(string text, out Uri? uri)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>64 hex characters, no prefix. The form GitHub's asset digest uses.</summary>
    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64
            && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }

    /// <summary>Keeps a long hash readable in a dialog.</summary>
    private static string Shorten(string value) =>
        value.Length <= 20 ? value : $"{value[..16]}… ({value.Length} chars)";

    /// <summary>
    /// Runs an install attempt and, if it ends early, keeps taking the user's decision from the
    /// still-open prompt until they install successfully or move on.
    /// </summary>
    /// <remarks>
    /// Retry is not a separate outcome: the Retry button re-enters as
    /// <see cref="UpdatePromptChoice.Install"/>, so one loop covers "try again", "later" and
    /// "skip this version" without re-querying GitHub or re-showing the version header.
    /// </remarks>
    private async Task DriveInstallAsync(UpdateInfo update)
    {
        while (!_disposed)
        {
            bool finished = await InstallAsync(update, silent: false).ConfigureAwait(true);

            if (finished) { return; }

            var next = await _ui.WaitForNextChoiceAsync().ConfigureAwait(true);
            switch (next)
            {
                case null:
                case UpdatePromptChoice.Later:
                    return;

                case UpdatePromptChoice.Skip:
                    // Same confirm-then-record contract as the first time round.
                    if (await _ui.ReportSkipped(update.Version).ConfigureAwait(true))
                    {
                        _settingsService.UserSettings.UpdateSkippedVersion = update.Version;
                        _settingsService.Save();
                    }
                    return;

                case UpdatePromptChoice.Install:
                default:
                    // Retry. Loop.
                    break;
            }
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


    /// <summary>
    /// DEBUG ONLY: fakes the download/install so the progress UI can be exercised. Ramps the
    /// bar to 100 and then hands the prompt back, so the window can be tested repeatedly in one
    /// session. Nothing is downloaded, verified, or launched.
    /// </summary>
    private async Task<bool> SimulateInstallAsync()
    {
        Logging.Log.Warning("Simulated update: faking the download - no installer will run");

        using var cts = new CancellationTokenSource();
        void OnCancelRequested(object? sender, EventArgs e)
        {
            try { cts.Cancel(); } catch { }
        }
        _ui.CancelRequested += OnCancelRequested;

        try
        {
            _ui.ReportProgress("Downloading update (simulated)…", 0);
            for (var percent = 10; percent <= 90; percent += 10)
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(true);
                _ui.ReportProgress($"Downloading update (simulated)… {percent}%", percent);
            }

            await Task.Delay(300, cts.Token).ConfigureAwait(true);
            _ui.ReportInstalling();
            await Task.Delay(700, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Logging.Log.Information("Simulated download cancelled by the user");
            _ui.ResetPromptState();
            return false;
        }
        finally
        {
            _ui.CancelRequested -= OnCancelRequested;
        }

        // A real install would be relaunching by now. Hand the prompt back instead, so the
        // simulated flow ends in a state the user can drive again.
        _ui.ResetPromptState();
        Logging.Log.Warning("Simulated update: finished (nothing was installed)");
        return true;
    }

    /// <summary>
    /// DEBUG ONLY: a stand-in release for <see cref="GlobalFeaturesSwitches.SimulateUpdateAvailable"/>.
    /// Deliberately not fetchable — the download URL does not resolve — so choosing "Update now"
    /// walks the real progress and failure UI without installing anything.
    /// </summary>
    private static UpdateInfo BuildSimulatedUpdate() => new()
    {
        Version = "99.0.0-simulated",
        TagName = "v99.0.0-simulated",
        DownloadUrl = "https://localhost.invalid/Maktaby-99.0.0-simulated-x64-setup.exe",
        Sha256 = new string('0', 64),
        SizeBytes = 15 * 1024 * 1024,
        IsPreRelease = true,
        PublishedAt = DateTimeOffset.Now,
        ReleasePageUrl = "https://localhost.invalid/releases/simulated",
        ReleaseNotes = "SIMULATED RELEASE — not a real download. This window is shown by the "
                     + "SimulateUpdateAvailable debug switch so the prompt UI can be tested.",
    };

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

        _notifiedVersion = null;
        try { _inFlight?.Cancel(); } catch { }
        _inFlight = null;
    }
}
