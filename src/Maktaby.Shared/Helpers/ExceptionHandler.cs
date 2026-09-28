using System.Windows;
using Maktaby.Shared.Controls;
using Maktaby.Shared.ViewModels;

namespace Maktaby.Shared.Helpers;

/// <summary>
/// Global unhandled-exception wiring shared by WPF hosts. One call wires the three sources:
/// <see cref="Application.DispatcherUnhandledException"/>, <see cref="AppDomain.UnhandledException"/>
/// and <see cref="TaskScheduler.UnobservedTaskException"/>.
/// Host-specific policy (what counts as ignorable, where to log) stays with the host via
/// <see cref="Options"/> — this class owns only the mechanics plus the Continue/Exit dialog contract:
/// only Exit Application shuts down; Continue and any system close just dismiss the dialog.
/// The dialog is single-flight: while one is open (or inside the duplicate cooldown after
/// one closed), identical exceptions are marked handled, counted, and logged once as a summary
/// instead of stacking a dialog per exception — under OutOfMemory each dialog and each full
/// log line would otherwise feed the storm that caused it.
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

        /// <summary>False = log only, never show the dialog (storm suppression is skipped too).</summary>
        public bool ShowDialog { get; set; } = true;

        /// <summary>Invoked once on the UI thread before the first dialog of a storm is shown.
        /// Hosts pause media/render churn here (live wallpaper, widgets) to stop feeding the
        /// storm. Must be allocation-light and never throw — under OutOfMemory every
        /// allocation is suspect (calls are still guarded). Null = no suspension.</summary>
        public Action<Exception?>? SuspendApp { get; set; }

        /// <summary>Invoked on the UI thread after the dialog closes: (exception, continued).
        /// Hosts may resume what <see cref="SuspendApp"/> paused — under OutOfMemory staying
        /// suspended is safer, since resuming usually re-triggers the storm.</summary>
        public Action<Exception?, bool>? DialogClosed { get; set; }

        /// <summary>Cooldown after a dialog was shown/closed during which an identical
        /// exception (type + message) opens no new dialog; hits are counted and logged once
        /// as a summary. Zero disables. Default 30 s.</summary>
        public TimeSpan DuplicateCooldown { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>Storm gate: at most one dialog at a time, plus a duplicate cooldown after it
    /// closes. Static (per process): concurrent storms from any thread serialize here.</summary>
    private static readonly object _stormGate = new();
    private static bool _dialogOpen;
    private static string? _lastSignature;
    private static DateTime _lastSignatureUtc = DateTime.MinValue;
    private static int _suppressedCount;

    private static string Signature(Exception? ex)
    {
        try { return ex is null ? "<null>" : ex.GetType().FullName + "|" + ex.Message; }
        catch { return "<unknown>"; }
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
            if (!options.ShowDialog)
            {
                try { InvokeLogError(options, e.Exception, "UnhandledException"); } catch { }
                e.Handled = true;
                return;
            }
            // Single-flight + duplicate cooldown: a dialog per exception turns a fault storm
            // (e.g. OutOfMemory from every window message) into dozens of stacked modal
            // dialogs plus a flooded log, each allocating into the exhaustion. Suppressed
            // hits are counted and reported once as a summary when the dialog closes.
            bool show;
            lock (_stormGate)
            {
                if (_dialogOpen)
                {
                    _suppressedCount++;
                    show = false;
                }
                else
                {
                    var sig = Signature(e.Exception);
                    if (sig == _lastSignature
                        && options.DuplicateCooldown > TimeSpan.Zero
                        && DateTime.UtcNow - _lastSignatureUtc < options.DuplicateCooldown)
                    {
                        _suppressedCount++;
                        show = false;
                    }
                    else
                    {
                        _lastSignature = sig;
                        _lastSignatureUtc = DateTime.UtcNow;
                        show = true;
                    }
                }
            }
            if (!show)
            {
                e.Handled = true;
                return;
            }
            try { InvokeLogError(options, e.Exception, "UnhandledException"); } catch { }
            try { options.SuspendApp?.Invoke(e.Exception); } catch { }
            e.Handled = true;
            var exception = e.Exception;
            // Claimed before BeginInvoke so exceptions arriving before the dialog shows
            // (or while it is open) suppress instead of queueing behind it.
            lock (_stormGate) { _dialogOpen = true; }
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
                        bool continued = vm.HasChosenContinue;
                        if (continued)
                        {
                            try { InvokeLogWarning(options, "User chose to continue after unhandled exception"); } catch { }
                        }
                        try { options.DialogClosed?.Invoke(exception, continued); } catch { }
                    }
                    catch { }
                    finally
                    {
                        int suppressed;
                        lock (_stormGate)
                        {
                            _dialogOpen = false;
                            _lastSignatureUtc = DateTime.UtcNow;
                            suppressed = _suppressedCount;
                            _suppressedCount = 0;
                        }
                        if (suppressed > 0)
                        {
                            try { InvokeLogWarning(options, $"Suppressed {suppressed} duplicate unhandled exception(s) while the error dialog was open"); } catch { }
                        }
                    }
                }));
            }
            catch
            {
                lock (_stormGate) { _dialogOpen = false; }
            }
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
