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

    // Box theme: "App" inherits the app theme (stored as null); otherwise an explicit dark/light override.
    public IReadOnlyList<string> BoxThemeOptions { get; } = new[] { "App", "Dark", "Light" };

    // CSS Widgets theme: "App" follows app theme, otherwise Dark/Light override for switchable widgets (null = App)
    public IReadOnlyList<string> WebWidgetThemeOptions { get; } = new[] { "App", "Dark", "Light" };

    // Log level: Serilog levels, staged by the selector and applied on Save.
    public IReadOnlyList<string> LogLevelOptions { get; } = new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

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
        _defaultWebWidgetsThemeOption = s.DefaultWebWidgetsTheme?.Trim().ToLowerInvariant() switch
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
        _defaultBoxIconSize = s.DefaultBoxIconSize;
        _liveWallpaperPreloadMaxMB = s.LiveWallpaperPreloadMaxMB;

        _launchOnStartup = StartupManager.IsEnabled;

        _selectedLogLevelOption = (AppJSettings.Instance.LogEventLevel ?? Logging.DefaultLogEventLevel).ToString();

        SaveCommand = new RelayCommand(_ => Save());
        ResetToDefaultCommand = new RelayCommand(ResetToDefault);
    }

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
    };

    public void Save()
    {
        var s = _settingsService.UserSettings;
        s.SelectedTheme = OptionToStored(_selectedThemeOption);
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
