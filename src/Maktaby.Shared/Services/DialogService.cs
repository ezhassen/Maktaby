using System.Windows;
using Wpf.Ui.Controls;
using Maktaby.Shared.Controls;
using Maktaby.Shared.Interfaces;

namespace Maktaby.Shared.Services;

/// <summary>
/// Default <see cref="IDialogService"/> implementation that shows a standalone
/// <see cref="DialogMessageBox"/> window. The dialog is modal to its owner, centered
/// over the owner and clamped to the owner's monitor work area so it is never
/// constrained by a small owner window.
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task ShowMessageAsync(string content, string title, Window? owner = null)
        => this.ShowMessageAsync(content, new DialogOptions { Title = title }, owner);

    public Task<bool> ShowConfirmAsync(string content, string title, string confirmText = "Yes", ControlAppearance primaryButtonAppearance = ControlAppearance.Info, Window? owner = null)
        => this.ShowConfirmAsync(content, new DialogOptions
        {
            Title = title,
            PrimaryButtonText = confirmText,
            PrimaryButtonAppearance = primaryButtonAppearance
        }, owner);

    public Task<bool> ShowConfirmDangerAsync(string content, string title, string confirmText = "Yes", ControlAppearance primaryButtonAppearance = ControlAppearance.Danger, Window? owner = null)
    {
        return ShowConfirmAsync(content, title, confirmText: confirmText, primaryButtonAppearance: primaryButtonAppearance, owner: owner);
    }

    public Task<bool> ShowConfirmDeleteAsync(string content, string title = "Confirm Delete", string confirmText = "Delete", ControlAppearance primaryButtonAppearance = ControlAppearance.Danger, Window? owner = null)
    {
        return ShowConfirmAsync(content, title, confirmText: confirmText, primaryButtonAppearance: primaryButtonAppearance, owner: owner);
    }

    public Task ShowMessageAsync(string message, DialogOptions? options = null, Window? owner = null, CancellationToken cancellationToken = default)
    {
        options ??= new DialogOptions();
        var merged = options with
        {
            Content = options.Content ?? message,
            CloseButtonText = options.CloseButtonText ?? "OK"
        };
        return ShowAsync(merged, owner, cancellationToken);
    }

    public async Task<bool> ShowConfirmAsync(string message, DialogOptions? options = null, Window? owner = null, CancellationToken cancellationToken = default)
    {
        options ??= new DialogOptions();
        var merged = options with
        {
            Content = options.Content ?? message,
            PrimaryButtonText = options.PrimaryButtonText ?? "OK",
            CloseButtonText = options.CloseButtonText ?? "Cancel"
        };
        var result = await ShowAsync(merged, owner, cancellationToken);
        return result == ContentDialogResult.Primary;
    }

    public Task<ContentDialogResult> ShowAsync(DialogOptions options, Window? owner = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(ContentDialogResult.None);
            }

            var resolvedOwner = ResolveOwner(owner);

            // Must run on UI thread because we create and show a Window
            if (Application.Current?.Dispatcher.CheckAccess() == false)
            {
                return Application.Current.Dispatcher.Invoke(() => ShowDialogInternal(options, resolvedOwner, cancellationToken));
            }

            return ShowDialogInternal(options, resolvedOwner, cancellationToken);
        }
        catch (Exception ex)
        {
            //ex.Log_Error();
            System.Windows.MessageBox.Show(ex.ToString(), "DialogService error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return Task.FromResult(ContentDialogResult.None);
        }
    }

    private static Task<ContentDialogResult> ShowDialogInternal(DialogOptions options, Window? owner, CancellationToken cancellationToken)
    {
        // No Application / no window at all: fallback to native MessageBox
        if (Application.Current == null)
        {
            return Task.FromResult(ShowFallback(options));
        }

        var dialog = new DialogMessageBox(owner, options);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                try
                {
                    dialog.Dispatcher.Invoke(dialog.Close);
                }
                catch { }
            });
        }

        // ShowDialog is blocking modal; it disables the owner so parent cannot be interacted with
        dialog.ShowDialog();
        return Task.FromResult(dialog.Result);
    }

    /// <summary>
    /// Resolves the window to use as owner for the dialog.
    /// Priority: explicit owner > active window > any visible window > null.
    /// </summary>
    private static Window? ResolveOwner(Window? explicitOwner)
    {
        if (explicitOwner != null)
        {
            return explicitOwner;
        }

        var app = Application.Current;
        if (app == null)
        {
            return null;
        }

        var windows = app.Windows.OfType<Window>().ToList();

        var active = windows.FirstOrDefault(w => w.IsActive);
        if (active != null)
        {
            return active;
        }

        var visible = windows.FirstOrDefault(w => w.IsVisible);
        if (visible != null)
        {
            return visible;
        }

        // No visible window (e.g. tray-only): return null so dialog centers on primary screen
        return null;
    }

    private static ContentDialogResult ShowFallback(DialogOptions o)
    {
        var text = o.Content?.ToString() ?? string.Empty;

        if (o.PrimaryButtonText is not null)
        {
            var r = System.Windows.MessageBox.Show(
                text, o.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            return r == System.Windows.MessageBoxResult.Yes ? ContentDialogResult.Primary : ContentDialogResult.None;
        }

        System.Windows.MessageBox.Show(
            text, o.Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return ContentDialogResult.None;
    }
}
