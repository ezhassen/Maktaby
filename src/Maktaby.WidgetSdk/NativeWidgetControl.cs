using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Maktaby.WidgetSdk;

/// <summary>Convenience base: a <see cref="UserControl"/> that already implements
/// <see cref="INativeWidget"/>. Derive from this, build UI in the constructor (or load
/// loose XAML via <see cref="LoadXamlResource"/>), override the <c>On*</c> hooks, and
/// call the <c>Raise*</c> methods for the interaction events the host cannot see
/// (e.g. content hosted through an HWND airspace). For pure WPF visuals the host
/// detects mouse/focus itself — raising events is then optional.</summary>
public class NativeWidgetControl : UserControl, INativeWidget, IWidgetSettingsProvider
{
    public FrameworkElement Visual => this;

    public bool IsSuspended { get; private set; }

    private readonly List<WidgetSetting> _settings = new();

    /// <summary>Settings declared via <see cref="DefineSetting"/> (empty by default).
    /// The host renders one editor per entry and writes back through
    /// <see cref="WidgetSetting.Value"/>.</summary>
    IReadOnlyList<WidgetSetting> IWidgetSettingsProvider.Settings => _settings;

    protected WidgetSetting DefineSetting(string name, string displayName, string description, WidgetSettingKind kind, Action onValueChanged, object? defaultValue = null, Dictionary<string, string>? listOfAvailableStrings = null)
    {
        if (_settings.Exists(s => s.Name == name)) throw new System.ArgumentException($"Duplicate widget setting '{name}'.", nameof(WidgetSetting));

        var setting = new WidgetSetting(name, displayName, description, kind, onValueChanged, defaultValue: defaultValue, listOfAvailableStrings: listOfAvailableStrings);
        _settings.Add(setting);
        return setting;
    }

    /// <summary>Declares a user-editable setting (call from the derived constructor).
    /// Names must be unique per widget; duplicates throw.</summary>
    protected WidgetSetting DefineSetting(WidgetSetting setting)
    {
        if (setting is null) throw new System.ArgumentNullException(nameof(setting));
        if (_settings.Exists(s => s.Name == setting.Name))
        {
            throw new System.ArgumentException($"Duplicate widget setting '{setting.Name}'.", nameof(setting));
        }

        _settings.Add(setting);
        return setting;
    }

    /// <summary>Finds a declared setting by name (<c>null</c> when absent).</summary>
    protected WidgetSetting? FindSetting(string name) =>
        _settings.Find(s => s.Name == name);

    /// <summary>Reads a declared setting's current value as <typeparamref name="T"/> (same
    /// shorthand as <c>FindSetting(name)?.GetBoolean() == true</c>, without the null dance).
    /// Returns <paramref name="defaultValue"/> when the setting is absent (e.g. read before
    /// its <see cref="DefineSetting"/> call) or the value cannot convert; never throws.
    /// Conversions use the invariant culture; enums parse from their string form
    /// (handy for <see cref="WidgetSettingKind.ListOfStrings"/> keys).</summary>
    protected T GetSettingsValue<T>(string name, T defaultValue)
    {
        try
        {
            var value = FindSetting(name)?.Value;
            if (value is null) return defaultValue;
            if (value is T typed) return typed;
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target.IsEnum && value is string s && System.Enum.TryParse(target, s, true, out var parsed))
                return (T)parsed;
            return (T)System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Suspend()
    {
        if (IsSuspended) return;
        IsSuspended = true;
        try { OnSuspend(); } catch { }
    }

    public void Resume()
    {
        if (!IsSuspended) return;
        IsSuspended = false;
        try { OnResume(); } catch { }
    }

    public void ApplyTheme(string? theme)
    {
        try { OnApplyTheme(theme); } catch { }
    }

    protected virtual void OnSuspend() { }
    protected virtual void OnResume() { }
    protected virtual void OnApplyTheme(string? theme) { }

    public event EventHandler? Entered;
    public event EventHandler? Left;
    public event EventHandler? Clicked;
    public event EventHandler<WidgetPointerEventArgs>? Pressed;
    public event EventHandler? Focused;
    public event EventHandler? Unfocused;

    protected void RaiseEntered() => Entered?.Invoke(this, EventArgs.Empty);
    protected void RaiseLeft() => Left?.Invoke(this, EventArgs.Empty);
    protected void RaiseClicked() => Clicked?.Invoke(this, EventArgs.Empty);
    protected void RaisePressed(double x, double y, WidgetPointerButton button) =>
        Pressed?.Invoke(this, new WidgetPointerEventArgs(x, y, button));
    protected void RaiseFocused() => Focused?.Invoke(this, EventArgs.Empty);
    protected void RaiseUnfocused() => Unfocused?.Invoke(this, EventArgs.Empty);

    /// <summary>Loads a loose <c>.xaml</c> file embedded as a manifest resource by the host
    /// compiler (resource name <c>&lt;slug&gt;/&lt;file&gt;.xaml</c>) and sets it as content.
    /// Named elements are reachable via <see cref="FrameworkElement.FindName"/> afterwards
    /// (loose XAML has no <c>x:Class</c> wiring).</summary>
    protected void LoadXamlResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"XAML resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        // Mark this control as the {sdk:Loc …} target owner while its loose XAML parses, so
        // extensions record against it. Restored in finally — nested loads can't happen here.
        var prev = CurrentConstructing;
        CurrentConstructing = this;
        try
        {
            var root = (FrameworkElement)XamlReader.Load(reader.BaseStream);
            Content = root;
        }
        finally
        {
            CurrentConstructing = prev;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_settings?.Any() == true)
        {
            foreach (var item in _settings)
            {
                item.Dispose();
            }
        }
        if (disposing && _loc is not null)
        {
            _loc.LanguageChanged -= OnWidgetLanguageChanged;
        }
    }

