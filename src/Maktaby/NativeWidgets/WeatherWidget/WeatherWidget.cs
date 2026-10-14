using Maktaby.WidgetSdk;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

// The host compiler injects "global using System.IO" (NativeWidgetService.ImplicitUsings),
// which collides with the WPF shape of the same name. An alias outranks an imported type,
// so this resolves Path to the shape everywhere without touching the other geometry names.
using Path = System.Windows.Shapes.Path;

namespace WeatherWidget;

/// <summary>Built-in weather widget (source mode). Three views - now / next 24 h / next
/// 4 days - switchable from inside the widget, fed by Open-Meteo (no API key, no
/// telemetry). Location defaults to an IP lookup once per process and can be overridden
/// with a city name that is geocoded through Open-Meteo's own search endpoint.
///
/// Everything is drawn in code on a fixed 340x260 canvas inside a Viewbox, like
/// NativeClock, so the widget stays proportional at any window size. No BitmapCache
/// anywhere (see ClockWidget.cs:69 for why) and no Effect on the hot path.
/// </summary>
public sealed class WeatherWidget : NativeWidgetControl
{
    private const double DesignW = 340;
    private const double DesignH = 260;

    // Theme-matched default cards, used while the user has not chosen a colour of their own.
    private const string DarkCard = "#B3161A1E";
    private const string LightCard = "#E3E7EC";

    private const string GeoUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    private const string IpUrl = "https://ipwho.is/";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    // Weather icon geometry, keyed by ((long)code << 1) | isDay. Bounded by the WMO code
    // set, so it never grows.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Geometry> GeometryCache = new();

    // Shared, never disposed: one HttpClient per process, not per widget instance.
    // Every loaded instance gets its own CancellationTokenSource, so disposing a
    // widget cancels only its own in-flight request.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Process-wide so a hide/show toggle (which destroys and recreates the host window
    // every cycle) does not re-geocode or re-hit the IP service on every load.
    private static readonly Dictionary<string, (double Lat, double Lng)> GeoCache = new();
    private static readonly object GeoCacheLock = new();
    private static double? DetectedLat;
    private static double? DetectedLng;
    private static string? DetectedName;

    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;

    private int _generation;
    private bool _disposed;
    private bool _isFetching;

    private string _view = "now";
    private string _units = "metric";
    private string _city = "";
    private bool _resolvedThemeIsLight;
    private string? _lastHostTheme;

    private Snapshot? _snapshot;
    private DateTimeOffset _fetchedUtc;
    private bool _hasData;

    // ---- visuals ----
    private readonly Border _background = new() { CornerRadius = new CornerRadius(10) };
    private StackPanel _headerButtons = null!;
    private StackPanel _tabPanel = null!;
    private readonly TextBlock _locationText = new();
    private readonly TextBlock _statusText = new();
    private Border _statusPill = null!;
    private readonly List<Border> _tabs = new();
    private readonly List<TextBlock> _tabLabels = new();
    private readonly List<Path> _buttonIcons = new();
    private readonly List<Path> _smallIcons = new();
    private Grid _nowPanel = null!;
    private Grid _hourPanel = null!;
    private Grid _dayPanel = null!;

    private readonly TextBlock _tempText = new();
    private readonly TextBlock _condText = new();
    private readonly TextBlock _hiloText = new();
    private readonly Path _nowIcon = new();
    private readonly List<TextBlock> _metricLabels = new();
    private readonly List<TextBlock> _metricValues = new();

    private readonly Polyline _spark = new();
    private readonly Path _sparkFill = new();
    private readonly List<TextBlock> _hourLabels = new();
    private readonly List<Path> _hourIcons = new();
    private readonly List<TextBlock> _hourTemps = new();
    private readonly List<TextBlock> _hourPrecip = new();

    private readonly List<TextBlock> _dayLabels = new();
    private readonly List<Path> _dayIcons = new();
    private readonly List<TextBlock> _dayHi = new();
    private readonly List<TextBlock> _dayLo = new();
    private readonly List<TextBlock> _dayPrecip = new();

    // Theme brushes, rebuilt in OnApplyTheme and reused by every render pass.
    private SolidColorBrush _fg = new(Colors.White);
    private SolidColorBrush _fgDim = new(Colors.Gray);
    private SolidColorBrush _accent = new(Colors.SkyBlue);
    private SolidColorBrush _chipBg = new(Colors.Black);
    private SolidColorBrush _chipSelectedBg = new(Colors.SteelBlue);
    private SolidColorBrush _warn = new(Colors.Orange);
    private SolidColorBrush _sparkFillBrush = new(Colors.Transparent);

