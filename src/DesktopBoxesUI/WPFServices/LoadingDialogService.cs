using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Views.Containers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopBoxesUI.WPFServices;

/// <summary>
/// Global loading dialog: a single application-modal <see cref="LoadingDialog"/> on the primary
/// screen. Modality blocks all same-thread windows (boxes, widgets, surface, settings); the tray
/// menu is suppressed separately via <see cref="IsBusy"/> — it is not opened at all while a
/// dialog is up, and one already open is dismissed (<c>App</c> drives this from
/// <see cref="StateChanged"/> via <c>TrayIconUI.SetMenuSuppressed</c>, and re-applies it when the
/// tray icon is rebuilt). Callers can opt into pausing live-wallpaper playback and suspending
/// widget windows for the dialog's lifetime (off by default); resume then covers only windows the
/// service suspended itself — widgets already suspended (auto-pause, hidden) are left alone.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class LoadingDialogService : ILoadingDialogService
{
    private readonly IDispatcher _dispatcher;
    private readonly LiveWallpaperManager _wallpaper;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _isBusy;
    private bool _didPausePlayback;
    private bool _wasWallpaperPlaying;
    private readonly List<WidgetWindow> _suspendedByMe = new();

    public bool IsBusy => _isBusy;

    public event EventHandler? StateChanged;

    public LoadingDialogService(IDispatcher dispatcher, LiveWallpaperManager wallpaper)
    {
        _dispatcher = dispatcher;
        _wallpaper = wallpaper;
    }

    public Task<object?> ShowAsync(Func<Action<string>, Task<object?>> loadingTask, Action<object?>? afterFinished = null, bool pausePlayback = true)
        => RunAsync((report, _) => loadingTask(report), afterFinished, CancellationToken.None, canCancel: false, pausePlayback);

    public Task<object?> ShowAsync(
        Func<Action<string>, CancellationToken, Task<object?>> loadingTask,
        Action<object?>? afterFinished,
        CancellationToken cancellationToken = default,
        bool pausePlayback = false)
        => RunAsync(loadingTask, afterFinished, cancellationToken, canCancel: true, pausePlayback);

    private async Task<object?> RunAsync(
        Func<Action<string>, CancellationToken, Task<object?>> loadingTask,
        Action<object?>? afterFinished,
        CancellationToken externalToken,
        bool canCancel,
        bool pausePlayback)
    {
        if (externalToken.IsCancellationRequested) return null;
        try
        {
            await _gate.WaitAsync(externalToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            return await RunCoreAsync(loadingTask, afterFinished, externalToken, canCancel, pausePlayback).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<object?> RunCoreAsync(
        Func<Action<string>, CancellationToken, Task<object?>> loadingTask,
        Action<object?>? afterFinished,
        CancellationToken externalToken,
        bool canCancel,
        bool pausePlayback)
    {
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        // The dialog runs on the UI thread with ShowDialog: the nested message loop keeps pumping,
        // so UI-touching tasks (startup init creates windows) keep working while input is blocked.
        await _dispatcher.InvokeAsync(() =>
        {
            var dialog = new LoadingDialog();
            if (canCancel)
                dialog.EnableCancel(() => { try { cts.Cancel(); } catch { } });
            SetBusy(true, pausePlayback);
            dialog.Loaded += async (_, _) =>
            {
                bool completed = false;
                object? result = null;
                try
                {
                    result = await loadingTask(dialog.SetMessage, cts.Token).ConfigureAwait(true);
                    completed = true;
                }
                catch (OperationCanceledException)
                {
                    // Cancelled: dialog ends with null, afterFinished is skipped.
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
                finally
                {
                    try { dialog.AllowClose(); dialog.Close(); } catch { }
                    SetBusy(false);
                }
                if (completed)
                {
                    try
                    {
                        afterFinished?.Invoke(result);
                        done.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        done.TrySetException(ex);
                    }
                }
                else if (!done.Task.IsCompleted)
                {
                    done.TrySetResult(null);
                }
            };
            dialog.ShowDialog();
        }).ConfigureAwait(false);

        return await done.Task.ConfigureAwait(false);
    }

    /// <summary>Must run on the UI thread (touches windows, raises UI-consumed event).</summary>
    private void SetBusy(bool busy, bool pausePlayback = false)
    {
        if (busy == _isBusy) return;
        if (busy)
        {
            _didPausePlayback = pausePlayback;
            if (pausePlayback) PausePlayback();
        }
        else if (_didPausePlayback)
        {
            ResumePlayback();
            _didPausePlayback = false;
        }
        _isBusy = busy;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PausePlayback()
    {
        _wasWallpaperPlaying = false;
        try
        {
            _wasWallpaperPlaying = _wallpaper.IsPlaying;
            if (_wasWallpaperPlaying) _wallpaper.SetTransientPaused(true);
        }
        catch { }
        _suspendedByMe.Clear();
        try
        {
            // Widgets only — boxes stay live (their Suspend also stops file-watcher driven
            // content, which a loading dialog must not disturb).
            foreach (var w in Application.Current.Windows.OfType<WidgetWindow>())
            {
                if (w is not WebWidgetWindow && w is not NativeWidgetWindow) continue;
                try
                {
                    if (!w.IsSuspended)
                    {
                        w.Suspend();
                        _suspendedByMe.Add(w);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private void ResumePlayback()
    {
        try
        {
            foreach (var w in _suspendedByMe)
            {
                try { w.Resume(); } catch { }
            }
        }
        catch { }
        finally
        {
            _suspendedByMe.Clear();
        }
        try
        {
            if (_wasWallpaperPlaying) _wallpaper.SetTransientPaused(false);
        }
        catch { }
        finally
        {
            _wasWallpaperPlaying = false;
        }
    }
}
