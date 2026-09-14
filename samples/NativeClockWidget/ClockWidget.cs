using DesktopBoxes.WidgetSdk;
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
    private readonly Ellipse _cap;
    private readonly DispatcherTimer _timer;

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

        var grid = new Grid { Width = Size, Height = Size };
        grid.Children.Add(_face);
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
            grid.Children.Add(tick);
        }
        _hourHand = Hand(9, 84);
        _minuteHand = Hand(7, 117);
        _secondHand = Hand(3, 129);
        grid.Children.Add(_hourHand);
        grid.Children.Add(_minuteHand);
        grid.Children.Add(_secondHand);
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
        _timer.Tick += (_, _) => UpdateHands();
        _timer.Start();

        OnApplyTheme(null);
        UpdateHands();
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
        }
        base.Dispose(disposing);
    }

    private void UpdateHands()
    {
        try
        {
            var now = DateTime.Now;
            SetHand(_hourHand, ((now.Hour % 12) + now.Minute / 60.0) / 12.0, 84);
            SetHand(_minuteHand, (now.Minute + now.Second / 60.0) / 60.0, 117);
            SetHand(_secondHand, (now.Second + now.Millisecond / 1000.0) / 60.0, 129);
        }
        catch { }
    }

    private static void SetHand(Line hand, double fraction, double length)
    {
        double a = fraction * Math.PI * 2;
        hand.X2 = Center + Math.Sin(a) * length;
        hand.Y2 = Center - Math.Cos(a) * length;
    }
}
