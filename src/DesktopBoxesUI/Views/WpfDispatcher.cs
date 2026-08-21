using System;
using System.Windows;
using DesktopBoxesUI.Core.Interfaces;

namespace DesktopBoxesUI.Views;

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
}
