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

    /// <summary>Prefix and extension a generated per-instance name must have.</summary>
    private const string InstancePrefix = "settings_";
    private const int InstanceTokenLength = 8;

    public string GetSettingsPath(NativeWidgetInfo info, string? settingsFileName)
    {
        string? name = SanitizeSettingsFileName(settingsFileName) ?? FileName;

        if (info.Source == NativeWidgetSource.App)
        {
            // One folder per plugin so a folder can hold the default file plus its instances, exactly
            // like a user-source widget folder does.
            string slugFolder = Path.Combine(SettingsService.AppDataDir, AppSettingsDirName, SanitizeSlug(info.Slug));
            MigrateLegacyAppFile(slugFolder);
            return Path.Combine(slugFolder, name);
        }

        string folder = string.IsNullOrWhiteSpace(info.FolderPath)
            ? Path.Combine(SettingsService.AppDataDir, AppSettingsDirName, SanitizeSlug(info.Slug))
            : info.FolderPath;
        return Path.Combine(folder, name);
    }

    /// <summary>
    /// Moves the pre-per-instance flat <c>WidgetSettings/{slug}.json</c> to
    /// <c>WidgetSettings/{slug}/settings.json</c> the first time that plugin is resolved. Without it
    /// every built-in widget would silently fall back to declared defaults on upgrade.
    /// </summary>
    private static void MigrateLegacyAppFile(string slugFolder)
    {
        try
        {
            string target = Path.Combine(slugFolder, FileName);
            if (File.Exists(target)) return;

            string legacy = Path.Combine(Path.GetDirectoryName(slugFolder)!, Path.GetFileName(slugFolder) + ".json");
            if (!File.Exists(legacy)) return;

            Directory.CreateDirectory(slugFolder);
            File.Move(legacy, target);
        }
        catch
        {
            // Migration is a convenience: a locked or already-moved file just means the defaults win.
        }
    }

    /// <summary>
    /// Accepts only a bare generated instance name (<c>settings_xxxxxxxx.json</c>). The value round
    /// trips through the snapshot file, so it is untrusted input on its way to becoming a path -
    /// anything carrying a separator, "..", or a rooted path falls back to the default file.
    /// </summary>
private static string? SanitizeSettingsFileName(string? settingsFileName)
    {
        if (string.IsNullOrWhiteSpace(settingsFileName)) return null;
        return IsInstanceFileName(settingsFileName.Trim()) ? settingsFileName.Trim() : null;
    }

    /// <summary>True only for a bare generated <c>settings_{8 hex}.json</c> name.</summary>
    private static bool IsInstanceFileName(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.Contains('/') || name.Contains('\\')) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (!name.StartsWith(InstancePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

        string token = name[InstancePrefix.Length..^".json".Length];
        if (token.Length != InstanceTokenLength) return false;
        foreach (char c in token)
        {
            if (!char.IsAsciiDigit(c) && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
        }

        return true;
    }

    public void LoadInto(NativeWidgetInfo info, IWidgetSettingsProvider provider, string? settingsFileName)
    {
        string path = GetSettingsPath(info, settingsFileName);
        if (!File.Exists(path))
        {
            // First run: materialise the file with defaults so users can discover/edit it.
            try { Save(info, provider, settingsFileName); } catch { }
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

    public void Save(NativeWidgetInfo info, IWidgetSettingsProvider provider, string? settingsFileName)
    {
        string path = GetSettingsPath(info, settingsFileName);
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

    /// <summary>
    /// Deletes the settings file of a single removed instance. A null
    /// <paramref name="settingsFileName"/> is deliberately a no-op: that instance shares the
    /// plugin's default file, which belongs to the remaining first instance, not to the one being
    /// deleted.
    /// </summary>
    public void Delete(NativeWidgetInfo info, string? settingsFileName)
    {
        var name = SanitizeSettingsFileName(settingsFileName);
        if (name is null) return;

        try
        {
            string path = GetSettingsPath(info, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort: an undeletable settings file must never block removing the widget.
        }
    }

    /// <summary>Copies one instance's settings file onto another, for the duplicate migration.
    /// A null <paramref name="fromFileName"/> means the shared default file, which is exactly what
    /// the migration copies from.</summary>
    public void CopySettings(NativeWidgetInfo info, string? fromFileName, string? toFileName)
    {
        if (SanitizeSettingsFileName(toFileName) is not { } to) return;

        try
        {
            string source = GetSettingsPath(info, fromFileName);
            string target = GetSettingsPath(info, to);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // No source file: leave the absence in place. LoadInto materialises the declared
            // defaults on first attach, which is the same end state.
            if (File.Exists(source)) File.Copy(source, target, overwrite: true);
        }
        catch
        {
            // Best effort, same contract as Save.
        }
    }

    /// <summary>
    /// Removes per-instance settings files no placed instance points at any more. Both roots are
    /// scanned: the app-source <c>WidgetSettings/{slug}/</c> folders and the user-source
    /// <c>UserWidgets/{slug}/</c> folders (in the latter only files matching the generated name
    /// pattern are considered, so authored widget sources are never touched).
    /// </summary>
    public int DeleteUnreferencedFiles(IEnumerable<string> referencedFileNames)
    {
        var referenced = new HashSet<string>(referencedFileNames, StringComparer.OrdinalIgnoreCase);
        int removed = 0;

        foreach (string root in new[]
                 {
                     Path.Combine(SettingsService.AppDataDir, AppSettingsDirName),
                     Path.Combine(SettingsService.AppDataDir, "UserWidgets"),
                 })
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (string slugFolder in Directory.EnumerateDirectories(root))
                {
                    foreach (string file in Directory.EnumerateFiles(slugFolder, "settings_*.json"))
                    {
                        string name = Path.GetFileName(file);
                        if (!IsInstanceFileName(name)) continue;
                        if (referenced.Contains(name)) continue;

                        try { File.Delete(file); removed++; }
                        catch { /* a locked file stays until the next run */ }
                    }
                }
            }
            catch
            {
                // Best effort: one unreadable root must not stop the others.
            }
        }

        return removed;
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