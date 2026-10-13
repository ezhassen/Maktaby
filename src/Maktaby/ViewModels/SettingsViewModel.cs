using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Settings;
using Maktaby.ViewModels;
using Maktaby.Win32APIs.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;

namespace Maktaby.ViewModels;

/// <summary>
/// One row of the Settings "Containers" list. Toggling <see cref="IsVisible"/> shows/hides the
/// live window immediately via <see cref="DesktopManager"/> without saving the snapshot, and
/// mirrors visibility changes made outside the Settings window (menus) through
/// <see cref="ContainerViewModel.PropertyChanged"/>.
/// </summary>
public sealed class ContainerVisibilityRow : ViewModelBase, IDisposable
{
    private readonly ContainerViewModel _container;
    private readonly DesktopManager _manager;
    private bool _disposed;

    public ContainerVisibilityRow(ContainerViewModel container, DesktopManager manager)
    {
        _container = container;
        _manager = manager;
        _container.PropertyChanged += OnContainerPropertyChanged;
    }

    public Guid ContainerId => _container.Id;

    public string Title => _container.Title;

    public string TypeDisplay => _container.Type switch
    {
        DesktopItemContainerType.BoxContainer => "Box",
        DesktopItemContainerType.WebWidget => "Web",
        DesktopItemContainerType.NativeWidget => "Native",
        _ => _container.Type.ToString(),
    };

    public bool IsVisible
    {
        get => _container.IsVisible;
        set
        {
            if (_container.IsVisible == value) return;
            if (value) _manager.ShowContainer(_container.Id);
            else _manager.HideContainer(_container.Id);
            OnPropertyChanged();
        }
    }

    private void OnContainerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContainerViewModel.IsVisible))
            OnPropertyChanged(nameof(IsVisible));
        else if (e.PropertyName == nameof(ContainerViewModel.Title))
            OnPropertyChanged(nameof(Title));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _container.PropertyChanged -= OnContainerPropertyChanged;
    }
}