    public WeatherWidget()
    {
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += OnTimerTick;

        BuildVisual();
        DefineSettings();

        // Paint correctly before the host pushes a theme (mirrors ClockWidget.cs:206).
        OnApplyTheme(null);
        ApplyView();
        ApplyBackground();
        // Starts hidden: the host pushes the real state on activation, and until then the
        // widget should look like data, not like controls.
        ApplyInteractiveVisibility();
        // Render the no-data state immediately so the widget is never blank between the
        // host showing it and the first fetch landing.
        Render();

        // Start on load, never in the ctor: the gallery preview host constructs widgets it
        // never shows, and a constructed-but-unloaded widget must not hit the network.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    #region Settings

    private void DefineSettings()
    {
        DefineSetting("city", "City", "City to show. Leave empty to detect automatically.",
            WidgetSettingKind.String, OnCityChanged, defaultValue: "");
        _city = FindSetting("city")?.GetString() ?? "";
        //
        DefineSetting("units", "Units", "Temperature and wind units",
            WidgetSettingKind.ListOfStrings, OnUnitsChanged, defaultValue: "metric",
            listOfAvailableStrings: new Dictionary<string, string>
            {
                ["metric"] = "Metric (°C, km/h)",
                ["imperial"] = "Imperial (°F, mph)",
            });
        _units = FindSetting("units")?.GetString() ?? "metric";
        //
        // The in-widget tab bar writes straight back to this setting, so the chosen view
        // persists per placed widget without a second storage path.
        DefineSetting("view", "Default view", "View shown when the widget opens",
            WidgetSettingKind.ListOfStrings, OnViewSettingChanged, defaultValue: "now",
            listOfAvailableStrings: new Dictionary<string, string>
            {
                ["now"] = "Current conditions",
                ["hour"] = "Next 24 hours",
                ["day"] = "Next 4 days",
            });
        _view = FindSetting("view")?.GetString() ?? "now";
        //
        DefineSetting("backgroundColor", "Background colour",
            "Widget background card. Left unset, a card matching the current theme is used.",
            WidgetSettingKind.ColorH, OnBackgroundChanged, defaultValue: null);
        //
        DefineSetting("opacity", "Background opacity", "How strongly the background colour is applied (0-100%)",
            WidgetSettingKind.Number, OnBackgroundChanged, defaultValue: 80, minimum: 0, maximum: 100);
    }

    /// <summary>True when the user has chosen a background colour, as opposed to inheriting the
    /// theme-derived default.</summary>
    private bool HasUserBackground() => UserBackgroundHex() is not null;

    private string? UserBackgroundHex()
    {
        string hex = FindSetting("backgroundColor")?.GetString() ?? "";
        return WidgetSetting.TryNormalizeColorHex(hex, out string normalized) ? normalized : null;
    }

    /// <summary>Whether the USER's background reads as a light card or a dark one, from
    /// relative luminance of the chosen colour. Reads the setting, never
    /// <c>_background.Background</c>: that brush may hold the theme-derived default card,
    /// which is exactly the circular read this avoids. Alpha is ignored — a translucent colour
    /// still reads as whatever hue it tints.</summary>
    private bool IsBackgroundLight()
    {
        try
        {
            if (UserBackgroundHex() is not string hex) return _resolvedThemeIsLight;
            uint v = Convert.ToUInt32(hex.Substring(1), 16);
            double r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 > 0.5;
        }
        catch { return _resolvedThemeIsLight; }
    }

    private void OnBackgroundChanged()
    {
        ApplyBackground();
        // Re-derive whenever the colour changes under "auto" — INCLUDING clearing it back to
        // null. Gating this on "do we have a user colour" would strand the widget on the
        // derived theme after a reset, with a theme-derived background and a theme that was
        // derived from the old one. With no colour, OnApplyTheme falls back to the theme the
        // host last pushed, which is exactly the desired behaviour.
        if (ThemeSettingValue == ThemeKeys.Auto)
        {
            OnApplyTheme(_lastHostTheme);
        }
    }

    /// <summary>Paints the background: the user's colour when they picked one, otherwise a card
    /// that matches the current theme. The default is derived rather than hardcoded — a fixed
    /// hex made the widget ship a dark card inside a light app, which is the one thing a
    /// theme-following widget must never do. Alpha is scaled by the opacity slider; the two are
    /// multiplied so dropping the slider to 0 and raising it again restores the original alpha
    /// instead of compounding.</summary>
    private void ApplyBackground()
    {
        try
        {
            string hex = FindSetting("backgroundColor")?.GetString() ?? "";
            if (!WidgetSetting.TryNormalizeColorHex(hex, out string candidate))
            {
                candidate = _resolvedThemeIsLight ? LightCard : DarkCard;
            }
            // Normalize AGAIN, unconditionally: the theme defaults above are written as
            // six-digit #RRGGBB, which parses as alpha 00 if handed straight to
            // Convert.ToUInt32 — the card would paint fully transparent.
            if (!WidgetSetting.TryNormalizeColorHex(candidate, out string normalized)) return;
            uint v = Convert.ToUInt32(normalized.Substring(1), 16);
            double scale = Math.Max(0, Math.Min(100, GetSettingsValue("opacity", 80.0))) / 100.0;
            byte a = (byte)Math.Round((v >> 24) * scale);
            _background.Background = new SolidColorBrush(
                Color.FromArgb(a, (byte)(v >> 16), (byte)(v >> 8), (byte)v));
        }
        catch { }
    }

    /// <summary>Interactive chrome AND the two status readouts are hidden until the user engages
    /// with the widget: the resting state should be just the weather. The host pushes the
    /// signal, which covers more than window activation — a drag or resize leaves the widget
    /// window inactive while the user is plainly using it, so the controls stay up for the
    /// whole gesture instead of disappearing under the cursor.</summary>
    protected override void OnHostInteractionChanged() => ApplyInteractiveVisibility();

    private void ApplyInteractiveVisibility()
    {
        try
        {
            var v = IsHostEngaged ? Visibility.Visible : Visibility.Collapsed;
            _tabPanel.Visibility = v;
            _headerButtons.Visibility = v;
            _locationText.Visibility = v;
            _statusPill.Visibility = v;
        }
        catch { }
    }

    private void OnCityChanged()
    {
        _city = FindSetting("city")?.GetString() ?? "";
        Invalidate();
    }

    private void OnUnitsChanged()
    {
        _units = FindSetting("units")?.GetString() ?? "metric";
        Render();
    }

    private void OnViewSettingChanged()
    {
        _view = FindSetting("view")?.GetString() ?? "now";
        ApplyView();
    }

    #endregion

    #region Visual construction

    private void BuildVisual()
    {
        var root = new Grid { Width = DesignW, Height = DesignH };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });   // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });   // tabs
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(186) });  // content
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });   // status

        // First child = painted behind everything. Also the hit-test surface for the whole
        // canvas, so drag and right-click work on any pixel even at zero opacity.
        root.Children.Add(BuildHeader());
        root.Children.Add(BuildTabs());
        root.Children.Add(BuildContent());
        root.Children.Add(BuildStatus());

        // The background wraps the Viewbox rather than living inside the design canvas: the
        // canvas is a fixed 340x260 centred by Stretch.Uniform, so a background painted
        // inside it leaves letterbox bands transparent whenever the window is a different
        // aspect ratio. Out here it covers the whole control at any size.
        _background.Child = new Viewbox { Stretch = Stretch.Uniform, Child = root };
        Content = _background;
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(12, 6, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _locationText.FontSize = 14;
        _locationText.FontWeight = FontWeights.SemiBold;
        _locationText.VerticalAlignment = VerticalAlignment.Center;
        _locationText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_locationText, 0);
        grid.Children.Add(_locationText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(MakeIconButton(RefreshGeometry(), L("Refresh now", "Refresh now"), OnRefreshClicked));
        buttons.Children.Add(MakeIconButton(LocateGeometry(), L("Detect my location", "Detect my location"), OnDetectClicked));
        _headerButtons = buttons;
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        Grid.SetRow(grid, 0);
        return grid;
    }

    private UIElement BuildTabs()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(MakeTab("now", "Now", "Now"));
        panel.Children.Add(MakeTab("hour", "Hour", "Next 24 hours"));
        panel.Children.Add(MakeTab("day", "Day", L("Next 4 days", "Next 4 days")));

        _tabPanel = panel;
        Grid.SetRow(panel, 1);
        return panel;
    }

    private UIElement BuildContent()
    {
        var host = new Grid { Margin = new Thickness(12, 2, 12, 0) };
        _nowPanel = BuildNowPanel();
        _hourPanel = BuildHourPanel();
        _dayPanel = BuildDayPanel();
        host.Children.Add(_nowPanel);
        host.Children.Add(_hourPanel);
        host.Children.Add(_dayPanel);
        Grid.SetRow(host, 2);
        return host;
    }

    private Grid BuildNowPanel()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) }); // metrics

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBox = new Viewbox
        {
            Width = 74,
            Height = 74,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _nowIcon.Data = BuildWeatherGeometry(0, true);
        iconBox.Child = _nowIcon;
        Grid.SetColumn(iconBox, 0);
        top.Children.Add(iconBox);

        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        _tempText.FontSize = 42;
        _tempText.FontWeight = FontWeights.Bold;
        _tempText.Text = "--";
        _condText.FontSize = 13;
        _condText.TextTrimming = TextTrimming.CharacterEllipsis;
        _hiloText.FontSize = 12;
        texts.Children.Add(_tempText);
        texts.Children.Add(_condText);
        texts.Children.Add(_hiloText);
        Grid.SetColumn(texts, 1);
        top.Children.Add(texts);

        panel.Children.Add(top);

        var metrics = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.Children.Add(MakeMetric(0, "Feels like", "Feels like"));
        metrics.Children.Add(MakeMetric(1, "Humidity", "Humidity"));
        metrics.Children.Add(MakeMetric(2, "Wind", "Wind"));
        panel.Children.Add(metrics);

        return panel;
    }

    private UIElement MakeMetric(int column, string labelKey, string labelDefault)
    {
        var stack = new StackPanel();
        var label = new TextBlock { FontSize = 9.5, Text = L(labelKey, labelDefault) };
        var value = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Text = "--" };
        _metricLabels.Add(label);
        _metricValues.Add(value);
        stack.Children.Add(label);
        stack.Children.Add(value);
        Grid.SetColumn(stack, column);
        return stack;
    }

    private Grid BuildHourPanel()
    {
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });  // sparkline
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // cells

        var chart = new Grid();
        chart.Children.Add(_sparkFill);
        _sparkFill.Data = null;
        _sparkFill.Fill = _sparkFillBrush;
        _spark.StrokeThickness = 2;
        _spark.StrokeEndLineCap = PenLineCap.Round;
        _spark.StrokeStartLineCap = PenLineCap.Round;
        chart.Children.Add(_spark);
        Grid.SetRow(chart, 0);
        panel.Children.Add(chart);

        var cells = new Grid();
        cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 1; i < 8; i++) cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < 8; i++)
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            var hour = new TextBlock { FontSize = 9.5, Text = "--" };
            var icon = new Viewbox { Width = 26, Height = 26, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 0, 2) };
            var path = new Path { Data = BuildWeatherGeometry(0, true) };
            icon.Child = path;
            _smallIcons.Add(path);
            var temp = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Text = "--" };
            var precip = new TextBlock { FontSize = 8.5, Text = "" };

            cell.Children.Add(hour);
            cell.Children.Add(icon);
            cell.Children.Add(temp);
            cell.Children.Add(precip);

            _hourLabels.Add(hour);
            _hourIcons.Add(path);
            _hourTemps.Add(temp);
            _hourPrecip.Add(precip);
            Grid.SetColumn(cell, i);
            cells.Children.Add(cell);
        }
        // Must be placed explicitly: without SetRow both the chart and the cells land in
        // row 0 and the strip is drawn straight over the sparkline.
        Grid.SetRow(cells, 1);
        panel.Children.Add(cells);

        return panel;
    }

    private Grid BuildDayPanel()
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 1; i < 4; i++) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < 4; i++)
        {
            var cell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var day = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Text = "--", HorizontalAlignment = HorizontalAlignment.Center };
            var icon = new Viewbox { Width = 40, Height = 40, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 4) };
            var path = new Path { Data = BuildWeatherGeometry(0, true) };
            icon.Child = path;
            _smallIcons.Add(path);
            var hi = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Text = "--", HorizontalAlignment = HorizontalAlignment.Center };
            var lo = new TextBlock { FontSize = 11, Text = "--", HorizontalAlignment = HorizontalAlignment.Center };
            var precip = new TextBlock { FontSize = 8.5, Text = "", HorizontalAlignment = HorizontalAlignment.Center };

            cell.Children.Add(day);
            cell.Children.Add(icon);
            cell.Children.Add(hi);
            cell.Children.Add(lo);
            cell.Children.Add(precip);

            _dayLabels.Add(day);
            _dayIcons.Add(path);
            _dayHi.Add(hi);
            _dayLo.Add(lo);
            _dayPrecip.Add(precip);
            Grid.SetColumn(cell, i);
            panel.Children.Add(cell);
        }

        return panel;
    }

    private UIElement BuildStatus()
    {
        _statusText.FontSize = 9.5;
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;

        _statusPill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(12, 0, 12, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _statusText,
        };
        Grid.SetRow(_statusPill, 3);
        return _statusPill;
    }

    private Border MakeIconButton(Geometry geometry, string tooltip, Action onClick)
    {
        // StrokeThickness matters here: the refresh arc and locate ticks are zero-area
        // lines, so they are only visible through the stroke, and this geometry is a
        // 24x24 box scaled into a 15px button.
        var path = new Path { Data = geometry, Stretch = Stretch.Uniform, Width = 15, Height = 15, StrokeThickness = 2 };

        var border = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            // Pressing a button must not start a window drag (NativeWidgetControl's
            // automatic move-on-mouse-down skips anything tagged NoDragTag).
            Tag = NoDragTag,
            Child = new Viewbox { Width = 15, Height = 15, Stretch = Stretch.Uniform, Child = path },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Hover/press feedback without a ControlTemplate: the buttons are Borders, so the
        // default Button chrome (and its heavy theme styling) is avoided entirely.
        border.MouseEnter += (_, _) => { border.Background = _chipBg; };
        border.MouseLeave += (_, _) => { border.Background = Brushes.Transparent; };
        border.MouseLeftButtonUp += (_, _) => onClick();
        _buttonIcons.Add(path);
        return border;
    }

    private Border MakeTab(string value, string labelKey, string labelDefault)
    {
        var label = new TextBlock { FontSize = 10.5, Text = L(labelKey, labelDefault) };
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(10, 2, 10, 3),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            // Same as the icon buttons: selecting a tab is a click, not a drag.
            Tag = NoDragTag,
            Child = label,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            // Writing the setting is the single source of truth for the view, so the tab
            // click and the persisted default can never disagree.
            var setting = FindSetting("view");
            if (setting is not null && setting.GetString() != value) setting.Value = value;
        };
        _tabs.Add(border);
        _tabLabels.Add(label);
        return border;
    }

    #endregion

    #region Data

    private sealed class HourPoint
    {
        public DateTime LocalTime;
        public double TempC;
        public int Code;
        public int PrecipProbability;
    }

    private sealed class DayPoint
    {
        public DateTime Date;
        public int Code;
        public double MaxC;
        public double MinC;
    }

    private sealed class Snapshot
    {
        public string LocationName = "";
        public double Lat;
        public double Lng;
        public double TempC;
        public double FeelsC;
        public double Humidity;
        public double WindKph;
        public double MaxC;
        public double MinC;
        public int Code;
        public bool IsDay;
        public double UtcOffsetSeconds;
        public List<HourPoint> Hours = new();
        public List<DayPoint> Days = new();
    }

    private static double Num(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out var d))
        {
            return d;
        }
        return double.NaN;
    }

    private static int Int(JsonElement parent, string name)
    {
        double d = Num(parent, name);
        return double.IsNaN(d) ? 0 : (int)Math.Round(d);
    }

    private static List<double> NumArray(JsonElement parent, string name)
    {
        var result = new List<double>();
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                result.Add(item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d) ? d : double.NaN);
            }
        }
        return result;
    }

    private static List<int> IntArray(JsonElement parent, string name)
    {
        var result = new List<int>();
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                result.Add(item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d) ? (int)Math.Round(d) : 0);
            }
        }
        return result;
    }

    private static List<string> StrArray(JsonElement parent, string name)
    {
        var result = new List<string>();
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "");
            }
        }
        return result;
    }

    private static async Task<Snapshot> FetchForecastAsync(double lat, double lng, CancellationToken token)
    {
        var sb = new StringBuilder(ForecastUrl);
        sb.Append("?latitude=").Append(lat.ToString("0.####", CultureInfo.InvariantCulture));
        sb.Append("&longitude=").Append(lng.ToString("0.####", CultureInfo.InvariantCulture));
        sb.Append("&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m");
        sb.Append("&hourly=temperature_2m,weather_code,precipitation_probability");
        sb.Append("&daily=weather_code,temperature_2m_max,temperature_2m_min");
        // forecast_hours starts at the current hour, so hourly[0] is "now" with no
        // timezone index math; forecast_days=5 gives today plus the 4 days we display.
        sb.Append("&forecast_hours=24&forecast_days=5");
        sb.Append("&timezone=auto");

        string json = await Http.GetStringAsync(sb.ToString(), token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Forecast response has no 'current' block.");
        }

        var snap = new Snapshot
        {
            TempC = Num(current, "temperature_2m"),
            FeelsC = Num(current, "apparent_temperature"),
            Humidity = Num(current, "relative_humidity_2m"),
            WindKph = Num(current, "wind_speed_10m"),
            Code = Int(current, "weather_code"),
            IsDay = Int(current, "is_day") == 1,
            UtcOffsetSeconds = Num(root, "utc_offset_seconds"),
            Lat = lat,
            Lng = lng,
        };
        if (double.IsNaN(snap.TempC)) throw new InvalidOperationException("Forecast response has no current temperature.");

        if (root.TryGetProperty("hourly", out var hourly) && hourly.ValueKind == JsonValueKind.Object)
        {
            var times = StrArray(hourly, "time");
            var temps = NumArray(hourly, "temperature_2m");
            var codes = IntArray(hourly, "weather_code");
            var precip = IntArray(hourly, "precipitation_probability");
            int n = Math.Min(times.Count, Math.Min(temps.Count, codes.Count));
            for (int i = 0; i < n; i++)
            {
                if (DateTime.TryParse(times[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) &&
                    !double.IsNaN(temps[i]))
                {
                    snap.Hours.Add(new HourPoint
                    {
                        LocalTime = t,
                        TempC = temps[i],
                        Code = codes[i],
                        PrecipProbability = i < precip.Count ? precip[i] : 0,
                    });
                }
            }
        }

        if (root.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Object)
        {
            var times = StrArray(daily, "time");
            var codes = IntArray(daily, "weather_code");
            var maxes = NumArray(daily, "temperature_2m_max");
            var mins = NumArray(daily, "temperature_2m_min");
            int n = Math.Min(times.Count, Math.Min(codes.Count, Math.Min(maxes.Count, mins.Count)));
            for (int i = 1; i < n; i++) // skip today, already shown by the Now view
            {
                if (DateTime.TryParse(times[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                {
                    snap.Days.Add(new DayPoint { Date = t, Code = codes[i], MaxC = maxes[i], MinC = mins[i] });
                }
            }
        }

        if (snap.Hours.Count > 0)
        {
            snap.MaxC = snap.Hours.Max(h => h.TempC);
            snap.MinC = snap.Hours.Min(h => h.TempC);
        }
        return snap;
    }

    private static async Task<(double Lat, double Lng, string Name)> GeocodeAsync(string city, CancellationToken token)
    {
        var sb = new StringBuilder(GeoUrl);
        sb.Append("?name=").Append(Uri.EscapeDataString(city));
        sb.Append("&count=1&language=en&format=json");

        string json = await Http.GetStringAsync(sb.ToString(), token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("CityNotFound");
        }
        var first = results[0];
        double lat = Num(first, "latitude");
        double lng = Num(first, "longitude");
        if (double.IsNaN(lat) || double.IsNaN(lng)) throw new InvalidOperationException("CityNotFound");

        string name = first.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;
        if (first.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.String)
        {
            string country = c.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(country) && !name.Contains(country, StringComparison.OrdinalIgnoreCase))
            {
                name = name + ", " + country;
            }
        }
        return (lat, lng, name);
    }

    private static async Task<(double Lat, double Lng, string Name)> DetectLocationAsync(CancellationToken token)
    {
        string json = await Http.GetStringAsync(IpUrl, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException("DetectFailed");
        }
        double lat = Num(root, "latitude");
        double lng = Num(root, "longitude");
        if (double.IsNaN(lat) || double.IsNaN(lng)) throw new InvalidOperationException("DetectFailed");

        string city = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(city)) city = "—";
        return (lat, lng, city);
    }

    private async Task<(double Lat, double Lng, string Name)> ResolveLocationAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(_city))
        {
            lock (GeoCacheLock)
            {
                if (GeoCache.TryGetValue(_city, out var cached)) return (cached.Lat, cached.Lng, _city);
            }
            var geo = await GeocodeAsync(_city, token).ConfigureAwait(false);
            lock (GeoCacheLock)
            {
                // Unbounded growth would need eviction; a desktop user has a handful of
                // cities, but keep it honest if a widget is recreated with new names often.
                if (GeoCache.Count > 64) GeoCache.Clear();
                GeoCache[_city] = (geo.Lat, geo.Lng);
            }
            return (geo.Lat, geo.Lng, geo.Name);
        }

        if (DetectedLat.HasValue && DetectedLng.HasValue)
        {
            return (DetectedLat.Value, DetectedLng.Value, DetectedName ?? "—");
        }

        var detected = await DetectLocationAsync(token).ConfigureAwait(false);
        DetectedLat = detected.Lat;
        DetectedLng = detected.Lng;
        DetectedName = detected.Name;
        return detected;
    }

    #endregion

    #region Refresh

    private async void OnTimerTick(object? sender, EventArgs e) => await RefreshAsync(force: false);

    // Named async-void handlers rather than `() => RefreshAsync(...)` lambdas typed as
    // Action: an Action-wrapped async lambda becomes async void with no place to catch,
    // and any escaping exception lands on the dispatcher (see agents.md, "Anything
    // scheduled as a callback that runs async work must be typed Func<Task>").
    private async void OnRefreshClicked()
    {
        try { await RefreshAsync(force: true); } catch { }
    }

    private async void OnDetectClicked()
    {
        try { await DetectLocationAsync(); } catch { }
    }

    private async Task RefreshAsync(bool force)
    {
        if (_disposed || IsSuspended) return;
        if (_isFetching) return;
        // Anything older than a full interval is stale; a forced pull refreshes regardless.
        if (!force && _hasData && DateTimeOffset.UtcNow - _fetchedUtc < RefreshInterval) return;

        var token = CancelInFlight();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _cts = cts;
        _isFetching = true;
        SetStatus(_hasData ? L("Updating", "Updating…") : L("Loading", "Loading…"), warn: false);

        int gen = _generation;
        try
        {
            var loc = await ResolveLocationAsync(cts.Token).ConfigureAwait(true);
            if (_disposed || gen != _generation) return;
            var snap = await FetchForecastAsync(loc.Lat, loc.Lng, cts.Token).ConfigureAwait(true);
            if (_disposed || gen != _generation) return;

            snap.LocationName = loc.Name;
            _snapshot = snap;
            _fetchedUtc = DateTimeOffset.UtcNow;
            _hasData = true;
            Render();
        }
        catch (OperationCanceledException)
        {
            // Suspend/dispose tore the request down; the next resume retries.
        }
        catch (Exception ex)
        {
            if (_disposed || gen != _generation) return;
            SetError(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            _isFetching = false;
        }
    }

    private async Task DetectLocationAsync()
    {
        if (_disposed || IsSuspended || _isFetching) return;

        var token = CancelInFlight();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _cts = cts;
        _isFetching = true;
        SetStatus(L("Detecting", "Detecting location…"), warn: false);

        int gen = _generation;
        try
        {
            var detected = await DetectLocationAsync(cts.Token).ConfigureAwait(true);
            if (_disposed || gen != _generation) return;

            // Store on the static cache so every future load skips the IP service, and
            // write the resolved city back into the setting so the user can edit or clear it.
            DetectedLat = detected.Lat;
            DetectedLng = detected.Lng;
            DetectedName = detected.Name;
            _city = detected.Name;
            var setting = FindSetting("city");
            if (setting is not null) setting.Value = detected.Name;

            // The setting write re-enters OnCityChanged -> Invalidate; the cleared static
            // pair above is what makes the next resolve use the fresh coordinates.
            await RefreshAsync(force: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (_disposed || gen != _generation) return;
            SetStatus(L("DetectFailed", "Could not detect your location"), warn: true);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            _isFetching = false;
        }
    }

    private CancellationToken CancelInFlight()
    {
        try { _cts?.Cancel(); } catch { }
        _generation++;
        return CancellationToken.None;
    }

    private void Invalidate()
    {
        _hasData = false;
        _snapshot = null;
        Render();
        _ = RefreshAsync(force: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsSuspended) return;
        _ = RefreshAsync(force: true);
        try { _timer.Start(); } catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { _timer.Stop(); } catch { }
        CancelInFlight();
    }

    #endregion

    #region Rendering

    private bool Metric => string.Equals(_units, "imperial", StringComparison.OrdinalIgnoreCase);

    private double Temp(double celsius) => Metric ? (celsius * 9.0 / 5.0 + 32.0) : celsius;
    private double Wind(double kph) => Metric ? kph : (kph / 1.609344);
    private string Unit => Metric ? "°F" : "°C";
    private string WindUnit => Metric ? "mph" : "km/h";

    private static string Fmt(double value)
    {
        if (double.IsNaN(value)) return "--";
        return Math.Round(value).ToString("0", CultureInfo.CurrentCulture);
    }

    private void ApplyView()
    {
        try
        {
            _nowPanel.Visibility = _view == "now" ? Visibility.Visible : Visibility.Collapsed;
            _hourPanel.Visibility = _view == "hour" ? Visibility.Visible : Visibility.Collapsed;
            _dayPanel.Visibility = _view == "day" ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < _tabs.Count; i++)
            {
                bool selected = i == TabIndexOf(_view);
                _tabs[i].Background = selected ? _chipSelectedBg : Brushes.Transparent;
                _tabLabels[i].Foreground = selected ? Brushes.White : _fgDim;
                _tabLabels[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        catch { }
    }

    /// <summary>Named TabIndexOf, not TabIndex: the latter hides <see cref="Control.TabIndex"/>
    /// from the inherited UserControl member and warns.</summary>
    private static int TabIndexOf(string view) => view switch
    {
        "hour" => 1,
        "day" => 2,
        _ => 0,
    };

    private void Render()
    {
        var snap = _snapshot;
        try
        {
            if (snap is null)
            {
                _locationText.Text = string.IsNullOrWhiteSpace(_city) ? L("Weather", "Weather") : _city;
                ClearContent();
                return;
            }

            _locationText.Text = snap.LocationName;

            // Now
            _tempText.Text = Fmt(Temp(snap.TempC)) + Unit;
            var condition = ConditionOf(snap.Code);
            _condText.Text = L(condition.Key, condition.Text);
            _nowIcon.Data = BuildWeatherGeometry(snap.Code, snap.IsDay);
            _hiloText.Text = L("HighLow", "H {0}  L {1}").Replace("{0}", Fmt(Temp(snap.MaxC))).Replace("{1}", Fmt(Temp(snap.MinC)));
            _metricValues[0].Text = Fmt(Temp(snap.FeelsC)) + Unit;
            _metricValues[1].Text = Fmt(snap.Humidity) + "%";
            _metricValues[2].Text = Fmt(Wind(snap.WindKph)) + " " + WindUnit;

            // Hourly: 8 cells at 3-hour steps, sparkline across all 24 points.
            var hours = snap.Hours;
            for (int i = 0; i < 8; i++)
            {
                int idx = i * 3;
                bool have = idx < hours.Count;
                var label = _hourLabels[i];
                var temp = _hourTemps[i];
                var precip = _hourPrecip[i];
                var icon = _hourIcons[i];
                if (have)
                {
                    var h = hours[idx];
                    label.Text = h.LocalTime.ToString("t", CultureInfo.CurrentCulture);
                    temp.Text = Fmt(Temp(h.TempC)) + "°";
                    precip.Text = h.PrecipProbability >= 20 ? h.PrecipProbability + "%" : "";
                    precip.Foreground = _accent;
                    icon.Data = BuildWeatherGeometry(h.Code, true);
                }
                else
                {
                    label.Text = "--";
                    temp.Text = "--";
                    precip.Text = "";
                    icon.Data = BuildWeatherGeometry(0, true);
                }
            }
            BuildSparkline(hours);

            // Daily: skip today (already in Now), show the next 4.
            for (int i = 0; i < 4; i++)
            {
                bool have = i < snap.Days.Count;
                var label = _dayLabels[i];
                var hi = _dayHi[i];
                var lo = _dayLo[i];
                var precip = _dayPrecip[i];
                var icon = _dayIcons[i];
                if (have)
                {
                    var d = snap.Days[i];
                    label.Text = d.Date.ToString("ddd", CultureInfo.CurrentCulture);
                    hi.Text = Fmt(Temp(d.MaxC)) + "°";
                    lo.Text = Fmt(Temp(d.MinC)) + "°";
                    precip.Text = "";
                    icon.Data = BuildWeatherGeometry(d.Code, true);
                }
                else
                {
                    label.Text = "--";
                    hi.Text = "--";
                    lo.Text = "--";
                    precip.Text = "";
                    icon.Data = BuildWeatherGeometry(0, true);
                }
            }

            SetFreshness();
        }
        catch { }
    }

    private void ClearContent()
    {
        try
        {
            _tempText.Text = "--";
            _condText.Text = L("NoData", "No data");
            _hiloText.Text = "";
            for (int i = 0; i < 3; i++) _metricValues[i].Text = "--";
            for (int i = 0; i < 8; i++)
            {
                _hourLabels[i].Text = "--";
                _hourTemps[i].Text = "--";
                _hourPrecip[i].Text = "";
            }
            for (int i = 0; i < 4; i++)
            {
                _dayLabels[i].Text = "--";
                _dayHi[i].Text = "--";
                _dayLo[i].Text = "--";
                _dayPrecip[i].Text = "";
            }
            _spark.Points.Clear();
            _sparkFill.Data = null;
        }
        catch { }
    }

    private void BuildSparkline(List<HourPoint> hours)
    {
        try
        {
            _spark.Points.Clear();
            _sparkFill.Data = null;
            if (hours.Count < 2) return;

            double min = hours.Min(h => h.TempC);
            double max = hours.Max(h => h.TempC);
            double span = Math.Max(max - min, 1.0);
            double w = DesignW - 24;
            double h = 78 - 8;
            double step = w / (hours.Count - 1);

            for (int i = 0; i < hours.Count; i++)
            {
                double x = 12 + i * step;
                double y = 4 + h - ((hours[i].TempC - min) / span) * h;
                _spark.Points.Add(new Point(x, y));
            }

            // Area under the curve: same points, closed down to the baseline.
            var figure = new PathFigure { StartPoint = new Point(12, 4 + h), IsClosed = true };
            foreach (var p in _spark.Points) figure.Segments.Add(new LineSegment(p, true));
            figure.Segments.Add(new LineSegment(new Point(12 + (hours.Count - 1) * step, 4 + h), true));
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            _sparkFill.Data = geo;
        }
        catch { }
    }

    private void SetFreshness()
    {
        try
        {
            if (!_hasData)
            {
                SetStatus(L("NoData", "No data"), warn: false);
                return;
            }
            var age = DateTimeOffset.UtcNow - _fetchedUtc;
            bool stale = age > RefreshInterval + RefreshInterval;
            var minutes = (int)Math.Max(0, age.TotalMinutes);
            // Pre-format the fallback: WidgetLocalization returns defaultValue VERBATIM when
            // a key is missing, so a "{0}" default would surface as literal braces.
            string text = minutes < 1
                ? L("UpdatedNow", "Updated just now")
                : LPlural("UpdatedMinutes", minutes, "Updated " + minutes + " min ago");
            SetStatus(text + (stale ? "  ·  " + L("Stale", "stale") : ""), stale);
        }
        catch { }
    }

    private void SetStatus(string text, bool warn)
    {
        try
        {
            _statusText.Text = text;
            _statusText.Foreground = warn ? _warn : _fgDim;
            _statusPill.Background = warn ? _chipBg : Brushes.Transparent;
        }
        catch { }
    }

    private void SetError(Exception ex)
    {
        string text = ex.Message == "CityNotFound"
            ? L("CityNotFound", "City not found")
            : ex is HttpRequestException or TaskCanceledException
                ? L("NetworkError", "Network unavailable")
                : L("Error", "Could not load weather");
        SetStatus(text, warn: true);
    }

    #endregion

    #region Theme

    protected override void OnApplyTheme(string? theme)
    {
        _lastHostTheme = theme;
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

        // "Auto (from background)" means "follow my own background colour" — and only makes
        // sense once the user has chosen one. While the colour is unset the background is
        // itself derived from the theme, so deriving the theme from it would be circular: the
        // card would be painted with the previous theme, read back as "still dark", and the
        // widget would never flip. In that case just follow what the host resolved.
        if (ThemeSettingValue == ThemeKeys.Auto && HasUserBackground())
        {
            light = IsBackgroundLight();
        }
        _resolvedThemeIsLight = light;
        try
        {
            _fg = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1C, 0x1E) : Colors.White);
            _fgDim = new SolidColorBrush(light ? Color.FromRgb(0x6B, 0x70, 0x76) : Color.FromRgb(0x9A, 0xA0, 0xA6));
            _accent = new SolidColorBrush(light ? Color.FromRgb(0x15, 0x65, 0xC0) : Color.FromRgb(0x64, 0xB5, 0xF6));
            _chipBg = new SolidColorBrush(light ? Color.FromRgb(0xE4, 0xE7, 0xEB) : Color.FromRgb(0x2C, 0x31, 0x38));
            _chipSelectedBg = new SolidColorBrush(light ? Color.FromRgb(0x15, 0x65, 0xC0) : Color.FromRgb(0x1E, 0x6F, 0xD9));
            _warn = new SolidColorBrush(light ? Color.FromRgb(0xB3, 0x4A, 0x00) : Color.FromRgb(0xFF, 0xB0, 0x4D));
            _sparkFillBrush = new SolidColorBrush(light ? Color.FromArgb(0x33, 0x15, 0x65, 0xC0) : Color.FromArgb(0x40, 0x64, 0xB5, 0xF6));

            _locationText.Foreground = _fg;
            _statusText.Foreground = _fgDim;
            _tempText.Foreground = _fg;
            _condText.Foreground = _fg;
            _hiloText.Foreground = _fgDim;
            _spark.Stroke = _accent;
            _sparkFill.Fill = _sparkFillBrush;

            // Header buttons stay dim so they read as chrome; the hero weather icon is
            // content, so it takes the full foreground colour.
            foreach (var p in _buttonIcons) { p.Fill = _fgDim; p.Stroke = _fgDim; }
            _nowIcon.Fill = _fg;
            foreach (var p in _smallIcons) { p.Fill = _fg; p.Stroke = _fg; }
            foreach (var t in _metricLabels) t.Foreground = _fgDim;
            foreach (var t in _metricValues) t.Foreground = _fg;
            foreach (var t in _hourLabels) t.Foreground = _fgDim;
            foreach (var t in _hourTemps) t.Foreground = _fg;
            foreach (var t in _dayLabels) t.Foreground = _fgDim;
            foreach (var t in _dayHi) t.Foreground = _fg;
            foreach (var t in _dayLo) t.Foreground = _fgDim;

            ApplyView();
            // The unset background default is theme-derived, so a theme change repaints it.
            ApplyBackground();
        }
        catch { }
    }

    protected override void OnLanguageChanged()
    {
        try
        {
            _metricLabels[0].Text = L("Feels like", "Feels like");
            _metricLabels[1].Text = L("Humidity", "Humidity");
            _metricLabels[2].Text = L("Wind", "Wind");
            _tabLabels[0].Text = L("Now", "Now");
            _tabLabels[1].Text = L("Hour", "Next 24 hours");
            _tabLabels[2].Text = L("Day", "Next 4 days");
            if (_snapshot is null)
            {
                _locationText.Text = string.IsNullOrWhiteSpace(_city) ? L("Weather", "Weather") : _city;
                ClearContent();
            }
            Render();
        }
        catch { }
    }

    #endregion

    #region Lifecycle

    protected override void OnSuspend()
    {
        try { _timer.Stop(); } catch { }
        // Cancel in-flight work: a hidden widget has no reason to keep a socket open, and
        // a late reply would touch a window the host may already have torn down.
        CancelInFlight();
    }

    protected override void OnResume()
    {
        // Resume after a long sleep is the common case, so force a pull rather than
        // waiting out the remaining interval.
        _ = RefreshAsync(force: true);
        try { _timer.Start(); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            // Unhook every root. Hide/show destroys and recreates the host window every
            // toggle, so a single leaked handler pins this instance, its visual tree and
            // its collectible AssemblyLoadContext for the life of the process.
            try { _timer.Stop(); } catch { }
            try { _timer.Tick -= OnTimerTick; } catch { }
            try { Loaded -= OnLoaded; } catch { }
            try { Unloaded -= OnUnloaded; } catch { }
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _snapshot = null;
        }
        base.Dispose(disposing);
    }

    #endregion

    #region Geometry helpers

    private static Geometry RefreshGeometry()
    {
        var g = new GeometryGroup();
        g.Children.Add(Geometry.Parse("M 16.6,7.4 A 7,7 0 1 1 12,5"));
        g.Children.Add(Geometry.Parse("M 12,2.2 L 15.8,5 L 12,7.8 Z"));
        return g;
    }

    private static Geometry LocateGeometry()
    {
        var g = new GeometryGroup();
        // Donut, not a disc: the button icons carry both a Fill and a Stroke, so a plain
        // ellipse would render solid. The ticks are zero-area lines and rely on the stroke.
            g.Children.Add(new CombinedGeometry(
                GeometryCombineMode.Exclude,
            new EllipseGeometry(new Rect(5.5, 5.5, 13, 13)),
            new EllipseGeometry(new Rect(8.2, 8.2, 7.6, 7.6))));
        g.Children.Add(Geometry.Parse("M 12,1.5 L 12,5 M 12,19 L 12,22.5 M 1.5,12 L 5,12 M 19,12 L 22.5,12"));
        return g;
    }

    /// <summary>Cloud silhouette shared by every precipitation icon (a GeometryGroup, not a
    /// union: overlapping shapes painted in one fill read as one solid cloud). Occupies
    /// x 18..82, y 28..71 — deliberately narrower than the icon box so a peeking sun or
    /// moon stays legible instead of being buried under it.</summary>
    private static void AddCloud(GeometryGroup g)
    {
        g.Children.Add(new EllipseGeometry(new Rect(28, 28, 44, 36)));   // crown
        g.Children.Add(new EllipseGeometry(new Rect(18, 45, 32, 26)));   // left lobe
        g.Children.Add(new EllipseGeometry(new Rect(50, 45, 32, 26)));   // right lobe
        g.Children.Add(new RectangleGeometry(new Rect(20, 58, 60, 13), 6.5, 6.5));
    }

    /// <summary>Eight rays as thin rotated rectangles rather than a stroked polyline: the
    /// whole icon renders from one GeometryGroup with a single Fill, and a zero-area line
    /// would be invisible under a fill.</summary>
    private static void AddSun(GeometryGroup g, double cx, double cy, double discR)
    {
        g.Children.Add(new EllipseGeometry(new Rect(cx - discR, cy - discR, discR * 2, discR * 2)));
        double inner = discR + 4, outer = discR + 12;
        for (int i = 0; i < 8; i++)
        {
            var ray = new RectangleGeometry(new Rect(cx - 1.5, cy - outer, 3, outer - inner), 1.5, 1.5);
            ray.Transform = new RotateTransform(i * 45, cx, cy);
            g.Children.Add(ray);
        }
    }

    private static void AddMoon(GeometryGroup g, double cx, double cy, double r)
    {
        var disc = new EllipseGeometry(new Rect(cx - r, cy - r, 2 * r, 2 * r));
        var bite = new EllipseGeometry(new Rect(cx - 0.5 * r, cy - 1.34 * r, 1.84 * r, 1.84 * r));
        g.Children.Add(new CombinedGeometry(GeometryCombineMode.Exclude, disc, bite));
    }

    private static void AddRain(GeometryGroup g)
    {
        for (int i = 0; i < 3; i++)
        {
            double x = 31 + i * 15;
            var drop = new RectangleGeometry(new Rect(x, 76, 3, 15), 1.5, 1.5);
            drop.Transform = new RotateTransform(-15, x + 1.5, 76);
            g.Children.Add(drop);
        }
    }

    private static void AddSnow(GeometryGroup g)
    {
        foreach (double x in new[] { 32.0, 47.5, 63.0 })
        {
            g.Children.Add(new EllipseGeometry(new Rect(x, 77, 7, 7)));
            g.Children.Add(new EllipseGeometry(new Rect(x - 2, 86, 11, 11)));
        }
    }

    private static void AddBolt(GeometryGroup g)
    {
        g.Children.Add(Geometry.Parse("M 56,74 L 42,92 L 52,92 L 47,107 L 66,86 L 55,86 Z"));
    }

    private static void AddFog(GeometryGroup g)
    {
        g.Children.Add(new RectangleGeometry(new Rect(24, 76, 52, 4.5), 2.25, 2.25));
        g.Children.Add(new RectangleGeometry(new Rect(31, 86, 38, 4.5), 2.25, 2.25));
        g.Children.Add(new RectangleGeometry(new Rect(26, 96, 48, 4.5), 2.25, 2.25));
    }

    private static int SkyOf(int code) => code switch
    {
        0 => 0,                       // clear
        1 => 1,                       // mainly clear
        2 => 2,                       // partly cloudy
        3 => 3,                       // overcast
        45 or 48 => 4,                // fog
        51 or 53 or 55 => 5,          // drizzle
        56 or 57 => 6,                // freezing drizzle
        61 or 63 or 65 => 7,          // rain
        66 or 67 => 6,                // freezing rain
        71 or 73 or 75 => 8,          // snow
        77 => 8,                      // snow grains
        80 or 81 or 82 => 7,          // rain showers
        85 or 86 => 8,                // snow showers
        95 => 9,                      // thunderstorm
        96 or 99 => 9,                // thunderstorm with hail
        _ => 3,
    };

    private static Geometry BuildWeatherGeometry(int code, bool isDay)
    {
        // Geometry is immutable once built and only depends on (code, isDay), and Render()
        // rebuilds icons on every refresh, so cache rather than re-tessellate.
        long key = ((long)code << 1) | (isDay ? 1L : 0L);
        if (GeometryCache.TryGetValue(key, out var cached)) return cached;

        var g = new GeometryGroup
        {
            // A bare GeometryGroup renders overlapping children as EVEN-ODD, which punches
            // the cloud's crown-vs-lobe overlaps into almond-shaped holes. Nonzero makes
            // the union solid; verified by rendering both (tools: wxharness).
            FillRule = FillRule.Nonzero,
        };
        switch (SkyOf(code))
        {
            // Clear: the luminary owns the whole box.
            case 0:
                if (isDay) AddSun(g, 50, 48, 20); else AddMoon(g, 50, 48, 20);
                break;
            // Mainly clear / partly cloudy: the luminary shrinks to the top-left and the
            // cloud is painted OVER it, so only a corner peeks out - the standard reading.
            case 1:
            case 2:
                if (isDay) AddSun(g, 32, 32, 13); else AddMoon(g, 32, 32, 13);
                AddCloud(g);
                break;
            case 3:
                AddCloud(g);
                break;
            case 4:
                AddCloud(g);
                AddFog(g);
                break;
            case 5:
            case 6:
            case 7:
                AddCloud(g);
                AddRain(g);
                break;
            case 8:
                AddCloud(g);
                AddSnow(g);
                break;
            case 9:
                AddCloud(g);
                AddBolt(g);
                break;
        }
        g.Freeze();
        GeometryCache[key] = g;
        return g;
    }

    /// <summary>Localization key and English source string for one WMO code, in a single
    /// table so the two cannot drift. Carrying the English text matters: WidgetLocalization
    /// returns the supplied default VERBATIM when a key is missing, so a fallback that is
    /// not the real phrase would label rain as "Clear sky".</summary>
    private static (string Key, string Text) ConditionOf(int code) => code switch
    {
        0 => ("Clear", "Clear sky"),
        1 => ("MainlyClear", "Mainly clear"),
        2 => ("PartlyCloudy", "Partly cloudy"),
        3 => ("Overcast", "Overcast"),
        45 or 48 => ("Fog", "Fog"),
        51 => ("LightDrizzle", "Light drizzle"),
        53 => ("Drizzle", "Drizzle"),
        55 => ("HeavyDrizzle", "Heavy drizzle"),
        56 => ("LightFreezingDrizzle", "Light freezing drizzle"),
        57 => ("FreezingDrizzle", "Freezing drizzle"),
        61 => ("LightRain", "Light rain"),
        63 => ("Rain", "Rain"),
        65 => ("HeavyRain", "Heavy rain"),
        66 => ("LightFreezingRain", "Light freezing rain"),
        67 => ("FreezingRain", "Freezing rain"),
        71 => ("LightSnow", "Light snow"),
        73 => ("Snow", "Snow"),
        75 => ("HeavySnow", "Heavy snow"),
        77 => ("SnowGrains", "Snow grains"),
        80 => ("LightShowers", "Light showers"),
        81 => ("Showers", "Rain showers"),
        82 => ("ViolentShowers", "Violent showers"),
        85 => ("LightSnowShowers", "Light snow showers"),
        86 => ("SnowShowers", "Snow showers"),
        95 => ("Thunderstorm", "Thunderstorm"),
        96 or 99 => ("ThunderstormHail", "Thunderstorm with hail"),
        _ => ("Unknown", "Unknown"),
    };

    #endregion
}
