namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Application settings: load/save and typed key/value access. Persistence detail is
/// intentionally hidden from the rest of the application.
/// </summary>
public interface ISettingsService
{
    void Load();

    void Save();

    T? GetValue<T>(string key) where T : struct;

    void SetValue<T>(string key, T value);
}
