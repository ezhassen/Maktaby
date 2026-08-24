using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Settings;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32APIs.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// Drives <see cref="Views.SettingsView"/>. Edits the persisted <see cref="Settings.UserSettings"/>
/// (app theme, default box appearance and colors) and applies them live via
/// <see cref="App.ApplyTheme"/> and <see cref="App.ApplyBoxAppearance"/>. Also exposes the launch-on-startup
/// toggle (mirrored to <see cref="StartupManager"/>) and a per-setting "reset to default" command that
/// reads each property's <see cref="DefaultValueAttribute"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    // App theme: "System" follows the Windows light/dark setting (stored as null).
    public IReadOnlyList<string> AppThemeOptions { get; } = new[] { "System", "Dark", "Light" };

    // Box theme: "App" inherits the app theme (stored as null); otherwise an explicit dark/light override.
    public IReadOnlyList<string> BoxThemeOptions { get; } = new[] { "App", "Dark", "Light" };

    /// <summary>True when the app is registered to launch on Windows startup.</summary>
    private bool _launchOnStartup;
    public bool LaunchOnStartup
    {
        get => _launchOnStartup;
        set
        {
            if (!SetField(ref _launchOnStartup, value))
            {
                return;
            }

            if (value) StartupManager.Enable();
            else StartupManager.Disable();
            AppJSettings.Instance.LaunchOnStartup = value;
            AppJSettings.Instance.Save();
        }
    }

    /// <summary>Resets a single editable setting to its <see cref="DefaultValueAttribute"/> value.</summary>
    public ICommand ResetToDefaultCommand { get; }

    private string _selectedThemeOption = "System";
    public string SelectedThemeOption
    {
        get => _selectedThemeOption;
        set => SetField(ref _selectedThemeOption, value);
    }

    private string _defaultBoxThemeOption = "App";
    public string DefaultBoxThemeOption
    {
        get => _defaultBoxThemeOption;
        set => SetField(ref _defaultBoxThemeOption, value);
    }

    private double _defaultBoxTransparencyValue;
    public double DefaultBoxTransparencyValue
    {
        get => _defaultBoxTransparencyValue;
        set => SetField(ref _defaultBoxTransparencyValue, value);
    }

    private string? _defaultBoxBackColor;
    public string? DefaultBoxBackColor
    {
        get => _defaultBoxBackColor;
        set => SetField(ref _defaultBoxBackColor, value);
    }

    private string? _defaultBoxForeColor;
    public string? DefaultBoxForeColor
    {
        get => _defaultBoxForeColor;
        set => SetField(ref _defaultBoxForeColor, value);
    }

    private string? _defaultBoxBorderColor;
    public string? DefaultBoxBorderColor
    {
        get => _defaultBoxBorderColor;
        set => SetField(ref _defaultBoxBorderColor, value);
    }

    private string? _defaultBoxTitleBarBackColor;
    public string? DefaultBoxTitleBarBackColor
    {
        get => _defaultBoxTitleBarBackColor;
        set => SetField(ref _defaultBoxTitleBarBackColor, value);
    }

    private string? _defaultBoxTitleBarForeColor;
    public string? DefaultBoxTitleBarForeColor
    {
        get => _defaultBoxTitleBarForeColor;
        set => SetField(ref _defaultBoxTitleBarForeColor, value);
    }

    private double? _defaultBoxBorderThickness;
    public double? DefaultBoxBorderThickness
    {
        get => _defaultBoxBorderThickness;
        set => SetField(ref _defaultBoxBorderThickness, value);
    }

    private bool _defaultBoxTitleBarColorsSameAsBox = true;
    public bool DefaultBoxTitleBarColorsSameAsBox
    {
        get => _defaultBoxTitleBarColorsSameAsBox;
        set => SetField(ref _defaultBoxTitleBarColorsSameAsBox, value);
    }

    public ICommand SaveCommand { get; }

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        var s = _settingsService.UserSettings;

        _selectedThemeOption = s.SelectedTheme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "System"
        };
        _defaultBoxThemeOption = s.DefaultBoxTheme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "App"
        };

        _defaultBoxTransparencyValue = s.DefaultBoxTransparencyValue;
        _defaultBoxBackColor = s.DefaultBoxBackColor;
        _defaultBoxForeColor = s.DefaultBoxForeColor;
        _defaultBoxBorderColor = s.DefaultBoxBorderColor;
        _defaultBoxTitleBarBackColor = s.DefaultBoxTitleBarBackColor;
        _defaultBoxTitleBarForeColor = s.DefaultBoxTitleBarForeColor;
        _defaultBoxBorderThickness = s.DefaultBoxBorderThickness;
        _defaultBoxTitleBarColorsSameAsBox = s.DefaultBoxTitleBarColorsSameAsBox;

        _launchOnStartup = StartupManager.IsEnabled;

        SaveCommand = new RelayCommand(_ => Save());
        ResetToDefaultCommand = new RelayCommand(ResetToDefault);
    }

    /// <summary>
    /// Resets the setting named by <paramref name="parameter"/> to the value declared on the matching
    /// <see cref="UserSettings"/> property via <see cref="DefaultValueAttribute"/>. The two theme option
    /// properties are special-cased ("System" / "App") because the model stores them as null.
    /// </summary>
    private void ResetToDefault(object? parameter)
    {
        if (parameter is not string name)
        {
            return;
        }

        if (name == nameof(SelectedThemeOption))
        {
            SelectedThemeOption = "System";
            return;
        }

        if (name == nameof(DefaultBoxThemeOption))
        {
            DefaultBoxThemeOption = "App";
            return;
        }

        var modelProp = typeof(UserSettings).GetProperty(name);
        if (modelProp is null)
        {
            return;
        }

        var defAttr = modelProp.GetCustomAttribute<DefaultValueAttribute>();
        if (defAttr is null)
        {
            return;
        }

        var vmProp = GetType().GetProperty(name);
        if (vmProp is null || !vmProp.CanWrite)
        {
            return;
        }

        var value = defAttr.Value;
        if (value is null)
        {
            vmProp.SetValue(this, null);
            return;
        }

        var underlying = Nullable.GetUnderlyingType(vmProp.PropertyType) ?? vmProp.PropertyType;
        vmProp.SetValue(this, Convert.ChangeType(value, underlying));
    }

    // "System"/"App" -> null; "Dark" -> "dark"; "Light" -> "light".
    private static string? OptionToStored(string option) => option.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => null
    };

    /// <summary>
    /// Returns a <see cref="UserSettings"/> snapshot reflecting the current edited values (without
    /// persisting). Used by the preview so it matches the boxes exactly.
    /// </summary>
    public UserSettings ToUserSettings() => new UserSettings
    {
        SelectedTheme = OptionToStored(_selectedThemeOption),
        DefaultBoxTheme = OptionToStored(_defaultBoxThemeOption),
        DefaultBoxTransparencyValue = _defaultBoxTransparencyValue,
        DefaultBoxBackColor = _defaultBoxBackColor,
        DefaultBoxForeColor = _defaultBoxForeColor,
        DefaultBoxBorderColor = _defaultBoxBorderColor,
        DefaultBoxTitleBarBackColor = _defaultBoxTitleBarBackColor,
        DefaultBoxTitleBarForeColor = _defaultBoxTitleBarForeColor,
        DefaultBoxBorderThickness = _defaultBoxBorderThickness,
        DefaultBoxTitleBarColorsSameAsBox = _defaultBoxTitleBarColorsSameAsBox,
    };

    public void Save()
    {
        var s = _settingsService.UserSettings;
        s.SelectedTheme = OptionToStored(_selectedThemeOption);
        s.DefaultBoxTheme = OptionToStored(_defaultBoxThemeOption);
        s.DefaultBoxTransparencyValue = _defaultBoxTransparencyValue;
        s.DefaultBoxBackColor = _defaultBoxBackColor;
        s.DefaultBoxForeColor = _defaultBoxForeColor;
        s.DefaultBoxBorderColor = _defaultBoxBorderColor;
        s.DefaultBoxTitleBarBackColor = _defaultBoxTitleBarBackColor;
        s.DefaultBoxTitleBarForeColor = _defaultBoxTitleBarForeColor;
        s.DefaultBoxBorderThickness = _defaultBoxBorderThickness;
        s.DefaultBoxTitleBarColorsSameAsBox = _defaultBoxTitleBarColorsSameAsBox;
        _settingsService.Save();
        App.ApplyTheme(s.SelectedTheme);
        App.ApplyBoxAppearance();
    }
}
