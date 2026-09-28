using Maktaby.Core.Interfaces;
using Maktaby.Helpers;
using Maktaby.Settings;
using System.IO;
using System.Text.Json;

namespace Maktaby.Core.Services;

/// <summary>
/// Volatile, in-memory settings implementation. Persistence to disk/json will replace
/// this later; the rest of the app only depends on <see cref="ISettingsService"/>.
/// </summary>
public sealed class SettingsService : ISettingsService
{
#if DEBUG
    //Use def app dir to store data for debugging.
    public static string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maktaby\\Debug");
#else
    public static string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maktaby");
#endif

    private static readonly string _filePath = Path.Combine(AppDataDir, "UserSettings.json");
    private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public UserSettings UserSettings { get; set; } = new UserSettings();
    public AppJSettings AppJSettings => AppJSettings.Instance;

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            UserSettings = new UserSettings();
            Save();
            return;
        }

        //try
        //{
        using var stream = File.OpenRead(_filePath);
        var desRes = JsonHelper.Deserialize<UserSettings>(stream);
        if (desRes is not null) UserSettings = desRes;
        //}
        //catch
        //{
        //    return null;
        //}
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
        JsonHelper.SerializeToFile(_filePath, UserSettings);
    }

    //public T? GetValue<T>(string key) where T : struct
    //{
    //    if (_values.TryGetValue(key, out var value) && value is T typed)
    //    {
    //        return typed;
    //    }

    //    return default;
    //}

    //public void SetValue<T>(string key, T value)
    //{
    //    _values[key] = value;
    //}
}
