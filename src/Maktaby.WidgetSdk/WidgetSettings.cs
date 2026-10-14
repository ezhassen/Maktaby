using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Maktaby.WidgetSdk;

/// <summary>Value kind of a <see cref="WidgetSetting"/>. Fixed set so hosts can render a matching
/// editor (text box, number box, check box) without reflection.</summary>
public enum WidgetSettingKind
{
    String,
    Number,
    Boolean,
    ListOfStrings,

    /// <summary>A colour stored as an <c>#AARRGGBB</c> hex string (alpha first). A separate kind
    /// rather than <see cref="String"/> so the host can render a colour picker instead of a
    /// text box, and so a malformed value can be rejected on write rather than silently
    /// round-tripping as text. Serializes as a string either way, so persistence is unchanged.
    /// </summary>
    ColorH,

    /// <summary>Widget theme. Value is one of the <see cref="ThemeKeys"/>; the host resolves
    /// it and pushes the result through <c>INativeWidget.ApplyTheme</c>. Declared automatically
    /// by <see cref="NativeWidgetControl"/> (first in the list) and rendered only when the
    /// manifest sets <c>supportsTheme</c>, so a widget never has to declare it by hand.</summary>
    Theme,
}

/// <summary>Valid values for a <see cref="WidgetSettingKind.Theme"/> setting. The empty
/// string means "follow the app" and is the default.</summary>
public static class ThemeKeys
{
    public const string FollowApp = "";
    public const string Light = "light";
    public const string Dark = "dark";
    /// <summary>"Derive it from my own background". Only meaningful for a widget that paints a
    /// user-choosable background. The host does NOT push this string through as a theme: it
    /// resolves the app theme as usual and the widget overrides it in <c>ApplyTheme</c> when
    /// (and only when) it has a user colour to derive from — otherwise the derivation is
    /// circular, because the unset background is itself theme-derived.</summary>
    public const string Auto = "auto";

    public static bool IsValid(string? key)
        => key is FollowApp or Light or Dark or Auto;

    /// <summary>True when the key names a concrete theme, i.e. the host should not override it.</summary>
    public static bool IsExplicit(string? key)
        => key is Light or Dark or Auto;
}

/// <summary>A single user-editable setting of a native widget: a <see cref="Name"/>d value with a
/// human-readable <see cref="Description"/>. Values are <see cref="string"/>, <see cref="double"/>
/// or <see cref="bool"/> depending on <see cref="Kind"/>; <see cref="Value"/> validates on write.
/// All members run on the UI thread (same contract as <see cref="INativeWidget"/>).</summary>
public sealed class WidgetSetting : IDisposable
{
    private object? _value;

    public WidgetSetting(string name, string displayName, string description, WidgetSettingKind kind, Action onValueChanged, object? defaultValue = null, Dictionary<string, string>? listOfAvailableStrings = null, double? minimum = null, double? maximum = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new System.ArgumentException("Setting name must not be empty.", nameof(name));
        }
        if (kind == WidgetSettingKind.ListOfStrings && (listOfAvailableStrings is null || listOfAvailableStrings.Count == 0))
        {
            throw new InvalidOperationException("ListOfStrings settings require a non-empty available-strings map.");
        }

