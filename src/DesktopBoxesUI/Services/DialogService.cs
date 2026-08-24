using System.Diagnostics;
using System.IO;
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

    // Caps so a dialog stays a comfortable, readable size regardless of the host window.
    private const double DialogMaxWidthValue = 480;
    private const double DialogMaxHeightValue = 640;

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
        try
        {
            var host = ResolveHost();
            if (host is null)
            {
                // No visible window can host the dialog (tray-only flow): degrade gracefully.
                return ShowFallback(options);
            }

            _service.SetDialogHost(host);
            //Trace("ShowAsync: SetDialogHost done, calling ShowAsync");
            var dialog = BuildDialog(options);
            //dialog.Opened += (_, _) => Trace("DIALOG Opened");
            //dialog.Closed += (_, e) => Trace($"DIALOG Closed: {e.Result}");
            //dialog.ButtonClicked += (_, e) => Trace($"DIALOG ButtonClicked: {e.Button}");
            var result = await _service.ShowAsync(dialog, cancellationToken);
            //Trace($"ShowAsync: returned {result}");
            return result;
        }
        catch (Exception ex)
        {
            //Trace($"ShowAsync EXCEPTION: {ex}");
            ex.Log_Error();
            System.Windows.MessageBox.Show(ex.ToString(), "DialogService error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return ContentDialogResult.None;
        }
    }

    // Prefer the focused window's own ContentDialogHost; fall back to any window that can host.
    // The dialog is shown within that window, so on a small window it is centered/clamped there
    // (reliable and visible) rather than rendered in a separate transparent overlay (which does not
    // display the WPF-UI ContentDialog at all).
    private static ContentDialogHost? ResolveHost()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        var windows = app.Windows.OfType<Window>().ToList();

        if (windows.FirstOrDefault(w => w.IsActive) is { } active && active is IContentDialogHostProvider activeProvider)
        {
            return activeProvider.DialogHost;
        }

        if (windows.FirstOrDefault(w => w is IContentDialogHostProvider) is { } other && other is IContentDialogHostProvider otherProvider)
        {
            return otherProvider.DialogHost;
        }

        return null;
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
            DialogMaxWidth = DialogMaxWidthValue,
            DialogMaxHeight = DialogMaxHeightValue,
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

    //private static void Trace(string message)
    //{
    //    try
    //    {
    //        var path = Path.Combine(Path.GetTempPath(), "dbx_dialog.log");
    //        File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
    //    }
    //    catch
    //    {
    //        // ignore tracing failures
    //    }
    //}
}
