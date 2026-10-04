using Maktaby.Core.Models;
using System;

namespace Maktaby.ViewModels;

/// <summary>What the user chose in the update prompt.</summary>
public enum UpdatePromptChoice
{
    /// <summary>Dismissed without deciding — checked again on the next scheduled run.</summary>
    Later = 0,

    /// <summary>Download, verify and install now.</summary>
    Install = 1,

    /// <summary>Never offer this exact version again.</summary>
    Skip = 2,
}

/// <summary>
/// View-model for the update prompt. Holds the two versions being compared so the window can
/// show "1.0.32-beta.4 → 1.0.33-beta.1" rather than a bare "an update is available".
/// </summary>
public sealed class UpdatePromptViewModel : ViewModelBase
{
    private string _statusText = string.Empty;
    private int _progress;
    private bool _isBusy;
    private bool _isCancellable;
    private bool _isInstalling;
    private string _errorText = string.Empty;
    private bool _hasError;

    public UpdatePromptViewModel(UpdateInfo update, string currentVersion)
    {
        Update = update ?? throw new ArgumentNullException(nameof(update));
        CurrentVersion = currentVersion;

        CurrentVersionText = $"Installed version: {currentVersion}";
        NewVersionText = update.IsPreRelease
            ? $"Available pre-release: {update.Version}"
            : $"Available version: {update.Version}";

        if (update.PublishedAt is { } published)
        {
            PublishedText = $"Published {published.LocalDateTime:d MMM yyyy}";
        }

        if (!string.IsNullOrWhiteSpace(update.ReleaseNotes))
        {
            NotesText = update.ReleaseNotes!;
        }
    }

    public UpdateInfo Update { get; }

    public string CurrentVersion { get; }

    public string CurrentVersionText { get; }

    public string NewVersionText { get; }

    public string PublishedText { get; } = string.Empty;

    public string NotesText { get; } = string.Empty;

    /// <summary>Raised when the user picks one of the three actions.</summary>
    public event Action<UpdatePromptChoice>? ChoiceMade;

    /// <summary>Raised when the user cancels an in-progress DOWNLOAD. Never raised once the
    /// installer has been launched — at that point the install cannot be taken back.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Why the last attempt failed, shown in red beside the progress area.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>True after a failed attempt. Drives the Retry button.</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>Free-text status shown while downloading/installing.</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>Download progress 0-100, or -1 when indeterminate.</summary>
    public int Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    /// <summary>True while downloading or installing — disables all three buttons and blocks
    /// the window from closing.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanChoose)); }
        }
    }

    /// <summary>Inverse of <see cref="IsBusy"/>, for binding the three buttons' IsEnabled.
    /// Exposed as a property rather than through an inverted converter so the enable rule is
    /// stated once, next to the state it derives from.</summary>
    public bool CanChoose => !IsBusy;

    /// <summary>True while the download can still be abandoned. False once the installer has
    /// been launched: from that point the update is committed and cancelling would leave the
    /// app half-updated.</summary>
    public bool IsCancellable
    {
        get => _isCancellable;
        set => SetProperty(ref _isCancellable, value);
    }

    /// <summary>True once the installer has been launched, so the window can say goodbye
    /// rather than sit there over a desktop the app is about to leave.</summary>
    public bool IsInstalling
    {
        get => _isInstalling;
        set => SetProperty(ref _isInstalling, value);
    }

    public void Choose(UpdatePromptChoice choice)
    {
        // Belt and braces: the buttons are disabled while busy, but a stale click must never
        // be able to pick a second outcome on top of a running install.
        if (_isBusy && choice != UpdatePromptChoice.Install) { return; }

        ClearError();
        ChoiceMade?.Invoke(choice);
    }

    /// <summary>Retries a failed attempt. The button is only shown after a failure, so this
    /// re-enters the flow as a plain <see cref="UpdatePromptChoice.Install"/>.</summary>
    public void Retry()
    {
        if (!_hasError || _isBusy) { return; }
        ClearError();
        ChoiceMade?.Invoke(UpdatePromptChoice.Install);
    }

    /// <summary>Records a failed attempt: red message in the window, choices re-enabled, and
    /// the window left open so the user can retry instead of losing the update entirely.</summary>
    public void ReportFailure(string message)
    {
        ErrorText = message;
        HasError = true;
        IsBusy = false;
        IsCancellable = false;
        IsInstalling = false;
    }

    private void ClearError()
    {
        if (!_hasError && _errorText.Length == 0) { return; }
        ErrorText = string.Empty;
        HasError = false;
    }

    /// <summary>Cancels an in-progress download. Ignored once installing has begun, when the
    /// update is committed and there is nothing to take back.</summary>
    public void Cancel()
    {
        if (!_isCancellable) { return; }
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