        Name = name;
        Description = description ?? "";
        Kind = kind;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
        ListOfAvailableStrings = listOfAvailableStrings;
        Minimum = minimum;
        Maximum = maximum;
        DefaultValue = Coerce(defaultValue, kind, nameof(defaultValue), listOfAvailableStrings);
        _value = DefaultValue;
        if (onValueChanged is not null) this.ValueChanged += (_, _) => { onValueChanged(); };
    }

    /// <summary>Optional inclusive lower bound of a <see cref="WidgetSettingKind.Number"/> setting.
    /// Null by default: with no range the host renders a numeric field, and only when BOTH
    /// bounds are supplied does it render a slider. Ignored by other kinds.</summary>
    public double? Minimum { get; }

    /// <summary>Optional inclusive upper bound of a <see cref="WidgetSettingKind.Number"/> setting.
    /// Slider only when set together with <see cref="Minimum"/>; otherwise numeric field.
    /// Ignored by other kinds.</summary>
    public double? Maximum { get; }

    /// <summary>True when both bounds are present and ordered, i.e. the host should offer a
    /// slider. A range with only one bound (or inverted) is treated as no range.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRange => Minimum.HasValue && Maximum.HasValue && Maximum.Value > Minimum.Value;

    /// <summary>Stable identifier (key). Immutable. Persisted to disk under this name.</summary>
    public string Name { get; }

    /// <summary>Short human-readable label shown in settings UI (defaults to <see cref="Name"/>).</summary>
    public string DisplayName { get; set; }

    /// <summary>Human-readable explanation shown next to the editor.</summary>
    public string Description { get; set; }

    public WidgetSettingKind Kind { get; }

    /// <summary>Value at declaration time. Used by host "reset to defaults". The host may replace
    /// it via <see cref="OverrideDefault"/> when the manifest changes what the sensible
    /// default is (e.g. enabling the Auto theme).</summary>
    public object? DefaultValue { get; private set; }

    /// <summary>Replaces the declared default. The current value moves with it when it still
    /// equals the OLD default — so a widget that has never been configured picks up the new
    /// default, while a value the user actually chose is left alone. This is what lets the
    /// host make "Auto" the default for a widget that opted into it without ever overwriting
    /// an explicit choice.</summary>
    public void OverrideDefault(object? newDefault)
    {
        object? previous = DefaultValue;
        var coerced = Coerce(newDefault, Kind, nameof(newDefault), ListOfAvailableStrings);
        DefaultValue = coerced;
        if (Equals(Value, previous)) Value = coerced;
    }

    /// <summary>Current value (<see cref="string"/> (also for <see cref="WidgetSettingKind.ListOfStrings"/>),
    /// <see cref="double"/> or <see cref="bool"/> per <see cref="Kind"/>; <c>null</c> only when the
    /// default was null). Raises <see cref="ValueChanged"/> on actual change; rejects mismatched
    /// values with <see cref="System.ArgumentException"/>.</summary>
    public object? Value
    {
        get => _value;
        set
        {
            var coerced = Coerce(value, Kind, nameof(value), ListOfAvailableStrings);
            if (Equals(_value, coerced))
            {
                return;
            }

            _value = coerced;
            ValueChanged?.Invoke(this, System.EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread after <see cref="Value"/> changes (host edits included).</summary>
    public event System.EventHandler? ValueChanged;

    /// <summary>Restores <see cref="DefaultValue"/> (raises <see cref="ValueChanged"/> when changed).</summary>
    public void Reset() => Value = DefaultValue;

    public string GetString() => Value as string ?? (DefaultValue as string ?? "");

    public double GetNumber()
    {
        if (Value is double d) return d;
        if (Value is null) return 0;
        return System.Convert.ToDouble(Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool GetBoolean()
    {
        if (Value is bool b) return b;
        if (Value is null) return false;
        return System.Convert.ToBoolean(Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The colour as <c>#AARRGGBB</c> (alpha first, uppercase). Falls back to
    /// <paramref name="fallback"/> when the value is not a valid hex colour. Handy because
    /// <see cref="WidgetSettingKind.ColorH"/> stores its value as a string — parse it once
    /// per frame rather than re-validating on every write.</summary>
    public string GetColorHex(string fallback = "#00000000")
    {
        string s = Value as string ?? "";
        return TryNormalizeColorHex(s, out string normalized) ? normalized : fallback;
    }

    /// <summary>Normalizes a colour to <c>#AARRGGBB</c> (alpha first, uppercase). Accepts
    /// <c>#RGB</c>, <c>#ARGB</c>, <c>#RRGGBB</c> and <c>#AARRGGBB</c>, with or without the
    /// leading <c>#</c>, and anything <see cref="System.Drawing.ColorTranslator"/> understands
    /// for the named forms. Alpha defaults to FF when the input carries none.</summary>
    public static bool TryNormalizeColorHex(string? text, out string normalized)
    {
        normalized = "#00000000";
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();

        // Named colours ("Red") and #RGB / #RRGGBB go through ColorTranslator, which also
        // covers the alpha-less forms by returning an opaque colour.
        try
        {
            var named = System.Drawing.ColorTranslator.FromHtml(s.StartsWith("#", StringComparison.Ordinal) ? s : "#" + s);
            if (named.A != 0 || string.Equals(s, "#000000", StringComparison.OrdinalIgnoreCase))
            {
                normalized = $"#{named.A:X2}{named.R:X2}{named.G:X2}{named.B:X2}";
                return true;
            }
        }
        catch { }

        // Explicit ARGB, possibly without '#'.
        string digits = s.StartsWith("#", StringComparison.Ordinal) ? s.Substring(1) : s;
        if (digits.Length == 8
            && uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint argb))
        {
            normalized = $"#{argb:X8}";
            return true;
        }
        return false;
    }

    public override string ToString() => $"{Name}={Value ?? "<null>"}";

    private static object? Coerce(object? value, WidgetSettingKind kind, string paramName, Dictionary<string, string>? options)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return kind switch
            {
                WidgetSettingKind.String => value as string
                    ?? System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                WidgetSettingKind.Number => value is double d ? d
                    : System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
                WidgetSettingKind.Boolean => value is bool b ? b
                    : System.Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture),
                // Rejected rather than stored verbatim: a colour that is not a colour would
                // otherwise survive a round-trip and fail much later, at paint time.
                WidgetSettingKind.ColorH => TryNormalizeColorHex(value as string, out string hex)
                    ? hex
                    : throw new System.ArgumentException(
                        $"'{value}' is not a valid colour; expected #AARRGGBB.", paramName),
                WidgetSettingKind.Theme => value is string t && ThemeKeys.IsValid(t)
                    ? t
                    : throw new System.ArgumentException(
                        $"'{value}' is not a valid theme; expected one of {ThemeKeys.FollowApp}, " +
                        $"{ThemeKeys.Light}, {ThemeKeys.Dark}, {ThemeKeys.Auto}.", paramName),
                WidgetSettingKind.ListOfStrings => value is string s
                    && (options is null || options.ContainsKey(s)) ? s
                    : throw new System.ArgumentException(
                        $"Value '{value}' is not one of the available options.", paramName),
                _ => throw new System.ArgumentOutOfRangeException(paramName),
            };
        }
        catch (System.Exception ex) when (ex is not System.ArgumentException)
        {
            throw new System.ArgumentException(
                $"Value '{value}' is not valid for a {kind} setting.", paramName, ex);
        }
    }

    /// <summary>
    /// <summary>Available options when <see cref="Kind"/> is
    /// <see cref="WidgetSettingKind.ListOfStrings"/>, as value → display-name pairs.</summary>
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string>? ListOfAvailableStrings { get; }

    #region Dispose pattern

    //public bool IsDisposed { get; private set; }

    private void Dispose(bool disposing)
    {
        //if (!IsDisposed)
        //{
        if (disposing)
        {
            // dispose managed state (managed objects)
        }
        ValueChanged = null;
        // free unmanaged resources (unmanaged objects) and override finalizer
        // set large fields to null

        //    IsDisposed = true;
        //}
    }

    // override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    // ~WidgetSetting()
    // {
    //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion Dispose pattern

}

/// <summary>Opt-in contract for widgets exposing user settings. The host shows one editor per
/// entry in <see cref="Settings"/> (honoring <see cref="WidgetSetting.Kind"/>,
/// <see cref="WidgetSetting.Description"/> and <see cref="WidgetSetting.ValueChanged"/>) and
/// persists values per placed widget. Implemented by <see cref="NativeWidgetControl"/>; raw
/// <see cref="INativeWidget"/> implementations may implement it directly.</summary>
public interface IWidgetSettingsProvider
{
    IReadOnlyList<WidgetSetting> Settings { get; }
}
