namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Global modal loading dialog. While shown it blocks all user input (the dialog is application-
/// modal, and the tray context menu is fully disabled), pauses live-wallpaper playback and suspends
/// widget windows, and exposes <see cref="IsBusy"/> so other code can check or defer work.
/// Only one dialog is ever shown at a time — concurrent callers are serialized.
/// </summary>
public interface ILoadingDialogService
{
    /// <summary>True while a loading dialog is on screen (playback paused, input blocked).</summary>
    bool IsBusy { get; }

    /// <summary>Raised on the UI thread whenever <see cref="IsBusy"/> changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Shows the dialog on the primary screen, runs <paramref name="loadingTask"/> to completion,
    /// then invokes <paramref name="afterFinished"/> on the UI thread with the task result.
    /// <paramref name="loadingTask"/> receives a thread-safe reporter for the dialog message and
    /// returns the task result. Exceptions propagate to the caller; <paramref name="afterFinished"/>
    /// is skipped when the task faults. Not cancellable — the dialog has no Cancel button.
    /// Playback is paused during the dialog only when <paramref name="pausePlayback"/> is set.
    /// </summary>
    Task<object?> ShowAsync(Func<Action<string>, Task<object?>> loadingTask, Action<object?>? afterFinished = null, bool pausePlayback = true);

    /// <summary>
    /// Cancellable overload: the dialog shows a Cancel button and the task receives a
    /// <see cref="CancellationToken"/> that fires on Cancel (or when
    /// <paramref name="cancellationToken"/> fires). A task that observes cancellation ends the
    /// dialog with a <c>null</c> result and <paramref name="afterFinished"/> is skipped; a task
    /// that ignores the token runs to normal completion. A task that is already cancelled before
    /// the dialog shows never shows it (returns <c>null</c>). Playback is paused during the
    /// dialog only when <paramref name="pausePlayback"/> is set.
    /// </summary>
    Task<object?> ShowAsync(
        Func<Action<string>, CancellationToken, Task<object?>> loadingTask,
        Action<object?>? afterFinished,
        CancellationToken cancellationToken = default,
        bool pausePlayback = false);
}