/// <summary>
/// Drives <see cref="Views.SettingsView"/>. Edits the persisted <see cref="Settings.UserSettings"/>
/// (app theme, default box appearance and colors) and applies them live via
/// <see cref="App.ApplyTheme"/> and <see cref="App.ApplyBoxAppearance"/>. Also exposes the launch-on-startup
/// toggle (staged by the checkbox, applied to <see cref="StartupManager"/> on Save) and a per-setting "reset to default" command that
/// reads each property's <see cref="DefaultValueAttribute"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly MainViewModel _mainVm;
    private readonly DesktopManager _desktopManager;
    private bool _disposed;

    /// <summary>Live list of all containers for the Settings "Containers" section.</summary>
    public ObservableCollection<ContainerVisibilityRow> Containers { get; } = new();

    // App theme: "System" follows the Windows light/dark setting (stored as null).
    public IReadOnlyList<string> AppThemeOptions { get; } = new[] { "System", "Dark", "Light" };

    // Window backdrop effect: "Auto" lets WPF-UI pick per theme (stored as null); the rest map
    // to their WPF-UI WindowBackdropType names.
    public IReadOnlyList<string> WindowBackdropOptions { get; } = new[] { "Auto", "None", "Mica", "Acrylic", "Tabbed" };

    // Box theme: "App" inherits the app theme (stored as null); otherwise an explicit dark/light override.
    public IReadOnlyList<string> BoxThemeOptions { get; } = new[] { "App", "Dark", "Light" };

    // CSS Widgets theme: "App" follows app theme, otherwise Dark/Light override for switchable widgets (null = App)
    public IReadOnlyList<string> WebWidgetThemeOptions { get; } = new[] { "App", "Dark", "Light" };

    // Log level: Serilog levels, staged by the selector and applied on Save.
    public IReadOnlyList<string> LogLevelOptions { get; } = new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    // Update channel. "Auto" follows the installed build; the other two pin it.
    public IReadOnlyList<string> UpdateChannelOptions { get; } = new[] { "Auto", "Stable", "Beta" };

    private string _updateChannelOption = "Auto";

    /// <summary>
    /// Named after the model property (<c>UserSettings.UpdateChannel</c>), NOT "Selected...".
    /// <see cref="ResetToDefault"/> resolves the setting by reflecting
    /// <c>typeof(UserSettings).GetProperty(CommandParameter)</c> and then the view-model
    /// property of the SAME name, so a mismatched name makes the reset button a silent
    /// no-op. Same for <see cref="CheckForUpdatesOnStartup"/> below.
    /// </summary>
    public string UpdateChannel
    {
        get => _updateChannelOption;
        set => SetField(ref _updateChannelOption, value);
    }

    private bool _checkForUpdatesOnStartup = true;

    /// <summary>See <see cref="UpdateChannel"/> for why this keeps the model's name.</summary>
    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => SetField(ref _checkForUpdatesOnStartup, value);
    }

    private string _selectedLogLevelOption = "Warning";
    public string SelectedLogLevelOption
    {
        get => _selectedLogLevelOption;
        set => SetField(ref _selectedLogLevelOption, value);
    }

    /// <summary>Staged launch-on-startup choice. Toggling stages the value only — the
    /// system shortcut is created/removed in <see cref="Save"/>, never by the checkbox itself.</summary>
    private bool _launchOnStartup;
    public bool LaunchOnStartup
    {
        get => _launchOnStartup;
        set => SetField(ref _launchOnStartup, value);
    }

    /// <summary>Resets a single editable setting to its <see cref="DefaultValueAttribute"/> value.</summary>
    public ICommand ResetToDefaultCommand { get; }

    private string _selectedThemeOption = "System";
    public string SelectedThemeOption
    {
        get => _selectedThemeOption;
        set => SetField(ref _selectedThemeOption, value);
    }

    private string _windowBackdropOption = "Mica";
    /// <summary>Window backdrop effect, bound to the Settings ComboBox. Setting it re-applies the
    /// theme live so already-open windows pick up the new backdrop immediately.</summary>
    public string WindowBackdropOption
    {
        get => _windowBackdropOption;
        set
        {
            if (SetField(ref _windowBackdropOption, value))
            {
                var s = _settingsService.UserSettings;
                s.WindowBackdropType = OptionToBackdrop(value);
                _settingsService.Save();
                App.ApplyTheme(s.SelectedTheme);
            }
        }
    }

    /// <summary>Maps a backdrop display name to its stored value ("Auto" → "auto"; else lowercased).</summary>
    private static string? OptionToBackdrop(string option)
        => option?.Trim().ToLowerInvariant();

    /// <summary>Maps a stored backdrop value to its display name (null → "Mica", the app default;
    /// unknown → "Mica").</summary>
    private static string BackdropToOption(string? stored)
        => stored?.Trim().ToLowerInvariant() switch
        {
            "none" => "None",
            "mica" => "Mica",
            "acrylic" => "Acrylic",
            "tabbed" => "Tabbed",
            "auto" => "Auto",
            _ => "Mica"
        };

    private string _languageOption = "English";
    /// <summary>Display name of the active language, bound to the Settings language ComboBox.
    /// Setting it applies the language immediately (live) and persists it — the picker must
    /// not wait for Save. Named after the model property is not required here (it is a
    /// display-name list, not the stored code), but <see cref="ResetToDefault"/> maps it
    /// back to English like the other option properties.</summary>
    public string LanguageOption
    {
        get => _languageOption;
        set
        {
            if (SetField(ref _languageOption, value))
                ApplyLanguage(value);
        }
    }

    /// <summary>Language choices for the ComboBox: "System" (follow the OS language) first,
    /// then every discovered language by its display name (meta.name).</summary>
    public IReadOnlyList<string> LanguageOptions { get; } = BuildLanguageOptions();

    /// <summary>Display name of the "follow the OS language" choice; the model stores it as null.</summary>
    public const string SystemLanguage = "System";

    private static IReadOnlyList<string> BuildLanguageOptions()
    {
        var list = new List<string> { SystemLanguage };
        try
        {
            var langs = Localization.Loc.AvailableLanguages;
            list.AddRange(langs.Select(l => l.DisplayName));
        }
        catch { }
        if (list.Count == 1) list.Add("English");
        return list;
    }

    /// <summary>Applies a language chosen in the Settings window immediately (live) and
    /// persists the underlying culture code. "System" persists null (follow the OS language).</summary>
    private void ApplyLanguage(string displayName)
    {
        try
        {
            if (string.Equals(displayName, SystemLanguage, StringComparison.OrdinalIgnoreCase))
            {
                App.ApplyLanguage(null);
                return;
            }
            var match = Localization.Loc.AvailableLanguages
                .FirstOrDefault(l => string.Equals(l.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
            var code = match?.Code ?? "en";
            App.ApplyLanguage(code);
        }
        catch { }
    }

    /// <summary>Maps a stored culture code (or null = follow OS) to the display name shown
    /// in the ComboBox. null → "System"; a code → that language's display name; otherwise the
    /// active language's display name, then English.</summary>
    private static string ResolveLanguageDisplayName(string? code)
    {
        try
        {
            var langs = Localization.Loc.AvailableLanguages;
            if (string.IsNullOrWhiteSpace(code))
                return SystemLanguage;
            var match = langs.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.DisplayName;
            var active = Localization.Loc.Current;
            if (active is not null) return active.DisplayName;
            return "English";
        }
        catch
        {
            return "English";
        }
    }

    private string _defaultBoxThemeOption = "App";
    public string DefaultBoxThemeOption
    {
        get => _defaultBoxThemeOption;
        set => SetField(ref _defaultBoxThemeOption, value);
    }

    private string _defaultWebWidgetsThemeOption = "App";
    public string DefaultWebWidgetsThemeOption
    {
        get => _defaultWebWidgetsThemeOption;
        set => SetField(ref _defaultWebWidgetsThemeOption, value);
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

    /// <summary>Null = follow the desktop's current icon size (shown as "Auto" in the slider readout).</summary>
    private int? _defaultBoxIconSize;
    public int? DefaultBoxIconSize
    {
        get => _defaultBoxIconSize;
        set => SetField(ref _defaultBoxIconSize, value);
    }

    /// <summary>Max MB of video/GIF bytes preloaded into RAM (0 = disabled). Clamped 0..1024.</summary>
    private int _liveWallpaperPreloadMaxMB = 100;
    public int LiveWallpaperPreloadMaxMB
    {
        get => _liveWallpaperPreloadMaxMB;
        set => SetField(ref _liveWallpaperPreloadMaxMB, System.Math.Clamp(value, 0, 1024));
    }

    public ICommand SaveCommand { get; }

    public SettingsViewModel(ISettingsService settingsService, MainViewModel mainVm, DesktopManager desktopManager)
    {
        _settingsService = settingsService;
        _mainVm = mainVm;
        _desktopManager = desktopManager;
        foreach (var c in _mainVm.Containers)
            Containers.Add(new ContainerVisibilityRow(c, _desktopManager));
        _mainVm.Containers.CollectionChanged += OnContainersChanged;

        LoadFromCurrentState();

        SaveCommand = new RelayCommand(_ => Save());
        ResetToDefaultCommand = new RelayCommand(ResetToDefault);
    }

    /// <summary>
    /// Primes every editable field from live state. Shared by the constructor and
    /// <see cref="ReloadFromSettings"/>: assigning through the properties (rather than the backing
    /// fields) makes this safe to call on a bound view-model, because the setters only raise
    /// PropertyChanged when the value actually changed.
    /// </summary>
    private void LoadFromCurrentState()
    {
        var s = _settingsService.UserSettings;

        SelectedThemeOption = s.SelectedTheme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "System"
        };
        // Backdrop: display name from the stored value. The setter applies live, so prime the
        // backing field here (do NOT re-apply on load).
        _windowBackdropOption = BackdropToOption(s.WindowBackdropType);
        // Language: display name of the active language (from meta.name). The setter
        // applies live, so priming it here must NOT re-apply — assign the backing field.
        _languageOption = ResolveLanguageDisplayName(s.Language);
        DefaultBoxThemeOption = s.DefaultBoxTheme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "App"
        };
        DefaultWebWidgetsThemeOption = s.DefaultWebWidgetsTheme?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            _ => "App"
        };
        UpdateChannel = s.UpdateChannel?.Trim() switch
        {
            "Stable" => "Stable",
            "Beta" => "Beta",
            _ => "Auto"
        };
        CheckForUpdatesOnStartup = s.CheckForUpdatesOnStartup;

        DefaultBoxTransparencyValue = s.DefaultBoxTransparencyValue;
        DefaultBoxBackColor = s.DefaultBoxBackColor;
        DefaultBoxForeColor = s.DefaultBoxForeColor;
        DefaultBoxBorderColor = s.DefaultBoxBorderColor;
        DefaultBoxTitleBarBackColor = s.DefaultBoxTitleBarBackColor;
        DefaultBoxTitleBarForeColor = s.DefaultBoxTitleBarForeColor;
        DefaultBoxBorderThickness = s.DefaultBoxBorderThickness;
        DefaultBoxTitleBarColorsSameAsBox = s.DefaultBoxTitleBarColorsSameAsBox;
        DefaultBoxIconSize = s.DefaultBoxIconSize;
        LiveWallpaperPreloadMaxMB = s.LiveWallpaperPreloadMaxMB;

        // Launch-on-startup and the log level do not live in UserSettings: the first is the real
        // registry key (UserSettings is not the source of truth), the second is AppJSettings.json.
        // Neither is carried by a .dbe1 bundle, so re-reading live state keeps the window truthful.
        LaunchOnStartup = StartupManager.IsEnabled;
        SelectedLogLevelOption = (AppJSettings.Instance.LogEventLevel ?? Logging.DefaultLogEventLevel).ToString();
    }

    /// <summary>
    /// Discards the edits staged in this window and re-reads every field from
    /// <see cref="ISettingsService.UserSettings"/>. A bundle restore replaces the settings object
    /// wholesale (<see cref="ISettingsService.Load"/>), so a window that was already open keeps
    /// showing the pre-restore values — and because <see cref="Save"/> writes those fields straight
    /// back, pressing Save afterwards would silently undo the restore. Must be called on the UI
    /// thread.
    /// </summary>
    public void ReloadFromSettings() => LoadFromCurrentState();

    private void OnContainersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var r in Containers) r.Dispose();
            Containers.Clear();
            return;
        }
        if (e.OldItems != null)
        {
            foreach (ContainerViewModel vm in e.OldItems)
            {
                var row = Containers.FirstOrDefault(r => r.ContainerId == vm.Id);
                if (row != null)
                {
                    Containers.Remove(row);
                    row.Dispose();
                }
            }
        }
        if (e.NewItems != null)
        {
            foreach (ContainerViewModel vm in e.NewItems)
                Containers.Add(new ContainerVisibilityRow(vm, _desktopManager));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainVm.Containers.CollectionChanged -= OnContainersChanged;
        foreach (var r in Containers) r.Dispose();
    }

    /// <summary>Maps a display name to its stored culture code. "System" → null (follow the OS
    /// language); a language display name → its code; unknown → "en".</summary>
    private static string? ResolveLanguageCode(string displayName)
    {
        try
        {
            if (string.Equals(displayName, SystemLanguage, StringComparison.OrdinalIgnoreCase))
                return null;
            var match = Localization.Loc.AvailableLanguages
                .FirstOrDefault(l => string.Equals(l.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
            return match?.Code ?? "en";
        }
        catch
        {
            return "en";
        }
    }

    /// <summary>
    /// Resets the setting named by <paramref name="parameter"/> to the value declared on the matching
    /// <see cref="UserSettings"/> property via <see cref="DefaultValueAttribute"/>. The theme option
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

        // Backdrop reset: back to Mica (the app default). The setter applies live.
        if (name == nameof(WindowBackdropOption))
        {
            WindowBackdropOption = "Mica";
            return;
        }

        // Language reset: back to System (follow the OS language). The setter applies live.
        if (name == nameof(LanguageOption))
        {
            LanguageOption = SystemLanguage;
            return;
        }

        if (name == nameof(DefaultBoxThemeOption))
        {
            DefaultBoxThemeOption = "App";
            return;
        }

        if (name == nameof(DefaultWebWidgetsThemeOption))
        {
            DefaultWebWidgetsThemeOption = "App";
            return;
        }

        // Nullable slider: reset means "follow the desktop" (null), not a numeric default.
        if (name == nameof(DefaultBoxIconSize))
        {
            DefaultBoxIconSize = null;
            return;
        }

        // Every early return below is logged, not silent. A reset button whose
        // CommandParameter does not match a UserSettings property fails in exactly the same
        // way as one that legitimately has no default, and that ambiguity is what hid a
        // broken button (CommandParameter="SelectedUpdateChannelOption" against a model
        // property named "UpdateChannel").
        var modelProp = typeof(UserSettings).GetProperty(name);
        if (modelProp is null)
        {
            Logging.Log.Warning(
                "Settings reset: '{Name}' is not a UserSettings property, so the reset button is wired to the wrong name",
                name);
            return;
        }

        var defAttr = modelProp.GetCustomAttribute<DefaultValueAttribute>();
        if (defAttr is null)
        {
            Logging.Log.Information("Settings reset: '{Name}' has no [DefaultValue], nothing to reset to", name);
            return;
        }

        var vmProp = GetType().GetProperty(name);
        if (vmProp is null || !vmProp.CanWrite)
        {
            Logging.Log.Warning(
                "Settings reset: '{Name}' has no writable view-model property of the same name, so the reset is a no-op",
                name);
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
        WindowBackdropType = OptionToBackdrop(_windowBackdropOption),
        DefaultBoxTheme = OptionToStored(_defaultBoxThemeOption),
        DefaultWebWidgetsTheme = OptionToStored(_defaultWebWidgetsThemeOption),
        DefaultBoxTransparencyValue = _defaultBoxTransparencyValue,
        DefaultBoxBackColor = _defaultBoxBackColor,
        DefaultBoxForeColor = _defaultBoxForeColor,
        DefaultBoxBorderColor = _defaultBoxBorderColor,
        DefaultBoxTitleBarBackColor = _defaultBoxTitleBarBackColor,
        DefaultBoxTitleBarForeColor = _defaultBoxTitleBarForeColor,
        DefaultBoxBorderThickness = _defaultBoxBorderThickness,
        DefaultBoxTitleBarColorsSameAsBox = _defaultBoxTitleBarColorsSameAsBox,
        DefaultBoxIconSize = _defaultBoxIconSize,
        UpdateChannel = _updateChannelOption,
        CheckForUpdatesOnStartup = _checkForUpdatesOnStartup,
        Language = ResolveLanguageCode(_languageOption),
    };

    public void Save()
    {
        var s = _settingsService.UserSettings;
        s.SelectedTheme = OptionToStored(_selectedThemeOption);
        s.WindowBackdropType = OptionToBackdrop(_windowBackdropOption);
        s.DefaultBoxTheme = OptionToStored(_defaultBoxThemeOption);
        s.DefaultWebWidgetsTheme = OptionToStored(_defaultWebWidgetsThemeOption);
        s.DefaultBoxTransparencyValue = _defaultBoxTransparencyValue;
        s.DefaultBoxBackColor = _defaultBoxBackColor;
        s.DefaultBoxForeColor = _defaultBoxForeColor;
        s.DefaultBoxBorderColor = _defaultBoxBorderColor;
        s.DefaultBoxTitleBarBackColor = _defaultBoxTitleBarBackColor;
        s.DefaultBoxTitleBarForeColor = _defaultBoxTitleBarForeColor;
        s.DefaultBoxBorderThickness = _defaultBoxBorderThickness;
        s.DefaultBoxTitleBarColorsSameAsBox = _defaultBoxTitleBarColorsSameAsBox;
        s.DefaultBoxIconSize = _defaultBoxIconSize;
        s.LiveWallpaperPreloadMaxMB = System.Math.Clamp(_liveWallpaperPreloadMaxMB, 0, 1024);
        _liveWallpaperPreloadMaxMB = s.LiveWallpaperPreloadMaxMB;
        s.UpdateChannel = _updateChannelOption;
        s.CheckForUpdatesOnStartup = _checkForUpdatesOnStartup;
        // Language is applied live by the ComboBox setter (ApplyLanguage already persisted
        // it); mirror it here so Save writes the current choice even if it was set before
        // this window opened.
        s.Language = ResolveLanguageCode(_languageOption);
        _settingsService.Save();
        // ApplyTheme applies the theme and registers the system-theme watcher; the boxes are
        // repainted by the ApplicationThemeManager.Changed handler.
        App.ApplyTheme(s.SelectedTheme);
        // Apply the staged launch-on-startup choice only here: flipping the checkbox stages
        // the value, and Save is the single point that touches system state for it.
        if (_launchOnStartup) StartupManager.Enable();
        else StartupManager.Disable();
        AppJSettings.Instance.LaunchOnStartup = _launchOnStartup;
        // Log level likewise: persist the staged choice and flip the live switch immediately
        // (the settings-file watcher would converge it within ~300 ms anyway).
        if (Enum.TryParse<Serilog.Events.LogEventLevel>(_selectedLogLevelOption, ignoreCase: true, out var logLevel))
        {
            AppJSettings.Instance.LogEventLevel = logLevel;
            try { Logging.LevelSwitch.MinimumLevel = logLevel; } catch { }
        }
        AppJSettings.Instance.Save();
        // Refresh all CSS widgets that follow the global theme (CanSwitchTheme=null) or app theme
        try
        {
            foreach (var win in System.Windows.Application.Current.Windows.OfType<Views.Containers.WebWidgetWindow>())
            {
                try
                {
                    var field = typeof(Views.Containers.WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(win) is Controls.ContainersControls.WebWidgetControl ctrl) ctrl.RefreshTheme();
                }
                catch { }
            }
            foreach (var win in System.Windows.Application.Current.Windows.OfType<Views.WidgetDataWindow>())
            {
                try
                {
                    var field = typeof(Views.WidgetDataWindow).GetField("_previewControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field?.GetValue(win) is Controls.ContainersControls.WebWidgetControl ctrl2) ctrl2.RefreshTheme();
                }
                catch { }
            }
        }
        catch { }
        // Start/suspend the live desktop icon-size watcher to match the new setting.
        App.SyncDesktopIconSizeWatcher();
        // Push the preload cap to a running engine (no rebuild needed).
        try { _ = App.Services.GetRequiredService<LiveWallpaperManager>().ApplyPreloadCapAsync(); } catch { }
    }
}
