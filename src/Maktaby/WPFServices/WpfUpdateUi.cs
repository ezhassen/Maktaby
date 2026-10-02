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

        var choice = UpdatePromptChoice.Later;
        vm.ChoiceMade += c =>
        {
            choice = c;
            // "Update later" must also close the window, so the choice event is the single
            // exit path for all three buttons.
            if (Application.Current.Windows
                .OfType<Views.UpdatePromptView>()
                .FirstOrDefault() is { } open)
            {
                open.Close();
            }
        };

        try
        {
            var window = new Views.UpdatePromptView { DataContext = vm };
            window.Show();      // non-modal on purpose: it must never block the desktop
            return await WaitForChoiceAsync(vm, window).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logging.Log.Error(ex, "Failed to show the update prompt");
            return UpdatePromptChoice.Later;
        }
        finally
        {
            _current = null;
        }
    }

    private static async Task<UpdatePromptChoice> WaitForChoiceAsync(
        UpdatePromptViewModel vm, Window window)
    {
        var tcs = new TaskCompletionSource<UpdatePromptChoice>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChoice(UpdatePromptChoice choice) => tcs.TrySetResult(choice);
        vm.ChoiceMade += OnChoice;

        // Closing via the X button is "not now", not a decision.
        void OnClosed(object? s, EventArgs e) => tcs.TrySetResult(UpdatePromptChoice.Later);
        window.Closed += OnClosed;

        try
        {
            return await tcs.Task.ConfigureAwait(true);
        }
        finally
        {
            window.Closed -= OnClosed;
            vm.ChoiceMade -= OnChoice;
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

    public void ReportSkipped(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return; }

        try
        {
            Dispatch(() => _dialogService.ShowMessageAsync(
                $"Maktaby {version} will not be offered again. A newer version will still be suggested.",
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
