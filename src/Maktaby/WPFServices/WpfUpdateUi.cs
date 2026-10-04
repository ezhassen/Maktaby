using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Shared.Interfaces;
using Maktaby.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Maktaby.WPFServices;

/// <summary>
/// WPF implementation of <see cref="IAppUpdateUi"/>: shows the prompt window, drives its
/// progress, and reports results through the existing <see cref="IDialogService"/> so
/// informational messages keep the app's look.
/// <para>
/// Every message here uses <c>CloseButtonText</c>/<c>CloseButtonAppearance</c>, NOT
/// <c>PrimaryButtonText</c>. <see cref="DialogService"/> renders the primary button as an
/// extra control alongside the close button, so setting it produces a two-button
/// "OK | Cancel" bar on what is only an acknowledgement. The close button is the single
/// button here, and its appearance still controls the colour.
/// </para>
/// </summary>
public sealed class WpfUpdateUi : IAppUpdateUi
{
    private readonly IDialogService _dialogService;
    private readonly IDispatcher _dispatcher;
    private UpdatePromptViewModel? _current;
    private Window? _currentWindow;

    /// <summary>Raised when the prompt's Cancel button is pressed.</summary>
    public event EventHandler? CancelRequested;

    public WpfUpdateUi(IDialogService dialogService, IDispatcher dispatcher)
    {
        _dialogService = dialogService;
        _dispatcher = dispatcher;
    }

