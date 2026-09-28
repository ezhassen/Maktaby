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
    private readonly Dictionary<string, object?> _initial = new();

    public string WindowTitle { get; }

    /// <summary>Parameterless for the VS/Blend designer: sample settings of every kind
    /// rendered through the real row builder.</summary>
    public WidgetSettingsWindow()
        : this(
            new NativeWidgetInfo("analog-clock", NativeWidgetSource.App, "", new NativeWidgetManifest { Name = "Analog Clock" }, null, null),
            new DesignSettingsProvider(),
            null!)
    {
    }

    private sealed class DesignSettingsProvider : IWidgetSettingsProvider
    {
        public IReadOnlyList<WidgetSetting> Settings { get; } = new List<WidgetSetting>
        {
            new("title", "Title", "Header text shown on the widget.", WidgetSettingKind.String, () => { }, "Analog Clock"),
            new("size", "Size", "Face diameter in pixels.", WidgetSettingKind.Number, () => { }, 300.0),
            new("showSeconds", "Show seconds", "Sweep the second hand.", WidgetSettingKind.Boolean, () => { }, true),
            new("handStyle", "Hand style", "Which hands to draw.", WidgetSettingKind.ListOfStrings, () => { }, "smooth",
                new Dictionary<string, string> { ["hide"] = "Hide", ["smooth"] = "Smooth sweep", ["step"] = "Step once per second" }),
        };
    }

    public WidgetSettingsWindow(NativeWidgetInfo info, IWidgetSettingsProvider provider, INativeWidgetSettingsService svc)
    {
        _info = info;
        _provider = provider;
        _svc = svc;
        WindowTitle = $"{(string.IsNullOrWhiteSpace(info.Manifest.Name) ? info.Slug : info.Manifest.Name)} Settings";
        InitializeComponent();
        // This dialog titles from its own WindowTitle property (not Window.Title) and hides
        // the min/max buttons: rebind/adjust the shared title bar accordingly.
        TitleBar.ShowMaximize = false;
        TitleBar.ShowMinimize = false;
        TitleBar.SetBinding(Wpf.Ui.Controls.TitleBar.TitleProperty,
            new System.Windows.Data.Binding(nameof(WindowTitle)) { Source = this });
        foreach (var setting in provider.Settings)
        {
            _initial[setting.Name] = setting.Value;
            SettingsHost.Children.Add(BuildRow(setting));
        }

        if (provider.Settings.Count == 0)
        {
            SettingsHost.Children.Add(new TextBlock
            {
                Text = "This widget has no settings.",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private static FrameworkElement BuildRow(WidgetSetting setting)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 14) };
        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(setting.DisplayName) ? setting.Name : setting.DisplayName,
            FontWeight = FontWeights.SemiBold,
            ToolTip = setting.Name,
        };
        panel.Children.Add(name);
        if (!string.IsNullOrWhiteSpace(setting.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = setting.Description,
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
                    Content = "Enabled",
                    IsChecked = setting.GetBoolean(),
                    Margin = new Thickness(0, 2, 0, 0),
                };
                check.Checked += (_, _) => { try { setting.Value = true; } catch { } };
                check.Unchecked += (_, _) => { try { setting.Value = false; } catch { } };
                panel.Children.Add(check);
                break;
            case WidgetSettingKind.ListOfStrings:
                var combo = new ComboBox
                {
                    Margin = new Thickness(0, 2, 0, 0),
                    ItemsSource = setting.ListOfAvailableStrings,
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
                var num = new TextBox
                {
                    Text = setting.Value?.ToString() ?? "",
                    Margin = new Thickness(0, 2, 0, 0),
                    ToolTip = "Number",
                };
                num.TextChanged += (_, _) =>
                {
                    if (double.TryParse(num.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        try { setting.Value = d; num.ClearValue(Border.BorderBrushProperty); } catch { }
                    }
                    else
                    {
                        num.BorderBrush = Brushes.IndianRed;
                    }
                };
                panel.Children.Add(num);
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { _svc.Save(_info, _provider); } catch { }
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