    #region Localization

    private IWidgetLocalization? _loc;

    // Targets recorded by {sdk:Loc …} during XAML parse, resolved once _loc is attached.
    private readonly List<(DependencyObject Target, DependencyProperty Property, string Key)> _locTargets = new();

    /// <summary>
    /// Called by the host right after construction to attach the widget's localization.
    /// Resolves every <c>{sdk:Loc …}</c> target recorded during the constructor's XAML parse
    /// and subscribes to <see cref="IWidgetLocalization.LanguageChanged"/> so a live app
    /// language switch re-translates them. Idempotent; safe to call with null (detaches).
    /// </summary>
    internal void SetLocalization(IWidgetLocalization? loc)
    {
        if (_loc is not null)
            _loc.LanguageChanged -= OnWidgetLanguageChanged;
        _loc = loc;
        if (loc is not null)
            loc.LanguageChanged += OnWidgetLanguageChanged;
        ResolveLocTargets();
        ApplyContentFlowDirection();
    }

    /// <summary>Records a <c>{sdk:Loc …}</c> target for later resolution. Called by
    /// <see cref="LocExtension"/> during parse; the owning control is the one whose
    /// constructor is running, which is always <c>this</c> on the UI thread.</summary>
    internal static void RegisterLocTarget(DependencyObject target, DependencyProperty property, string key)
    {
        // Resolve the control currently being constructed. During a plugin constructor the
        // only NativeWidgetControl on the stack is the one building its visual, so a
        // thread-static "current" set at the top of the base constructor is exact.
        var current = CurrentConstructing;
        current?._locTargets.Add((target, property, key));
    }

    // The control whose constructor is currently running (UI thread only).
    [System.ThreadStatic] private static NativeWidgetControl? CurrentConstructing;

    /// <summary>Localized text for <paramref name="key"/>. When no localization is attached
    /// (or the key is missing) returns <paramref name="defaultValue"/>. <paramref name="args"/>
    /// are formatted with the current culture.</summary>
    protected string L(string key, string defaultValue, params object?[] args)
        => _loc?.Get(key, defaultValue, args) ?? defaultValue;

    /// <summary>Plural form of <paramref name="key"/> for <paramref name="count"/>, falling
    /// back to <paramref name="defaultValue"/> when unresolved.</summary>
    protected string LPlural(string key, int count, string defaultValue, params object?[] args)
        => _loc?.Plural(key, count, defaultValue, args) ?? defaultValue;

    /// <summary>Content flow direction for this widget: the widget's own direction when its
    /// active xlf declares one, else the host app language's. Apply to the widget's root
    /// visual if the plugin wants its content mirrored (opt-in per the widget's xlf).</summary>
    protected FlowDirection ContentFlowDirection => _loc?.FlowDirection ?? FlowDirection.LeftToRight;

    private void ResolveLocTargets()
    {
        if (_loc is null || _locTargets.Count == 0) return;
        foreach (var (target, property, key) in _locTargets)
        {
            try
            {
                var text = _loc.Get(key, key);
                if (target.Dispatcher.CheckAccess())
                    target.SetValue(property, text);
                else
                    target.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { target.SetValue(property, text); } catch { }
                    }));
            }
            catch { }
        }
    }

    private void ApplyContentFlowDirection()
    {
        // Opt-in: only mirror when the widget's active language actually declares RTL.
        if (ContentFlowDirection == FlowDirection.RightToLeft)
        {
            try { FlowDirection = FlowDirection.RightToLeft; } catch { }
        }
    }

    private void OnWidgetLanguageChanged()
    {
        ResolveLocTargets();
        ApplyContentFlowDirection();
        try { OnLanguageChanged(); } catch { }
    }

    /// <summary>Called after the active language changed (host switch or initial attach).
    /// Override to re-apply strings the widget built in code (tooltips, labels) that are not
    /// <c>{sdk:Loc …}</c> targets. Runs on the UI thread.</summary>
    protected virtual void OnLanguageChanged() { }

    #endregion
}
