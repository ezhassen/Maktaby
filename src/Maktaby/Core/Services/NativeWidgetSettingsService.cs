using Maktaby.WidgetSdk;
using Maktaby.Core.Interfaces;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Maktaby.Core.Services;

/// <inheritdoc cref="INativeWidgetSettingsService"/>
public sealed class NativeWidgetSettingsService : INativeWidgetSettingsService
{
    private const string FileName = "settings.json";
    private const string AppSettingsDirName = "WidgetSettings";

    public string GetSettingsPath(NativeWidgetInfo info)
    {
        if (info.Source == NativeWidgetSource.App)
        {
            return Path.Combine(SettingsService.AppDataDir, AppSettingsDirName, SanitizeSlug(info.Slug) + ".json");
        }

        string folder = string.IsNullOrWhiteSpace(info.FolderPath)
            ? Path.Combine(SettingsService.AppDataDir, AppSettingsDirName, SanitizeSlug(info.Slug))
            : info.FolderPath;
        return Path.Combine(folder, FileName);
    }

    public void LoadInto(NativeWidgetInfo info, IWidgetSettingsProvider provider)
    {
        string path = GetSettingsPath(info);
        if (!File.Exists(path))
        {
            // First run: materialise the file with defaults so users can discover/edit it.
            try { Save(info, provider); } catch { }
            return;
        }

        Dictionary<string, JsonElement>? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
        }
        catch
        {
            return;
        }

        if (stored is null) return;
        var byName = stored.ToDictionary(kv => kv.Key, kv => kv.Value,
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var setting in provider.Settings)
        {
            if (!byName.TryGetValue(setting.Name, out var el)) continue;
            try
            {
                setting.Value = setting.Kind switch
                {
                    WidgetSettingKind.String or WidgetSettingKind.ListOfStrings
                        => el.ValueKind == JsonValueKind.Null ? null : el.GetString(),
                    WidgetSettingKind.Number => el.ValueKind == JsonValueKind.Number ? el.GetDouble()
                        : double.Parse(el.GetString() ?? "", CultureInfo.InvariantCulture),
                    WidgetSettingKind.Boolean => el.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? el.GetBoolean()
                        : bool.Parse(el.GetString() ?? ""),
                    _ => setting.Value,
                };
            }
            catch
            {
                // Mismatched stored value: keep the declared default.
            }
        }
    }

    public void Save(NativeWidgetInfo info, IWidgetSettingsProvider provider)
    {
        string path = GetSettingsPath(info);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var map = new Dictionary<string, object?>();
            foreach (var setting in provider.Settings)
            {
                map[setting.Name] = setting.Value;
            }

            File.WriteAllText(path, JsonSerializer.Serialize(map,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: a failed save must never break the widget.
        }
    }

    private static string SanitizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "widget";
        var sb = new System.Text.StringBuilder();
        foreach (char c in slug.Trim())
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c);
        }

        var s = sb.ToString();
        if (s.Length > 64) s = s[..64];
        return string.IsNullOrWhiteSpace(s) ? "widget" : s;
    }
}
