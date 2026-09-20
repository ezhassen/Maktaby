using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.Helpers;
using DesktopBoxesUI.Settings;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Exceptions;
using System.IO;
using ThreadingTimer = System.Threading.Timer;

namespace DesktopBoxesUI;

public static class Logging
{
#if DEBUG
    public const Serilog.Events.LogEventLevel DefaultLogEventLevel = Serilog.Events.LogEventLevel.Debug;
#else
    public const Serilog.Events.LogEventLevel DefaultLogEventLevel = Serilog.Events.LogEventLevel.Warning;
#endif
    private static LoggingLevelSwitch? levelSwitch;
    public static LoggingLevelSwitch LevelSwitch
    {
        get
        {
            if (levelSwitch is null) ReReadLevelSwitch();
            return levelSwitch!;
        }
    }

    static Logger? _log;

    /// <summary>
    /// Default main logger that Writes to file log with name "ElForsan{Day}.log" in "%TEMP%\ElForsan_Logs\\" Folder
    /// </summary>
    public static Logger Log
    {
        get
        {
            if (_log is null) InitializeDefaultLogger();
            return _log!;
        }
    }

    public static void Log_Error(this Exception ex, string? title = null)
    {
        if (string.IsNullOrEmpty(title))
        {
            Logging.Log?.Error(ex, ex.Message);
        }
        else
        {
            Logging.Log?.Error(ex, $"{title} : {{Message}}", ex.Message);
            //Logging.Log?.Error(ex, "Error in exception handler: {Message}", ex.Message);

        }
    }

    public static string LogsFolder
    {
        get
        {
            //var folderPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Logs\\");
            var folderPath = Path.Combine(Path.GetTempPath(), "DesktopBoxes\\");
            if (!Directory.Exists(folderPath))
            {
                //DirectoryEx.CreateDirectoryWithFullControl(folderPath);
                Directory.CreateDirectory(folderPath);
            }

            return folderPath;
        }
    }

    public static Logger GetNewLogger(string fileName, LoggingLevelSwitch _LevelSwitch, RollingInterval rollingInterval = RollingInterval.Day)
    {
        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_LevelSwitch)
            .Enrich.FromLogContext()
            .Enrich.WithExceptionDetails()
            .WriteTo.File(Path.Combine(LogsFolder, fileName), shared: true, rollingInterval: rollingInterval)
#if DEBUG
            .WriteTo.Console()
#endif
            //.WriteTo.File(new JsonFormatter(renderMessage: true), Path.Combine(LogsFolder, fileName), shared: true, rollingInterval: RollingInterval.Day)
            //.WriteTo.File(new CompactJsonFormatter(), Path.Combine(LogsFolder, fileName), shared: true, rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    private static FileSystemWatcher? _watcher;

    public static void InitializeDefaultLogger()
    {
        // Initialize the default loggers
        if (_log is null)
        {
            _log = GetNewLogger("DesktopBoxes.log", LevelSwitch);
            Serilog.Log.Logger = _log;
            if (_watcher is null)
            {
                // Setup file watcher to reload settings on change
                _watcher = new FileSystemWatcher(SettingsService.AppDataDir, AppJSettings.FileJsonName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                _watcher.Changed += (s, e) => OnSettingsChanged(s, e);
                _watcher.Created += (s, e) => OnSettingsChanged(s, e);
                _watcher.Renamed += (s, e) => OnSettingsChanged(s, e);
                _watcher.Deleted += (s, e) =>
                {
                    _watcher?.Dispose();
                    _watcher = null;
                };
                _watcher.EnableRaisingEvents = true;
            }
            if (_log.IsEnabled(Serilog.Events.LogEventLevel.Information)) _log.Information("Default Logger Initialized");
        }
        //if (Serilog.Log.Logger != _log) Serilog.Log.Logger = _log;
    }
    private static ThreadingTimer? _debounce;
    private static void OnSettingsChanged(object sender, FileSystemEventArgs e)
    {
        _debounce?.Dispose();
        _debounce = new ThreadingTimer(_ =>
        {
            ReReadLevelSwitch();
        }, null, 300, Timeout.Infinite);
    }
    public static void ReReadLevelSwitch()
    {
        // Re-read the log level switch from the configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(SettingsService.AppDataDir)
            .AddJsonFile(AppJSettings.FileJsonName, optional: true, reloadOnChange: false)//using FileSystemWatcher instead
            .Build();
        //var logLevelStr = config.GetSection("Serilog:MinimumLevel").Value ?? "Warning";
        var logLevelStr = config.GetSection(nameof(AppJSettings.LogEventLevel))?.Value ?? "Warning";
        if (levelSwitch is null) levelSwitch = new LoggingLevelSwitch();
        if (JsonHelper.TryGetLogEventLevelFromString(logLevelStr, out var logLevel))
        {
            levelSwitch.MinimumLevel = logLevel;
        }
        else
        {
            levelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Warning;
        }
    }

    public static void OpenLogsFolderInExplorer()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
        {
            FileName = LogsFolder,
            UseShellExecute = true,
            Verb = "open"
        });
    }

    /// <summary>
    /// Close and flush the logging pipeline of this <see cref="Log"/>.
    /// </summary>
    public static void DisposeAllDefaultLoggers()
    {
        if (_log?.IsEnabled(Serilog.Events.LogEventLevel.Information) == true) _log.Information("Default Logger Ended");
        _log?.Dispose();
        _log = null;
        _watcher?.Dispose();
        _watcher = null;
        //Serilog.Log.Logger = null; // Clear the static logger reference
    }
}