    public async Task<UpdatePromptChoice> ShowPromptAsync(UpdateInfo update, string currentVersion)
    {
        var vm = new UpdatePromptViewModel(update, currentVersion);
        _current = vm;

        var window = new Views.UpdatePromptView { DataContext = vm };
        var tcs = new TaskCompletionSource<UpdatePromptChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChoice(UpdatePromptChoice choice)
        {
            // Complete the result BEFORE closing, and do both from this single handler.
            // Closing first is a race that silently discarded the user's choice:
            // Window.Close() raises Closed synchronously, so a Closed handler would complete
            // the task with Later before OnChoice ever ran, and the later TrySetResult would
            // be a no-op. Every button then behaved as "Later".
            if (!tcs.TrySetResult(choice))
            {
                return;
            }

            // "Update now" KEEPS the window open. The download progress that follows is
            // reported to this same window, so closing it here left ReportProgress with
            // nothing to draw on and the install ran invisibly.
            if (choice != UpdatePromptChoice.Install)
            {
                _dispatcher.Invoke(window.Close);
            }
        }

        // Closing via the X button is "not now", not a decision. It can only win when no
        // button was pressed, which TrySetResult above guarantees.
        //
        // This also drops the window reference, which is what keeps an abandoned prompt from
        // leaking: either the user closed it, or we did after Later/Skip. An Install prompt
        // is released by ReportError (failed download) or by RequestAppExit (the app is
        // going away).
        void OnClosed(object? sender, EventArgs e)
        {
            _currentWindow = null;
            _current = null;
            vm.CancelRequested -= OnCancel;
            tcs.TrySetResult(UpdatePromptChoice.Later);
        }

        vm.ChoiceMade += OnChoice;
        vm.CancelRequested += OnCancel;
        window.Closed += OnClosed;
        // Cancel stays wired for the WINDOW's lifetime, deliberately not for this method's:
        // ShowPromptAsync returns the instant a choice is made, which is before the download
        // even starts, so unsubscribing in the finally severed Cancel for the whole install.

        try
        {
            _currentWindow = window;
            window.Show();      // non-modal on purpose: it must never block the desktop
            window.Activate();
            return await tcs.Task.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "Failed to show the update prompt");
            return UpdatePromptChoice.Later;
        }
        finally
        {
            vm.ChoiceMade -= OnChoice;
            window.Closed -= OnClosed;
        }
    }

    private void OnCancel(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    public void ReportProgress(string status, int percent)
    {
        var vm = _current;
        if (vm is null) { return; }

        var clamped = Math.Clamp(percent, 0, 100);
        vm.StatusText = status;
        vm.Progress = clamped;

        // The progress panel, the disabled buttons and the blocked close are all driven from
        // here — the one place that knows an install is running. Downloading is still
        // cancellable; ReportInstalling is what closes that door.
        vm.IsBusy = true;
        vm.IsCancellable = true;
        vm.IsInstalling = false;
    }

    /// <summary>The installer is about to run: not cancellable, and not closable.</summary>
    public void ReportInstalling()
    {
        var vm = _current;
        if (vm is null) { return; }

        vm.IsBusy = true;
        vm.IsCancellable = false;
        vm.IsInstalling = true;
        vm.Progress = 100;
    }

    /// <summary>Closes the prompt window if one is still on screen (an Install prompt stays
    /// open for its progress) and drops the references. Safe to call when none is open.</summary>
    private void ClosePromptWindow()
    {
        var window = _currentWindow;
        _currentWindow = null;
        _current = null;

        if (window is null) { return; }

        try
        {
            if (window.IsVisible) { window.Close(); }
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Could not close the update prompt window");
        }
    }

    /// <summary>Clears the in-progress state so the prompt becomes interactive again. Called
    /// when an install ends without launching the installer (a failed download, or the end
    /// of a simulated one) - otherwise the window would sit disabled forever.</summary>
    public void ResetPromptState()
    {
        var vm = _current;
        if (vm is null) { return; }

        vm.IsBusy = false;
        vm.IsCancellable = false;
        vm.IsInstalling = false;
        vm.Progress = 0;
        vm.StatusText = string.Empty;
    }

    public void ReportUpdated(string fromVersion, string toVersion)
    {
        try
        {
            _dialogService.ShowMessageAsync(
                $"Maktaby was updated from {fromVersion} to {toVersion}.",
                new DialogOptions
                {
                    Title = "Update installed",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Success,
                });
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Could not show the update-installed notification");
        }
    }

    public void ReportUpToDate(string currentVersion)
    {
        try
        {
            _dialogService.ShowMessageAsync(
                $"Maktaby {currentVersion} is the latest version.",
                new DialogOptions
                {
                    Title = "No update available",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
                });
        }
        catch { }
    }

    /// <summary>Confirmation for the "Skip this version" action. Returns true for Yes.
    /// Deliberately a question, not an acknowledgement: a skip is recorded permanently, so
    /// "No" must leave the release on offer.</summary>
    public Task<bool> ReportSkipped(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return Task.FromResult(false); }

        // DialogService.ShowAsync marshals to the UI thread itself, so awaiting this
        // Task directly is already correct.
        return _dialogService.ShowConfirmAsync(
            $"Stop offering Maktaby {version}? A newer release will still be suggested.",
            new DialogOptions
            {
                Title = "Skip this version",
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
            });
    }

    /// <summary>Informational: the newest release is one the user already skipped.</summary>
    public void ReportAlreadySkipped(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return; }

        try
        {
            _dialogService.ShowMessageAsync(
                $"Maktaby {version} is already on your skip list. A newer release will still be suggested.",
                new DialogOptions
                {
                    Title = "Version skipped",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
                });
        }
        catch { }
    }

    public void ReportUnavailable(string? diagnostic)
    {
        Logging.Log.Information("Update check unavailable: {Reason}", diagnostic ?? "no diagnostic");

        try
        {
            _dialogService.ShowMessageAsync(
                "Maktaby could not reach GitHub to check for updates. Check your internet connection and try again.",
                new DialogOptions
                {
                    Title = "Update check failed",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
                });
        }
        catch { }
    }

    /// <summary>
    /// Reports a failed or cancelled attempt INLINE: the reason in red beside the progress
    /// area, the choices re-enabled, and a Retry button.
    /// </summary>
    /// <remarks>
    /// No message box. A modal on top of a window that already says what went wrong is noise,
    /// and it would also cover the Retry button the user needs. The window stays open either
    /// way, so an update is never lost to a failure the user could only dismiss.
    /// </remarks>
    public void ReportError(string title, string detail)
    {
        Dispatch(() =>
        {
            var vm = _current;
            if (vm is null) { return; }

            var message = string.IsNullOrWhiteSpace(detail) ? title : detail;
            vm.ReportFailure(string.IsNullOrWhiteSpace(title) ? message : $"{title}: {message}");

            Logging.Log.Warning("Update attempt failed: {Title} - {Detail}", title, detail);
        });
    }

    /// <summary>
    /// Waits for the user's next decision on the still-open prompt, so a failed attempt can be
    /// retried without re-running the whole check.
    /// </summary>
    public async Task<UpdatePromptChoice?> WaitForNextChoiceAsync()
    {
        var vm = _current;
        var window = _currentWindow;
        if (vm is null || window is null) { return null; }

        var tcs = new TaskCompletionSource<UpdatePromptChoice?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChoice(UpdatePromptChoice choice)
        {
            if (tcs.TrySetResult(choice))
            {
                // Install (and therefore Retry) keeps the window for the next attempt; any
                // other decision ends the prompt for good.
                if (choice != UpdatePromptChoice.Install) { window.Close(); }
            }
        }

        void OnClosed(object? sender, EventArgs e) => tcs.TrySetResult(null);

        vm.ChoiceMade += OnChoice;
        window.Closed += OnClosed;
        try
        {
            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            vm.ChoiceMade -= OnChoice;
            window.Closed -= OnClosed;
        }
    }
    /// <summary>Modal OK dialog for a release rejected before the prompt exists. Close button
    /// only, for the reason documented on this class.</summary>
    public void ReportInvalidUpdate(string reasons)
    {
        try
        {
            _dialogService.ShowMessageAsync(
                $"Maktaby found a release it cannot display, please check for update manualy to get latest update:\r\n\r\n{reasons}",
                new DialogOptions
                {
                    Title = "Invalid update data",
                    CloseButtonText = "OK",
                    // Danger, not Warning: Wpf.Ui has no Warning appearance, and this is a
                    // refusal to run the release rather than a caution about it.
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,

                });
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Could not report invalid update data to the user");
        }
    }

    public void RequestAppExit()
    {
        try
        {
            Dispatch(() =>
            {
                ClosePromptWindow();

                var app = Application.Current;
                if (app is null) { return; }
                app.Shutdown();
            });
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Could not request app exit for the update");
        }
    }

    /// <summary>Marshals to the UI thread. Needed for <see cref="RequestAppExit"/>, which runs
    /// after the install's awaits and is not a dialog. The dialog methods above deliberately
    /// do NOT use this: <c>DialogService.ShowAsync</c> already hops to the dispatcher itself,
    /// so wrapping them marshalled the same work twice.</summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return; }
        dispatcher.BeginInvoke(action);
    }
}
