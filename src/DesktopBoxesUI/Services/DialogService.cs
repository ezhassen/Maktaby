using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Services;

/// <summary>
/// Default <see cref="IDialogService"/> implementation backed by WPF-UI's <see cref="ContentDialogService"/>.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly ContentDialogService _service = new();

    public Task ShowMessageAsync(string message, DialogOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DialogOptions();
        var merged = options with
        {
            Content = options.Content ?? message,
            CloseButtonText = options.CloseButtonText ?? "OK"
        };
        return ShowAsync(merged, cancellationToken);
    }

    public async Task<bool> ShowConfirmAsync(string message, DialogOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DialogOptions();
        var merged = options with
        {
            Content = options.Content ?? message,
            PrimaryButtonText = options.PrimaryButtonText ?? "OK",
            CloseButtonText = options.CloseButtonText ?? "Cancel"
        };
        var result = await ShowAsync(merged, cancellationToken);
        return result == ContentDialogResult.Primary;
    }

    public async Task<ContentDialogResult> ShowAsync(DialogOptions options, CancellationToken cancellationToken = default)
    {
        var host = ResolveHost();
        if (host is null)
        {
            // No visible window can host the dialog (tray-only flow): degrade gracefully.
            return ShowFallback(options);
        }

        _service.SetDialogHost(host);
        return await _service.ShowAsync(BuildDialog(options), cancellationToken);
    }

    // Prefer the focused window; otherwise the first window that can host a dialog.
    private static ContentDialogHost? ResolveHost()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        var windows = app.Windows.OfType<Window>().ToList();
        var provider = windows.FirstOrDefault(w => w.IsActive) as IContentDialogHostProvider
                       ?? windows.OfType<IContentDialogHostProvider>().FirstOrDefault();
        return provider?.DialogHost;
    }

    private static ContentDialog BuildDialog(DialogOptions o)
    {
        var dialog = new ContentDialog
        {
            Title = o.Title,
            Content = o.Content,
            PrimaryButtonText = o.PrimaryButtonText ?? string.Empty,
            SecondaryButtonText = o.SecondaryButtonText ?? string.Empty,
            CloseButtonText = o.CloseButtonText ?? string.Empty,
            DefaultButton = o.DefaultButton,
            // In this WPF-UI build these default to true and are not auto-disabled when the
            // matching text is empty, so an empty button still renders. Disable explicitly.
            IsPrimaryButtonEnabled = !string.IsNullOrEmpty(o.PrimaryButtonText),
            IsSecondaryButtonEnabled = !string.IsNullOrEmpty(o.SecondaryButtonText)
        };

        if (o.PrimaryButtonAppearance is { } pa) dialog.PrimaryButtonAppearance = pa;
        if (o.SecondaryButtonAppearance is { } sa) dialog.SecondaryButtonAppearance = sa;
        if (o.CloseButtonAppearance is { } ca) dialog.CloseButtonAppearance = ca;

        return dialog;
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
