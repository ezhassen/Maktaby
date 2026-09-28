using Maktaby.WidgetSdk;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NativeClockWidget;

/// <summary>Sample native widget (source mode): a modern analog clock mirroring the
/// built-in CSS AnalogClock — offset radial-glow face, ring border, soft shadow,
/// chunky rounded hands with a red second hand, center cap and subtle minute ticks.
/// Demonstrates Suspend/Resume (timer control), ApplyTheme (dark/light faces) and the
/// parameterless-constructor + INativeWidget discovery contract. Interaction (hover,
/// click, focus) needs no code here — the host detects WPF routed events itself.</summary>
public sealed class ClockWidget : NativeWidgetControl
{
    private const double Size = 300;
    private const double Center = Size / 2;

    private readonly RadialGradientBrush _faceBrush;
    private readonly GradientStop _glowStop;
    private readonly GradientStop _edgeStop;
    private readonly Ellipse _face;
    private readonly DropShadowEffect _shadow;
    private readonly List<Line> _ticks = new();
    private readonly List<Line> _cardinals = new();
    private readonly Line _hourHand;
    private readonly Line _minuteHand;
    private readonly Line _secondHand;
    private readonly List<TextBlock> _hourNumerals = new();
    private readonly TextBlock _ampmText;
    private readonly TextBlock _dateText;
    private readonly Ellipse _cap;
    private readonly DispatcherTimer _timer;
    private WidgetSetting? _secondsHandMode;
    private bool _showSeconds = true;
    private string _showTicksMode = "all";
    private bool _smoothSeconds = true;
    private string _showHoursMode = "hide";
    private bool _showAmPm;
    private bool _showDate;

