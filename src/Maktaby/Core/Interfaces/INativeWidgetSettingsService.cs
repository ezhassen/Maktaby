using Maktaby.WidgetSdk;

namespace Maktaby.Core.Interfaces;

/// <summary>Persists native widget <see cref="WidgetSetting"/> values as JSON name→value maps.
/// One folder per plugin holds <c>settings.json</c> — shared by the first instance of that slug —
/// plus a <c>settings_{token}.json</c> per additional placed instance. Read-only app widgets use
/// <c>%LocalAppData%\Maktaby\WidgetSettings\{slug}\</c>; user widgets use their own folder under
/// <c>UserWidgets\{slug}\</c>. Files are created on first save (or first load with defaults).
/// Unknown names and mismatched values are ignored on load. All members are exception-safe (best
/// effort, logged) and run on the UI thread.</summary>
public interface INativeWidgetSettingsService
{
    /// <summary>Settings file path for this instance (parent dir may not exist yet).
    /// <paramref name="settingsFileName"/> is the owning container's
    /// <c>DesktopItemContainer.SettingsFileName</c>; null means the shared default file.</summary>
    string GetSettingsPath(NativeWidgetInfo info, string? settingsFileName);

    /// <summary>Applies stored values onto <paramref name="provider"/>'s settings (raises
    /// <see cref="WidgetSetting.ValueChanged"/> per applied value). Creates the file with
    /// defaults when missing.</summary>
    void LoadInto(NativeWidgetInfo info, IWidgetSettingsProvider provider, string? settingsFileName);

    /// <summary>Writes current values to disk (creates directories as needed).</summary>
    void Save(NativeWidgetInfo info, IWidgetSettingsProvider provider, string? settingsFileName);

    /// <summary>Deletes one removed instance's file. A null
    /// <paramref name="settingsFileName"/> is a no-op - that instance shares the default file, which
    /// belongs to the remaining first instance.</summary>
    void Delete(NativeWidgetInfo info, string? settingsFileName);

    /// <summary>Copies one instance's settings file onto another. Used by the duplicate migration so
    /// existing instances keep the values they had been sharing.</summary>
    void CopySettings(NativeWidgetInfo info, string? fromFileName, string? toFileName);

    /// <summary>Deletes per-instance settings files (<c>settings_{token}.json</c>) that no placed
    /// instance references any more — the leftovers of removed instances and of plugins that are no
    /// longer installed. The shared <c>settings.json</c> and every non-settings file in a widget
    /// folder are left alone. Returns how many were removed.</summary>
    int DeleteUnreferencedFiles(IEnumerable<string> referencedFileNames);
}