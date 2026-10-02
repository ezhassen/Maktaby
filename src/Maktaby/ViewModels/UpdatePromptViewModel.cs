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
    private bool _isInstalling;

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

    /// <summary>True while downloading or installing — disables all three buttons.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>True once the installer has been launched, so the window can say goodbye
    /// rather than sit there over a desktop the app is about to leave.</summary>
    public bool IsInstalling
    {
        get => _isInstalling;
        set => SetProperty(ref _isInstalling, value);
    }

    public void Choose(UpdatePromptChoice choice) => ChoiceMade?.Invoke(choice);
}
