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
    private UpdatePromptViewModel? _current;

    public WpfUpdateUi(IDialogService dialogService)
    {
        _dialogService = dialogService;
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
            // Complete the result BEFORE closing the window, and do both from this single
            // handler. Closing first is a race that silently discarded the user's choice:
            // Window.Close() raises Closed synchronously, so a Closed handler would complete
            // the task with Later before OnChoice ever ran, and the later TrySetResult would
            // be a no-op. Every button then behaved as "Later" — Install and Skip did
            // nothing at all.
            if (tcs.TrySetResult(choice))
            {
                window.Close();
            }
        }

        // Closing via the X button is "not now", not a decision. It can only win when no
        // button was pressed, which TrySetResult above guarantees.
        void OnClosed(object? sender, EventArgs e) => tcs.TrySetResult(UpdatePromptChoice.Later);

        vm.ChoiceMade += OnChoice;
        window.Closed += OnClosed;

        try
        {
            window.Show();      // non-modal on purpose: it must never block the desktop
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
            _current = null;
        }
    }

    public void ReportProgress(string status, int percent)
    {
        var vm = _current;
        if (vm is null) { return; }

        vm.StatusText = status;
        vm.Progress = Math.Clamp(percent, 0, 100);
    }

    public void ReportUpdated(string fromVersion, string toVersion)
    {
        try
        {
            Dispatch(() => _dialogService.ShowMessageAsync(
                $"Maktaby was updated from {fromVersion} to {toVersion}.",
                new DialogOptions
                {
                    Title = "Update installed",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Success,
                }));
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
            Dispatch(() => _dialogService.ShowMessageAsync(
                $"Maktaby {currentVersion} is the latest version.",
                new DialogOptions
                {
                    Title = "No update available",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
                }));
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
            Dispatch(() => _dialogService.ShowMessageAsync(
                $"Maktaby {version} is already on your skip list. A newer release will still be suggested.",
                new DialogOptions
                {
                    Title = "Version skipped",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
                }));
        }
        catch { }
    }

    public void ReportUnavailable(string? diagnostic)
    {
        Logging.Log.Information("Update check unavailable: {Reason}", diagnostic ?? "no diagnostic");

        try
        {
            Dispatch(() => _dialogService.ShowMessageAsync(
                "Maktaby could not reach GitHub to check for updates. Check your internet connection and try again.",
                new DialogOptions
                {
                    Title = "Update check failed",
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Info,
                }));
        }
        catch { }
    }

    public void ReportError(string title, string detail)
    {
        try
        {
            Dispatch(() => _dialogService.ShowMessageAsync(
                detail,
                new DialogOptions
                {
                    Title = title,
                    CloseButtonText = "OK",
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                }));
        }
        catch { }
    }

    public void RequestAppExit()
    {
        try
        {
            Dispatch(() =>
            {
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

    /// <summary>Everything here is called from the UI thread, but the install path resumes
    /// after an await, so the hop back is made explicit rather than assumed.</summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return; }
        dispatcher.BeginInvoke(action);
    }
}
