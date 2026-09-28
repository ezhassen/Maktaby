using Maktaby.Core.Interfaces;
using System;
using System.Windows;

namespace Maktaby.Helpers;

/// <summary>
/// <see cref="IDispatcher"/> backed by the WPF <see cref="Application.Dispatcher"/>. Posts the work
/// (BeginInvoke) so the calling (watcher) thread is never blocked waiting on the UI thread.
/// </summary>
public sealed class WpfDispatcher : IDispatcher
{
    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
    public async Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(action);
        }
        else
        {
            action();
        }
    }
    public async Task InvokeAsync(Func<Task> asyncAction)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(asyncAction);
        }
        else
        {
            await asyncAction();
        }
    }
}
