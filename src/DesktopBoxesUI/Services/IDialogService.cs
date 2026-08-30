using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Services;

/// <summary>
/// A window that can host WPF-UI content dialogs. The <see cref="IDialogService"/> renders dialogs
/// on the active window implementing this interface.
/// </summary>
public interface IContentDialogHostProvider
{
    ContentDialogHost DialogHost { get; }
}

/// <summary>
/// Configurable options for a content dialog shown through <see cref="IDialogService"/>.
/// </summary>
public sealed record DialogOptions
{
    public string? Title { get; init; }
    public object? Content { get; init; }
    public string? PrimaryButtonText { get; init; }
    public string? SecondaryButtonText { get; init; }
    public string? CloseButtonText { get; init; }
    public ContentDialogButton DefaultButton { get; init; } = ContentDialogButton.Close;
    public ControlAppearance? PrimaryButtonAppearance { get; init; }
    public ControlAppearance? SecondaryButtonAppearance { get; init; }
    public ControlAppearance? CloseButtonAppearance { get; init; }
}

/// <summary>
/// Application-wide replacement for <see cref="System.Windows.MessageBox"/> that renders WPF-UI
/// <see cref="ContentDialog"/>s on the active host window. Falls back to the native message box when
/// no window currently provides a dialog host (e.g. a tray-only flow with no visible window).
/// </summary>
public interface IDialogService
{
    Task ShowMessageAsync(string message, DialogOptions? options = null, CancellationToken cancellationToken = default);
    Task ShowMessageAsync(string content, string title);
    Task<bool> ShowConfirmAsync(string message, DialogOptions? options = null, CancellationToken cancellationToken = default);
    Task<bool> ShowConfirmAsync(string content, string title, string confirmText = "Yes", ControlAppearance primaryButtonAppearance = ControlAppearance.Info);
    Task<bool> ShowConfirmDangerAsync(string content, string title, string confirmText = "Yes", ControlAppearance primaryButtonAppearance = ControlAppearance.Danger);
    Task<bool> ShowConfirmDeleteAsync(string content, string title = "Confirm Delete", string confirmText = "Delete", ControlAppearance primaryButtonAppearance = ControlAppearance.Danger);
    Task<ContentDialogResult> ShowAsync(DialogOptions options, CancellationToken cancellationToken = default);
}
