using System.Windows;
using WPFShared.Controls;
using WPFShared.ViewModels;

namespace WPFShared.Helpers;

/// <summary>
/// Global unhandled-exception wiring shared by WPF hosts. One call wires the three sources:
/// <see cref="Application.DispatcherUnhandledException"/>, <see cref="AppDomain.UnhandledException"/>
/// and <see cref="TaskScheduler.UnobservedTaskException"/>.
/// Host-specific policy (what counts as ignorable, where to log) stays with the host via
/// <see cref="Options"/> — this class owns only the mechanics plus the Continue/Exit dialog contract:
/// only Exit Application shuts down; Continue and any system close just dismiss the dialog.
/// </summary>
public static class ExceptionHandler
{
    public sealed class Options
    {
        /// <summary>True = swallow silently (mark handled, no dialog). E.g. known shutdown races.</summary>
        public Func<Exception?, bool>? IsIgnorable { get; set; }

        public Action<Exception?, string>? LogError { get; set; }
        public Action<Exception?, string>? LogFatal { get; set; }
        public Action<string>? LogWarning { get; set; }

        /// <summary>False = log only, never show the dialog.</summary>
        public bool ShowDialog { get; set; } = true;
    }

    private static void InvokeLogError(Options? options, Exception? ex, string msg)
    {
        if (options?.LogError is not null)
        {
            options.LogError(ex, msg);
        }
        else
        {
            Serilog.Log.Error(ex, msg);
        }
    }

    private static void InvokeLogFatal(Options? options, Exception? ex, string msg)
    {
        if (options?.LogFatal is not null)
        {
            options.LogFatal(ex, msg);
        }
        else
        {
            Serilog.Log.Fatal(ex, msg);
        }
    }

    private static void InvokeLogWarning(Options? options, string msg)
    {
        if (options?.LogWarning is not null)
        {
            options.LogWarning(msg);
        }
        else
        {
            Serilog.Log.Warning(msg);
        }
    }

    /// <summary>Wires the three global handlers on <paramref name="app"/>. Returns an
    /// <see cref="IDisposable"/> that unwires them.</summary>
    public static IDisposable Register(Application app, Options? options = null)
    {
        options ??= new Options();

        void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                if (options.IsIgnorable?.Invoke(e.Exception) == true)
                {
                    e.Handled = true;
                    return;
                }
            }
            catch { }
            try { InvokeLogError(options, e.Exception, "UnhandledException"); } catch { }
            e.Handled = true;
            if (!options.ShowDialog) return;
            var exception = e.Exception;
            try
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var vm = new WindowExceptionHandlerViewModel(exception);
                        var window = new WindowExceptionHandler { DataContext = vm };
                        window.ShowDialog();
                        // Window owns the shutdown decision: only Exit Application shuts down;
                        // Continue and any system close (X / Alt+F4) just dismiss the dialog.
                        if (vm.HasChosenContinue)
                        {
                            try { InvokeLogWarning(options, "User chose to continue after unhandled exception"); } catch { }
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            // No dialog here: the runtime may be terminating and UI is unreliable.
            try { InvokeLogFatal(options, e.ExceptionObject as Exception, $"CurrentDomain_UnhandledException IsTerminating={e.IsTerminating}"); } catch { }
        }

        void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try { InvokeLogError(options, e.Exception, "UnobservedTaskException"); } catch { }
            try { e.SetObserved(); } catch { }
        }

        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        return new Unregister(app, new System.Windows.Threading.DispatcherUnhandledExceptionEventHandler(OnDispatcherUnhandledException), OnDomainUnhandledException, OnUnobservedTaskException);
    }

    private sealed class Unregister(
        Application app,
        System.Windows.Threading.DispatcherUnhandledExceptionEventHandler dispatcher,
        UnhandledExceptionEventHandler domain,
        EventHandler<UnobservedTaskExceptionEventArgs> unobserved) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { app.DispatcherUnhandledException -= dispatcher; } catch { }
            try { AppDomain.CurrentDomain.UnhandledException -= domain; } catch { }
            try { TaskScheduler.UnobservedTaskException -= unobserved; } catch { }
        }
    }
}
