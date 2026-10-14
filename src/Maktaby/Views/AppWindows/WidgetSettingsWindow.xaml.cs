using Maktaby.WidgetSdk;
using Maktaby.Core.Interfaces;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Maktaby.Views.AppWindows;

/// <summary>Per-widget settings editor, opened from the native widget window menu when the
/// plugin exposes settings (<see cref="IWidgetSettingsProvider"/> with entries). Edits apply
/// live to the widget; <b>Save</b> persists to disk, <b>Cancel</b> reverts to the values the
/// window opened with, <b>Reset</b> restores declared defaults (persist on Save).</summary>
public partial class WidgetSettingsWindow : AppFluentWindow
{
    private readonly NativeWidgetInfo _info;
    private readonly IWidgetSettingsProvider _provider;
    private readonly INativeWidgetSettingsService _svc;
    private readonly string? _settingsFileName;
    private readonly Dictionary<string, object?> _initial = new();
    private IWidgetLocalization? _loc;

    public string WindowTitle { get; }

    /// <summary>Parameterless for the VS/Blend designer: sample settings of every kind
    /// rendered through the real row builder.</summary>
    public WidgetSettingsWindow()
        : this(
            new NativeWidgetInfo("analog-clock", NativeWidgetSource.App, "", new NativeWidgetManifest { Name = "Analog Clock" }, null, null),
            new DesignSettingsProvider(),
            null!,
            null)
    {
    }

    private sealed class DesignSettingsProvider : IWidgetSettingsProvider
    {
        public IReadOnlyList<WidgetSetting> Settings { get; } = new List<WidgetSetting>
        {
            new("title", "Title", "Header text shown on the widget.", WidgetSettingKind.String, () => { }, "Analog Clock"),
            new("size", "Size", "Face diameter in pixels.", WidgetSettingKind.Number, () => { }, 300.0, minimum: 100, maximum: 600),
            new("showSeconds", "Show seconds", "Sweep the second hand.", WidgetSettingKind.Boolean, () => { }, true),
            new("handStyle", "Hand style", "Which hands to draw.", WidgetSettingKind.ListOfStrings, () => { }, "smooth",
                new Dictionary<string, string> { ["hide"] = "Hide", ["smooth"] = "Smooth sweep", ["step"] = "Step once per second" }),
            new("tint", "Tint", "Background tint.", WidgetSettingKind.ColorH, () => { }, "#B3161A1E"),
        };
    }

