using Maktaby.WidgetSdk;

namespace Maktaby.Core.Interfaces;

/// <summary>Persists native widget <see cref="WidgetSetting"/> values as JSON name→value maps.
/// User widgets keep <c>settings.json</c> in their own folder; read-only app widgets use
/// <c>%LocalAppData%\Maktaby\WidgetSettings\{slug}.json</c>. Files are created on first
/// save (or first load with defaults). Unknown names and mismatched values are ignored on load.
/// All members are exception-safe (best effort, logged) and run on the UI thread.</summary>
public interface INativeWidgetSettingsService
{
    /// <summary>Settings file path for <paramref name="info"/> (parent dir may not exist yet).</summary>
    string GetSettingsPath(NativeWidgetInfo info);

    /// <summary>Applies stored values onto <paramref name="provider"/>'s settings (raises
    /// <see cref="WidgetSetting.ValueChanged"/> per applied value). Creates the file with
    /// defaults when missing.</summary>
    void LoadInto(NativeWidgetInfo info, IWidgetSettingsProvider provider);

    /// <summary>Writes current values to disk (creates directories as needed).</summary>
    void Save(NativeWidgetInfo info, IWidgetSettingsProvider provider);
}
