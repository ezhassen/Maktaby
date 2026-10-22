using Serilog;
using Serilog.Events;
using System.Diagnostics;

namespace Maktaby.Helpers;

/// <summary>
/// One-shot startup phase timing.
/// </summary>
/// <remarks>
/// <para>
/// The repo had no timing instrumentation at all, so every claim about startup cost was an estimate.
/// This records where the time actually goes: one <see cref="LogEventLevel.Information"/> line per
/// phase plus a single summary line listing them all.
/// </para>
/// <para>
/// The anchor is taken in this type's static constructor, and <c>App</c> touches the type from its
/// own static constructor — so the anchor is earlier than any app code runs, but later than the CLR
/// and the runtime itself. The summary therefore also reports the time since
/// <see cref="Process.StartTime"/>, which covers the whole process, for comparison.
/// </para>
/// <para>
/// Writes to the one global logger, and only when it is enabled for
/// <see cref="LogEventLevel.Information"/> — the app's release log level is
/// <see cref="LogEventLevel.Warning"/>, so the timing lines appear once the level is raised.
/// </para>
/// <para>
/// Inactive means inactive: when the level does not allow <see cref="LogEventLevel.Information"/>
/// nothing is anchored, no process start time is read and no line is formatted. The profiler must
/// not cost anything in a build that will not show its output.
/// </para>
/// <para>
/// The anchor is taken at the first mark after <c>Logging.InitializeDefaultLogger</c> has run —
/// before that there is no logger to write to, and reading the level is only meaningful once the
/// global logger is bound. The summary therefore reports the total since *process* start as the
/// headline number, which covers the part before the anchor.
/// </para>
/// <para>
/// Uses the <see cref="Serilog.Log"/> static API on purpose: it <em>is</em> the global logger
/// (<c>Logging.InitializeDefaultLogger</c> binds it, <c>Logging.cs:101</c>), but unlike the
/// <c>Logging.Log</c> property it does not initialize the default file sink as a side effect — the
/// profiler must not create a log file before <c>Logging.InitializeDefaultLogger</c> has run.
/// </para>
/// </remarks>
internal static class StartupProfiler
{
    private static readonly object Gate = new();
    private static readonly List<string> Entries = new();

    private static long _anchorTicks;
    private static long _processStartMs = -1;
    private static int _anchored;

    /// <summary>True when the global logger would actually write an Information line.</summary>
    public static bool Enabled => Log.IsEnabled(LogEventLevel.Information);

    /// <summary>
    /// Captures the start of the measured window. Does nothing when the profiler is disabled, and
    /// only the first call counts.
    /// </summary>
    public static void Anchor()
    {
        if (!Enabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _anchored, 1) == 1)
        {
            return;
        }

        _anchorTicks = Environment.TickCount64;

        try
        {
            using var process = Process.GetCurrentProcess();
            _processStartMs = (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch
        {
            // Reading the process start time can fail under restrictive policies; the anchor-only
            // numbers are still useful.
            _processStartMs = -1;
        }
    }

    /// <summary>Milliseconds since the anchor, or 0 when the profiler never anchored.</summary>
    public static long SinceAnchorMs => _anchorTicks == 0 ? 0 : Environment.TickCount64 - _anchorTicks;

    /// <summary>Records a point in the startup sequence ("the splash is up", "boxes are streamed").</summary>
    public static void Mark(string phase)
    {
        if (!Enabled)
        {
            return;
        }

        Anchor();

        long at = SinceAnchorMs;
        Record($"{phase} +{at}ms");

        Log.Information("Startup: {Phase} at +{Ms}ms", phase, at);
    }

    /// <summary>Times a synchronous step.</summary>
    public static void Measure(string phase, Action action)
    {
        if (!Enabled)
        {
            action();
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            sw.Stop();
            Done(phase, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Times a synchronous step that produces a value.</summary>
    public static T Measure<T>(string phase, Func<T> action)
    {
        if (!Enabled)
        {
            return action();
        }

        var sw = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            sw.Stop();
            Done(phase, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Times an asynchronous step.</summary>
    public static async Task MeasureAsync(string phase, Func<Task> action)
    {
        if (!Enabled)
        {
            await action().ConfigureAwait(true);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            sw.Stop();
            Done(phase, sw.ElapsedMilliseconds);
        }
    }

    private static void Done(string phase, long ms)
    {
        Record($"{phase} {ms}ms");

        Log.Information("Startup: {Phase} took {Ms}ms", phase, ms);
    }

    /// <summary>
    /// Writes the one line to look at: total time, and every phase with its duration and offset.
    /// </summary>
    public static void Summary(string what = "startup")
    {
        if (!Enabled)
        {
            return;
        }

        string phases;
        lock (Gate)
        {
            phases = string.Join(" | ", Entries);
        }

        // The headline is the whole process: the anchor sits after the logger is up, so the phases
        // alone would not show the pre-anchor work.
        long process = _processStartMs >= 0 ? _processStartMs + SinceAnchorMs : SinceAnchorMs;

        Log.Information(
            "Startup: {What} total +{Total}ms since process start (anchor +{Anchor}ms) | {Phases}",
            what,
            process,
            SinceAnchorMs,
            phases);
    }

    private static void Record(string entry)
    {
        lock (Gate)
        {
            Entries.Add(entry);
        }
    }
}