    public WidgetSettingsWindow(NativeWidgetInfo info, IWidgetSettingsProvider provider, INativeWidgetSettingsService svc,
        string? settingsFileName = null, IWidgetLocalization? localization = null)
    {
        _info = info;
        _provider = provider;
        _svc = svc;
        _settingsFileName = settingsFileName;
        _loc = localization;
        WindowTitle = $"{(string.IsNullOrWhiteSpace(info.Manifest.Name) ? info.Slug : info.Manifest.Name)} Settings";
        InitializeComponent();
        Localization.Loc.ApplyTo(this);
        // This dialog titles from its own WindowTitle property (not Window.Title) and hides
        // the min/max buttons: rebind/adjust the shared title bar accordingly.
        TitleBar.ShowMaximize = false;
        TitleBar.ShowMinimize = false;
        TitleBar.SetBinding(Wpf.Ui.Controls.TitleBar.TitleProperty,
            new System.Windows.Data.Binding(nameof(WindowTitle)) { Source = this });
        BuildSettings();

        // Re-translate setting labels/options on a host language switch (the rows are built
        // in code, not {loc:LocKey …} targets). Detached on close.
        Localization.Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Localization.Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void BuildSettings()
    {
        SettingsHost.Children.Clear();

        // Theme first, then the widget's own settings in declaration order. The host skips the
        // theme row entirely for a widget that does not set supportsTheme, so a widget that
        // never themes itself does not grow a dead dropdown.
        var theme = _provider.Settings.Where(s => s.Kind == WidgetSettingKind.Theme).ToList();
        var rest = _provider.Settings.Where(s => s.Kind != WidgetSettingKind.Theme).ToList();
        foreach (var setting in theme.Concat(rest))
        {
            if (setting.Kind == WidgetSettingKind.Theme && _info?.Manifest.SupportsTheme != true) continue;
            _initial[setting.Name] = setting.Value;
            SettingsHost.Children.Add(BuildRow(setting));
        }

        if (SettingsHost.Children.Count == 0)
        {
            SettingsHost.Children.Add(new TextBlock
            {
                Text = Localization.Loc.Get("WidgetSettings.NoSettings"),
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private void OnLanguageChanged()
    {
        try { BuildSettings(); }
        catch { }
    }

    /// <summary>Localizes a setting label/description through the widget's own langs/, falling
    /// back to the plugin's English string. Key = the English string itself.</summary>
    private string T(string? english)
        => _loc?.Get(english ?? string.Empty, english ?? string.Empty) ?? (english ?? string.Empty);

    private FrameworkElement BuildRow(WidgetSetting setting)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 14) };
        // The theme row is host chrome: the SDK declares it, so its wording is shared by every
        // themable widget and lives in the app's Langs rather than being repeated in each
        // widget's langs/. Everything else is the widget's own English text, resolved through
        // the widget's localization.
        bool isTheme = setting.Kind == WidgetSettingKind.Theme;
        var name = new TextBlock
        {
            Text = isTheme
                ? Localization.Loc.Get("WidgetSettings.Theme")
                : string.IsNullOrWhiteSpace(setting.DisplayName) ? setting.Name : T(setting.DisplayName),
            FontWeight = FontWeights.SemiBold,
            ToolTip = setting.Name,
        };
        panel.Children.Add(name);
        if (isTheme)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Localization.Loc.Get("WidgetSettings.ThemeDescription"),
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });
        }
        else if (!string.IsNullOrWhiteSpace(setting.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = T(setting.Description),
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4),
            });
        }

        switch (setting.Kind)
        {
            case WidgetSettingKind.Boolean:
                var check = new CheckBox
                {
                    Content = Localization.Loc.Get("WidgetSettings.Enabled"),
                    IsChecked = setting.GetBoolean(),
                    Margin = new Thickness(0, 2, 0, 0),
                };
                check.Checked += (_, _) => { try { setting.Value = true; } catch { } };
                check.Unchecked += (_, _) => { try { setting.Value = false; } catch { } };
                panel.Children.Add(check);
                break;
            case WidgetSettingKind.ListOfStrings:
                // Project key→English-label into key→translated-label so the dropdown shows
                // the active language while SelectedValuePath still binds the stable key.
                var options = setting.ListOfAvailableStrings?
                    .Select(kv => new KeyValuePair<string, string>(kv.Key, T(kv.Value)))
                    .ToList();
                var combo = new ComboBox
                {
                    Margin = new Thickness(0, 2, 0, 0),
                    ItemsSource = options,
                    DisplayMemberPath = "Value",
                    SelectedValuePath = "Key",
                    SelectedValue = setting.GetString(),
                };
                combo.SelectionChanged += (_, _) =>
                {
                    try
                    {
                        if (combo.SelectedValue is string s) setting.Value = s;
                    }
                    catch
                    {
                        combo.SelectedValue = setting.GetString();
                    }
                };
                panel.Children.Add(combo);
                break;
            case WidgetSettingKind.Number:
                // The plugin picks the editor by supplying a range: both bounds => slider,
                // otherwise a numeric field with spin buttons, which is the right default for
                // a free number (a font size, an angle) that a 0..100 slider would distort.
                if (setting.HasRange)
                {
                    panel.Children.Add(BuildNumberSlider(setting));
                }
                else
                {
                    panel.Children.Add(BuildNumberBox(setting));
                }
                break;
            case WidgetSettingKind.Theme:
                panel.Children.Add(BuildThemeCombo(setting));
                break;
            case WidgetSettingKind.ColorH:
                panel.Children.Add(BuildColorPicker(setting));
                break;
            default:
                var text = new TextBox
                {
                    Text = setting.GetString(),
                    Margin = new Thickness(0, 2, 0, 0),
                };
                text.TextChanged += (_, _) => { try { setting.Value = text.Text; } catch { } };
                panel.Children.Add(text);
                break;
        }

        return panel;
    }

    /// <summary>Colour editor for <see cref="WidgetSettingKind.ColorH"/>: live swatch, direct
    /// <c>#AARRGGBB</c> entry, and a preset row. Built in code rather than pulling in a picker
    /// control — WPF has no built-in colour dialog (WinForms' <c>ColorDialog</c> is not
    /// referenced) and Wpf.Ui 4.3 ships none, so a dependency would be the only way to get
    /// one. Typing the hex is also the fastest route for anyone who knows the value.</summary>
    private FrameworkElement BuildNumberSlider(WidgetSetting setting)
    {
        var box = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        double lo = setting.Minimum!.Value;
        double hi = setting.Maximum!.Value;
        var slider = new System.Windows.Controls.Slider
        {
            Minimum = lo,
            Maximum = hi,
            Value = Math.Min(Math.Max(setting.GetNumber(), lo), hi),
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Localization.Loc.Get("WidgetSettings.Number"),
        };
        var readout = new TextBlock
        {
            Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44,
            Opacity = 0.8,
        };
        slider.ValueChanged += (_, _) =>
        {
            readout.Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture);
            try { setting.Value = Math.Round(slider.Value, 2); } catch { }
        };
        box.Children.Add(slider);
        box.Children.Add(readout);
        return box;
    }

    private FrameworkElement BuildNumberBox(WidgetSetting setting)
    {
        var number = new Wpf.Ui.Controls.NumberBox
        {
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = Localization.Loc.Get("WidgetSettings.Number"),
            ValidationMode = Wpf.Ui.Controls.NumberBoxValidationMode.InvalidInputOverwritten,
            NumberFormatter = new Wpf.Ui.Controls.ValidateNumberFormatter(),
        };
        try { number.Value = setting.GetNumber(); } catch { }
        if (setting.HasRange)
        {
            number.Minimum = setting.Minimum!.Value;
            number.Maximum = setting.Maximum!.Value;
        }
        number.ValueChanged += (_, _) =>
        {
            if (number.Value is double d) { try { setting.Value = d; } catch { } }
        };
        return number;
    }

    private FrameworkElement BuildThemeCombo(WidgetSetting setting)
    {
        // Options are built here rather than on the setting because "Auto" is a manifest
        // decision: it is only meaningful for a widget that paints its own background and so
        // can derive a theme from it.
        var options = new List<KeyValuePair<string, string>>
        {
            new(ThemeKeys.FollowApp, Localization.Loc.Get("WidgetSettings.FollowApp")),
            new(ThemeKeys.Light, Localization.Loc.Get("WidgetSettings.Light")),
            new(ThemeKeys.Dark, Localization.Loc.Get("WidgetSettings.Dark")),
        };
        if (_info?.Manifest.AutoTheme == true)
        {
            options.Add(new KeyValuePair<string, string>(
                ThemeKeys.Auto, Localization.Loc.Get("WidgetSettings.AutoBackground")));
        }

        var combo = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 0),
            ItemsSource = options,
            DisplayMemberPath = "Value",
            SelectedValuePath = "Key",
            SelectedValue = ThemeKeys.IsValid(setting.GetString()) ? setting.GetString() : ThemeKeys.FollowApp,
        };
        combo.SelectionChanged += (_, _) =>
        {
            try
            {
                if (combo.SelectedValue is string s && ThemeKeys.IsValid(s)) setting.Value = s;
            }
            catch { combo.SelectedValue = setting.GetString(); }
        };
        return combo;
    }

    private FrameworkElement BuildColorPicker(WidgetSetting setting)
    {
        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 2, 0, 0) };

        // A null value means "the widget decides" — a colour derived from its own theme. It is
        // a first-class state rather than an error, so the box starts empty with a hint, the
        // swatch shows a neutral "unset" fill, and Reset returns to it.
        bool IsUnset() => setting.Value is null;

        var swatch = new Border
        {
            Width = 44,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.GrayTextBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Localization.Loc.Get("WidgetSettings.PickColor"),
        };

        // Wpf.Ui's TextBox for its PlaceholderText; a plain WPF TextBox has no hint.
        var hex = new Wpf.Ui.Controls.TextBox
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = Localization.Loc.Get("WidgetSettings.ThemeDefault"),
            ToolTip = Localization.Loc.Get("WidgetSettings.ColorHint"),
        };

        var reset = new Wpf.Ui.Controls.Button
        {
            Content = Localization.Loc.Get("WidgetSettings.Reset"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            ToolTip = Localization.Loc.Get("WidgetSettings.ResetColor"),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(hex);
        row.Children.Add(reset);
        root.Children.Add(row);

        void ApplyPaint()
        {
            // Empty is the unset state, not an invalid colour: no red border, neutral swatch.
            if (string.IsNullOrWhiteSpace(hex.Text))
            {
                swatch.Background = new SolidColorBrush(
                    SystemColors.ControlLightBrush is System.Windows.Media.SolidColorBrush s
                        ? Color.FromArgb(0x40, s.Color.R, s.Color.G, s.Color.B)
                        : Colors.LightGray);
                hex.BorderBrush = System.Windows.Media.Brushes.Transparent;
                return;
            }
            if (WidgetSetting.TryNormalizeColorHex(hex.Text, out string normalized))
            {
                try { swatch.Background = new SolidColorBrush(ParseHexColor(normalized)); }
                catch { }
                hex.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
            else
            {
                swatch.Background = System.Windows.Media.Brushes.Transparent;
                hex.BorderBrush = System.Windows.Media.Brushes.IndianRed;
            }
        }

        hex.Text = IsUnset() ? "" : setting.GetColorHex();
        hex.TextChanged += (_, _) =>
        {
            ApplyPaint();
            if (WidgetSetting.TryNormalizeColorHex(hex.Text, out string normalized))
            {
                try { setting.Value = normalized; } catch { }
            }
        };
        reset.Click += (_, _) =>
        {
            try { setting.Value = null; } catch { }
            hex.Text = "";
            ApplyPaint();
        };
        // Swatch opens the app's own picker. Starting colour is the current value, or mid grey
        // when unset — a neutral default the user is far more likely to adjust deliberately
        // than "transparent", which would look like a bug on open.
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                var initial = IsUnset()
                    ? Color.FromRgb(0x80, 0x80, 0x80)
                    : ParseHexColor(setting.GetColorHex());
                var owner = Window.GetWindow(this);
                var dialog = owner is null
                    ? new Maktaby.Views.Dialogs.ColorPickerDialog(initial)
                    : new Maktaby.Views.Dialogs.ColorPickerDialog(owner, initial);
                if (dialog.ShowDialog() != true) return;
                var picked = dialog.SelectedColor;
                // Always store the full 8-digit form: this kind is #AARRGGBB and the picker
                // emits a 6-digit value for opaque colours, which would lose the alpha channel
                // on the way to storage.
                string stored = $"#{picked.A:X2}{picked.R:X2}{picked.G:X2}{picked.B:X2}";
                setting.Value = stored;
                hex.Text = stored;
                ApplyPaint();
            }
            catch { }
        };
        ApplyPaint();

        // Presets: opaque and translucent variants of the two neutrals a widget background is
        // almost always painted in, plus fully transparent to mean "no background".
        string[] presets =
        {
            "#00000000", "#B3161A1E", "#F21A1C1E", "#66151B20",
            "#B3FFFFFF", "#F2FFFFFF", "#66151B20", "#00000000",
        };
        var strip = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (string preset in presets.Distinct())
        {
            var chip = new Border
            {
                Width = 26,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.GrayTextBrush,
                Background = new SolidColorBrush(ParseHexColor(preset)),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = preset,
                Tag = NoDragTagValue,
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                try { setting.Value = preset; } catch { }
                hex.Text = setting.GetColorHex(preset);
                ApplyPaint();
            };
            strip.Children.Add(chip);
        }
        root.Children.Add(strip);

        return root;
    }

    private static System.Windows.Media.Color ParseHexColor(string hex)
    {
        if (!WidgetSetting.TryNormalizeColorHex(hex, out string normalized)) return System.Windows.Media.Colors.Transparent;
        try
        {
            uint v = Convert.ToUInt32(normalized.Substring(1), 16);
            return System.Windows.Media.Color.FromArgb(
                (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        catch { return System.Windows.Media.Colors.Transparent; }
    }

    /// <summary>Mirrors <see cref="Maktaby.WidgetSdk.NativeWidgetControl.NoDragTag"/>. Copied
    /// rather than referenced: these are chrome rows hosted by the app, not plugin visuals, and
    /// referencing the SDK constant here would couple the settings dialog to the plugin model.</summary>
    private const string NoDragTagValue = "no-drag";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { _svc.Save(_info, _provider, _settingsFileName); } catch { }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Revert live-applied edits to the snapshot taken on open.
        foreach (var setting in _provider.Settings)
        {
            try
            {
                if (_initial.TryGetValue(setting.Name, out var v)) setting.Value = v;
            }
            catch { }
        }

        DialogResult = false;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var setting in _provider.Settings)
        {
            try { setting.Reset(); } catch { }
        }
        // Rebuild editors so they show the restored defaults.
        SettingsHost.Children.Clear();
        foreach (var setting in _provider.Settings)
        {
            SettingsHost.Children.Add(BuildRow(setting));
        }
    }
}
