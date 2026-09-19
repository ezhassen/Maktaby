using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopBoxes.WidgetSdk;

/// <summary>Value kind of a <see cref="WidgetSetting"/>. Fixed set so hosts can render a matching
/// editor (text box, number box, check box) without reflection.</summary>
public enum WidgetSettingKind
{
    String,
    Number,
    Boolean,
    ListOfStrings
}

/// <summary>A single user-editable setting of a native widget: a <see cref="Name"/>d value with a
/// human-readable <see cref="Description"/>. Values are <see cref="string"/>, <see cref="double"/>
/// or <see cref="bool"/> depending on <see cref="Kind"/>; <see cref="Value"/> validates on write.
/// All members run on the UI thread (same contract as <see cref="INativeWidget"/>).</summary>
public sealed class WidgetSetting : IDisposable
{
    private object? _value;

    public WidgetSetting(string name, string displayName, string description, WidgetSettingKind kind, Action onValueChanged, object? defaultValue = null, Dictionary<string, string>? listOfAvilableStrings = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new System.ArgumentException("Setting name must not be empty.", nameof(name));
        }
        if (kind == WidgetSettingKind.ListOfStrings && (listOfAvilableStrings is null || listOfAvilableStrings.Count == 0))
        {
            throw new InvalidOperationException("If WidgetSetting kind is ListOfStrings must provide listOfAvilableStrings");
        }

        Name = name;
        Description = description ?? "";
        Kind = kind;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;
        ListOfAvilableStrings = listOfAvilableStrings;
        DefaultValue = Coerce(defaultValue, kind, nameof(defaultValue), listOfAvilableStrings);
        _value = DefaultValue;
        if (onValueChanged is not null) this.ValueChanged += (_, _) => { onValueChanged(); };
    }

    /// <summary>Stable identifier (key). Immutable. Persisted to disk under this name.</summary>
    public string Name { get; }

    /// <summary>Short human-readable label shown in settings UI (defaults to <see cref="Name"/>).</summary>
    public string DisplayName { get; set; }

    /// <summary>Human-readable explanation shown next to the editor.</summary>
    public string Description { get; set; }

    public WidgetSettingKind Kind { get; }

    /// <summary>Value at declaration time. Used by host "reset to defaults".</summary>
    public object? DefaultValue { get; }

    /// <summary>Current value (<see cref="string"/> (also for <see cref="WidgetSettingKind.ListOfStrings"/>),
    /// <see cref="double"/> or <see cref="bool"/> per <see cref="Kind"/>; <c>null</c> only when the
    /// default was null). Raises <see cref="ValueChanged"/> on actual change; rejects mismatched
    /// values with <see cref="System.ArgumentException"/>.</summary>
    public object? Value
    {
        get => _value;
        set
        {
            var coerced = Coerce(value, Kind, nameof(value), ListOfAvilableStrings);
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
    /// Defined List of avilable strings if <see cref="Kind"/> is <see cref="WidgetSettingKind.ListOfStrings"/> [settingValue, DisplayName]
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string>? ListOfAvilableStrings { get; }

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
