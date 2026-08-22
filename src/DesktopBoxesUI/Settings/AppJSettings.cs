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

        _Instance = JsonHelper.DeserializeFromFile<AppJSettings>(FilePath);
        bool save = false;
        if (_Instance.AppIdentity == Guid.Empty)
        {
            _Instance.AppIdentity = Guid.NewGuid();//Generate a New guid for current app user
            save = true;
        }
        if (!_Instance.LogEventLevel.HasValue)
        {
            _Instance.LogEventLevel = Logging.DefaultLogEventLevel; //Default log level
            save = true;
        }
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