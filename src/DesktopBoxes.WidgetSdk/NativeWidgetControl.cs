using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace DesktopBoxes.WidgetSdk;

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
        var root = (FrameworkElement)XamlReader.Load(reader.BaseStream);
        Content = root;
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
    }
}