    public ClockWidget()
    {
        _glowStop = new GradientStop(Colors.Transparent, 0.0);
        _edgeStop = new GradientStop(Colors.Transparent, 1.0);
        _faceBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.32, 0.3),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops = { _glowStop, _edgeStop },
        };
        _shadow = new DropShadowEffect
        {
            Color = Colors.Black,
            Direction = 270,
            ShadowDepth = 4,
            BlurRadius = 16,
            Opacity = 0.5,
        };
        _face = new Ellipse { Fill = _faceBrush, StrokeThickness = 6, Effect = _shadow };

        // Static layer (face, ticks, numerals, labels): cached as one bitmap so the 10 Hz hand
        // updates composite over it instead of re-tessellating + re-blurring (DropShadow) the
        // whole face every frame. Hands stay live above it.
        var staticLayer = new Grid { Width = Size, Height = Size };
        staticLayer.Children.Add(_face);
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6;
            bool cardinal = i % 3 == 0;
            double r1 = Center - (cardinal ? 26 : 16);
            double r2 = Center - 8;
            var tick = new Line
            {
                X1 = Center + Math.Sin(a) * r1,
                Y1 = Center - Math.Cos(a) * r1,
                X2 = Center + Math.Sin(a) * r2,
                Y2 = Center - Math.Cos(a) * r2,
                StrokeThickness = cardinal ? 4 : 2,
            };
            (cardinal ? _cardinals : _ticks).Add(tick);
            staticLayer.Children.Add(tick);
        }
        // Cache after children are in (before first render): one bitmap composite per frame.
        staticLayer.CacheMode = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = false };
        var grid = new Grid { Width = Size, Height = Size };
        grid.Children.Add(staticLayer);
        _hourHand = Hand(9, 84);
        _minuteHand = Hand(7, 117);
        _secondHand = Hand(3, 129);
        grid.Children.Add(_hourHand);
        grid.Children.Add(_minuteHand);
        grid.Children.Add(_secondHand);
        // Hour numerals (1-12) on a 102px ring + AM/PM and short date below center, all
        // centered via translate-from-center so text size never offsets them.
        for (int h = 1; h <= 12; h++)
        {
            double na = h * Math.PI / 6;
            var numeral = new TextBlock
            {
                Text = h.ToString(),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = new TranslateTransform(Math.Sin(na) * 114, -Math.Cos(na) * 114),
            };
            _hourNumerals.Add(numeral);
            staticLayer.Children.Add(numeral);
        }
        _ampmText = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(0, 52),
        };
        staticLayer.Children.Add(_ampmText);
        _dateText = new TextBlock
        {
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new TranslateTransform(0, 72),
        };
        staticLayer.Children.Add(_dateText);
        _cap = new Ellipse
        {
            Width = 16.5,
            Height = 16.5,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(_cap);

        Content = new Viewbox { Stretch = Stretch.Uniform, Child = grid };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTimerTick;
        // Start on load, not in the ctor: a constructed-but-never-shown instance (gallery
        // preview host, background tab) must not tick. Suspend-aware so a suspended reload
        // doesn't restart behind the host's back.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Demo user settings (see docs/native-widgets.md): the host renders an editor for each.
        // Cached in fields — UpdateHands runs at 10 Hz and must not re-resolve per frame.
        var secondsMode = DefineSetting("secondsHandMode", "Second hand", "How the second hand behaves",
            WidgetSettingKind.ListOfStrings, OnSecondsHandModeChanged, defaultValue: "smooth",
            listOfAvailableStrings: new Dictionary<string, string>
            {
                ["hide"] = "Hide",
                ["smooth"] = "Smooth sweep",
                ["step"] = "Step once per second",
            });
        _secondsHandMode = secondsMode;
        ApplySecondsHandMode(applyTimerRate: false);
        //
        var showTicks = DefineSetting("showTicks", "Tick marks", "Which tick marks to show",
            WidgetSettingKind.ListOfStrings, OnShowTicksChanged, defaultValue: "all",
            listOfAvailableStrings: new Dictionary<string, string>
            {
                ["hide"] = "Hide",
                ["all"] = "Show all",
                ["quarters"] = "Quarters only",
            });
        _showTicksMode = showTicks.GetString();
        //
        DefineSetting("showHours", "Hour numbers", "Which hour numbers to show",
            WidgetSettingKind.ListOfStrings, OnShowHoursChanged, defaultValue: "hide",
            listOfAvailableStrings: new Dictionary<string, string>
            {
                ["hide"] = "Hide",
                ["all"] = "Show all",
                ["quarters"] = "Quarters only",
            });
        _showHoursMode = FindSetting("showHours")?.GetString() ?? "hide";
        //
        DefineSetting("showAmPm", "AM/PM", "Show AM/PM below the center",
            WidgetSettingKind.Boolean, () => { _showAmPm = FindSetting("showAmPm")?.GetBoolean() == true; ApplyExtrasVisibility(); UpdateHands(); },
            defaultValue: false);
        _showAmPm = FindSetting("showAmPm")?.GetBoolean() == true;
        //
        DefineSetting("showDate", "Short date", "Show the short date below the center",
            WidgetSettingKind.Boolean, () => { _showDate = FindSetting("showDate")?.GetBoolean() == true; ApplyExtrasVisibility(); UpdateHands(); },
            defaultValue: false);
        _showDate = FindSetting("showDate")?.GetBoolean() == true;
        //
        ApplyTimerRate();
        ApplyTickVisibility();
        ApplyExtrasVisibility();

        OnApplyTheme(null);
        //UpdateHands();//applied onLoaded
    }

    private void OnShowTicksChanged()
    {
        _showTicksMode = FindSetting("showTicks")?.GetString() ?? "all";
        ApplyTickVisibility();
    }

    private void OnSecondsHandModeChanged()
    {
        ApplySecondsHandMode();
        UpdateHands();
    }

    /// <summary>Folds the combined second-hand mode into the cached flags.</summary>
    private void ApplySecondsHandMode(bool applyTimerRate = true)
    {
        string mode = _secondsHandMode?.GetString() ?? "smooth";
        _showSeconds = mode != "hide";
        _smoothSeconds = mode == "smooth";
        if (applyTimerRate) ApplyTimerRate();
    }

    private void ApplyTimerRate()
    {
        // No second hand, no sub-minute work: tick once a minute.
        try { _timer.Interval = TimeSpan.FromMilliseconds(!_showSeconds ? 60000 : (_smoothSeconds ? 100 : 1000)); } catch { }
    }

    private void ApplyTickVisibility()
    {
        try
        {
            var minorsVis = _showTicksMode == "all" ? Visibility.Visible : Visibility.Collapsed;
            var cardinalsVis = _showTicksMode != "hide" ? Visibility.Visible : Visibility.Collapsed;
            foreach (var t in _ticks) t.Visibility = minorsVis;
            foreach (var t in _cardinals) t.Visibility = cardinalsVis;
        }
        catch { }
    }

    private void OnShowHoursChanged()
    {
        _showHoursMode = FindSetting("showHours")?.GetString() ?? "hide";
        ApplyExtrasVisibility();
    }

    private void ApplyExtrasVisibility()
    {
        try
        {
            for (int i = 0; i < _hourNumerals.Count; i++)
            {
                int h = i + 1;
                _hourNumerals[i].Visibility = _showHoursMode == "all"
                    || (_showHoursMode == "quarters" && h % 3 == 0)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            _ampmText.Visibility = _showAmPm ? Visibility.Visible : Visibility.Collapsed;
            _dateText.Visibility = _showDate ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private static Line Hand(double thickness, double length) => new()
    {
        X1 = Center,
        Y1 = Center,
        X2 = Center,
        Y2 = Center - length,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    protected override void OnSuspend()
    {
        try { _timer.Stop(); } catch { }
    }

    protected override void OnResume()
    {
        UpdateHands();
        try { _timer.Start(); } catch { }
    }

    private void OnTimerTick(object? sender, EventArgs e) => UpdateHands();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsSuspended) return;
        UpdateHands();
        try { _timer.Start(); } catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { _timer.Stop(); } catch { }
    }

    protected override void OnApplyTheme(string? theme)
    {
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        _glowStop.Color = light ? Colors.White : Color.FromRgb(0x2A, 0x2A, 0x2A);
        _edgeStop.Color = light ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x11, 0x11, 0x11);
        _face.Stroke = light
            ? new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0))
            : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        _shadow.Opacity = light ? 0.15 : 0.5;
        foreach (var t in _ticks) t.Stroke = light ? Brushes.LightGray : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        foreach (var t in _cardinals) t.Stroke = light ? Brushes.DimGray : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var numeralBrush = light ? Brushes.DimGray : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        foreach (var t in _hourNumerals) t.Foreground = numeralBrush;
        _ampmText.Foreground = numeralBrush;
        _dateText.Foreground = numeralBrush;
        _hourHand.Stroke = light ? new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)) : Brushes.WhiteSmoke;
        _minuteHand.Stroke = light ? new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)) : Brushes.LightGray;
        _secondHand.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52));
        _cap.Fill = light
            ? new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D))
            : Brushes.WhiteSmoke;
        _cap.Stroke = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _timer.Stop(); } catch { }
            // Unhook everything that roots this instance. Hide/show destroys + recreates the
            // host window every toggle, so a leaked root here keeps the whole visual tree
            // (BitmapCache, DropShadow surface) and its collectible ALC alive per cycle.
            try { _timer.Tick -= OnTimerTick; } catch { }
            try { Loaded -= OnLoaded; } catch { }
            try { Unloaded -= OnUnloaded; } catch { }
            // Drop the effect surface reference with the tree; composition released it on detach.
            try { _face.Effect = null; } catch { }
        }
        base.Dispose(disposing);
    }

    private double _lastHour = double.NaN;
    private double _lastMinute = double.NaN;
    private double _lastSecond = double.NaN;

    private void UpdateHands()
    {
        try
        {
            _secondHand.Visibility = _showSeconds ? Visibility.Visible : Visibility.Collapsed;
            var now = DateTime.Now;
            SetHandOnce(_hourHand, ((now.Hour % 12) + now.Minute / 60.0) / 12.0, 84, ref _lastHour);
            SetHandOnce(_minuteHand, (now.Minute + now.Second / 60.0) / 60.0, 117, ref _lastMinute);
            double seconds = _smoothSeconds ? now.Second + now.Millisecond / 1000.0 : now.Second;
            if (_showSeconds)
            {
                SetHandOnce(_secondHand, seconds / 60.0, 129, ref _lastSecond);
            }
            if (_showAmPm)
            {
                var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
                string ap = now.Hour < 12 ? dtf.AMDesignator : dtf.PMDesignator;
                if (string.IsNullOrWhiteSpace(ap)) ap = now.Hour < 12 ? "AM" : "PM";
                if (_ampmText.Text != ap) _ampmText.Text = ap;
            }
            if (_showDate)
            {
                string d = now.ToString("d", CultureInfo.CurrentCulture);
                if (_dateText.Text != d) _dateText.Text = d;
            }
        }
        catch { }
    }

    /// <summary>Moves a hand only when its fraction actually changed — untouched visuals
    /// don't invalidate, so an idle minute costs zero renders instead of 600.</summary>
    private static void SetHandOnce(Line hand, double fraction, double length, ref double last)
    {
        if (fraction == last) return;
        last = fraction;
        SetHand(hand, fraction, length);
    }

    private static void SetHand(Line hand, double fraction, double length)
    {
        double a = fraction * Math.PI * 2;
        hand.X2 = Center + Math.Sin(a) * length;
        hand.Y2 = Center - Math.Cos(a) * length;
    }
}
