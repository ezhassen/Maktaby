using Maktaby.Core.Interfaces;
using System.Windows;
using System.Windows.Threading;

namespace Maktaby.Helpers;

/// <summary>
/// Summary:
///   WPF-backed implementation of <see cref="IDispatcher"/> that marshals work onto the application
///   dispatcher. This keeps Core services independent from WPF while ensuring UI-bound code runs on the
///   UI thread without blocking the caller.
///
/// Why the async overloads do the extra await:
///   <see cref="Dispatcher.InvokeAsync(Func{Task})"/> and <see cref="Dispatcher.InvokeAsync(Func{Task{T}})"/>
///   return a dispatcher operation whose result is the inner task. Awaiting only the outer operation would
///   complete before the actual work finishes. We must await the inner task to ensure the call really waits
///   for the UI work to complete.
/// </summary>
/// <remarks>
/// If there's no active <see cref="Application"/> instance yet, the work runs inline because there is
/// no UI thread to marshal onto. In normal app execution we always have a dispatcher and the calls below
/// make sure UI-bound code stays on the correct thread without blocking the caller.
/// </remarks>
public sealed class WpfDispatcher : IDispatcher
{
    private static Dispatcher? GetDispatcher()
    {
        return Application.Current?.Dispatcher;
    }

    /// <inheritdoc />
    public void BeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // BeginInvoke is intentionally fire-and-forget: the caller does not wait for completion.
            _ = dispatcher.BeginInvoke(action);
            return;
        }

        action();
    }

    /// <inheritdoc />
    public void BeginInvoke(Func<Task> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // This keeps the async callback as a Task-returning delegate, rather than an async void lambda,
            // which preserves exception flow and keeps the fire-and-forget semantics explicit.
            _ = dispatcher.BeginInvoke(asyncAction);
            return;
        }

        _ = asyncAction();
    }

    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Invoke waits for the delegate to finish. This is the synchronous variant: callers expect the
            // UI work to complete before returning.
            dispatcher.Invoke(action);
            return;
        }

        action();
    }

    /// <inheritdoc />
    public T Invoke<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Invoke blocks until the delegate completes, which is correct for synchronous callers
            // that expect a result from the UI thread.
            return dispatcher.Invoke(func);
        }

        return func();
    }

    /// <inheritdoc />
    public async Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(action);
            return;
        }

        action();
    }

    /// <inheritdoc />
    public async Task InvokeAsync(Func<Task> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Dispatcher.InvokeAsync(Func<Task>) returns a DispatcherOperation whose Result is the inner task.
            // Await the operation's Task first, then await the actual async work so this method waits for the
            // full completion instead of finishing at the outer dispatch boundary.
            var inner = await dispatcher.InvokeAsync(asyncAction).Task.ConfigureAwait(false);
            await inner.ConfigureAwait(false);
            return;
        }

        await asyncAction().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction)
    {
        ArgumentNullException.ThrowIfNull(asyncAction);

        var dispatcher = GetDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Same pattern as InvokeAsync(Func<Task>): the operation we receive from the dispatcher contains
            // the inner task, so we must await the Task property first and then unwrap the result.
            var inner = await dispatcher.InvokeAsync(asyncAction).Task.ConfigureAwait(false);
            return await inner.ConfigureAwait(false);
        }

        return await asyncAction().ConfigureAwait(false);
    }
}
