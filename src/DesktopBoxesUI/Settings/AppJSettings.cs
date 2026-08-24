using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.Helpers;
using System.IO;

namespace DesktopBoxesUI.Settings;

public class AppJSettings
{
    static AppJSettings? _Instance;

    public static AppJSettings Instance
    {
        get
        {
            if (_Instance is null)
            {
                Reload();
                //_Instance = new AppJSettings();
                if (_Instance is null) _Instance = new() { LogEventLevel = Logging.DefaultLogEventLevel };
            }
            return _Instance;
        }
        //set { _Instance = value; }
    }
    public static string FileJsonName => "AppJSettings.json";
    public static string FilePath => Path.Combine(SettingsService.AppDataDir, FileJsonName);

    /// <summary>
    /// A unique application guid per user machine 
    /// </summary>
    public Guid AppIdentity { get; set; }

    /// <summary>
    /// When true the app registers itself in the current user's Run key so it launches on Windows
    /// startup. The actual Run-key entry is managed by <see cref="Win32APIs.Services.StartupManager"/>;
    /// this only persists the user's preference.
    /// </summary>
    public bool LaunchOnStartup { get; set; }

    //public string? Notes { get; set; }

    public Serilog.Events.LogEventLevel? LogEventLevel { get; set; }

    public static void Reload()
    {
        //RegisterJsonConverters();
        if (!File.Exists(FilePath))
        {
            _Instance = new AppJSettings()
            {
                AppIdentity = Guid.NewGuid(),
                LogEventLevel = Logging.DefaultLogEventLevel
            };
            _Instance.Save();
            return;
        }

        var _appJS = JsonHelper.DeserializeFromFile<AppJSettings>(FilePath);
        if (_appJS is null) throw new InvalidOperationException("Cannot load AppJSettings");
        bool save = false;
        if (_appJS.AppIdentity == Guid.Empty)
        {
            _appJS.AppIdentity = Guid.NewGuid();//Generate a New guid for current app user
            save = true;
        }
        if (!_appJS.LogEventLevel.HasValue)
        {
            _appJS.LogEventLevel = Logging.DefaultLogEventLevel; //Default log level
            save = true;
        }
        _Instance = _appJS;
        if (save) _Instance.Save();
    }

    public void Save()
    {
        JsonHelper.SafeWriteJson(FilePath, this);
    }
    //private static void RegisterJsonConverters()
    //{

    //}
}