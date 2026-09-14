using DesktopBoxes.WidgetSdk;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NativeCalendarWidget;

/// <summary>Built-in month calendar. Shows the current day, navigates by month and year,
/// and follows <see cref="CultureInfo.CurrentCulture"/> for first day of week, day/month
/// names and calendar system. Clicking a trailing day jumps to its month; clicking the
/// title returns to today.</summary>
public sealed class CalendarWidget : NativeWidgetControl
{
    private const double DesignWidth = 340;
    private const double DesignHeight = 300;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;
    private static DateTimeFormatInfo Format => Culture.DateTimeFormat;
    private static System.Globalization.Calendar Calendar => Culture.Calendar;

    private readonly Grid _dayGrid = new();
    private readonly TextBlock _title = new();
    private readonly Border _panel = new();
    private readonly DispatcherTimer _midnightCheck;
    private Storyboard? _navStoryboard;

    // Gregorian cursor; all displayed values convert through the culture calendar.
    private DateTime _cursor = DateTime.Today;
    private DateTime _today = DateTime.Today;
    private DateTime? _selected;

    private Brush _text = Brushes.WhiteSmoke;
    private Brush _dim = Brushes.Gray;
    private Brush _header = Brushes.White;
    private Brush _weekday = Brushes.Gray;
    private Brush _selectedFill = Brushes.DarkOrange;
    private Brush _selectedText = Brushes.White;
    private Brush _todayRing = Brushes.Orange;
    private Brush _panelBg = Brushes.Transparent;
    private Brush _panelBorder = Brushes.Transparent;
    private Brush _hoverFill = Brushes.Transparent;
    private Brush _hoverCell = Brushes.Transparent;

    public CalendarWidget()
    {
        var root = new Grid { Width = DesignWidth, Height = DesignHeight };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(BuildHeader());
        root.Children.Add(BuildWeekdays());
        Grid.SetRow(_dayGrid, 2);
        for (int i = 0; i < 7; i++)
            _dayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 6; i++)
            _dayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // Clip host so sliding days never paint over the header/weekday rows mid-animation.
        var dayClip = new Border { ClipToBounds = true, Child = _dayGrid };
        Grid.SetRow(dayClip, 2);
        root.Children.Add(dayClip);

        // Semi-transparent rounded panel so the calendar reads over any wallpaper.
        _panel.CornerRadius = new CornerRadius(14);
        _panel.Padding = new Thickness(10, 8, 10, 10);
        _panel.BorderThickness = new Thickness(1);
        _panel.Background = _panelBg;
        _panel.BorderBrush = _panelBorder;
        _panel.Child = root;
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _panel };

        // Keyboard selection: arrows move the selected day (focus lands here on click).
        Focusable = true;
        PreviewKeyDown += OnPreviewKeyDown;

        // Wheel up = earlier, wheel down = later; Shift steps whole years.
        PreviewMouseWheel += OnMouseWheel;

        _midnightCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _midnightCheck.Tick += (_, _) => CheckDayRollover();
        _midnightCheck.Start();

        OnApplyTheme(null);
        Rebuild();
    }

    /// <summary>Culture year/month currently displayed (uses the culture calendar).</summary>
    public int DisplayYear => Calendar.GetYear(_cursor);
    public int DisplayMonth => Calendar.GetMonth(_cursor);
    public DateTime? SelectedDate => _selected;

    public void NextMonth() => MoveMonths(1);
    public void PrevMonth() => MoveMonths(-1);
    public void NextYear() => MoveMonths(12);
    public void PrevYear() => MoveMonths(-12);
    public void GoToToday()
    {
        _today = DateTime.Today;
        _selected = DateTime.Today;
        SetCursor(DateTime.Today, animate: true);
    }

    /// <summary>Selects a date, sliding into its month when it lies outside the display.</summary>
    public void SelectDate(DateTime date)
    {
        date = date.Date;
        _selected = date;
        try
        {
            if (Calendar.GetYear(date) != Calendar.GetYear(_cursor) ||
                Calendar.GetMonth(date) != Calendar.GetMonth(_cursor))
                SetCursor(date, animate: true);
            else
                Rebuild();
        }
        catch
        {
            _cursor = date;
            Rebuild();
        }
    }

    /// <summary>Moves the selection by whole days (arrows use ±1/±7).</summary>
    public void MoveSelection(int days) => SelectDate((_selected ?? DateTime.Today).AddDays(days));

    private void MoveMonths(int months) => SetCursor(_cursor.AddMonths(months), animate: true);

    private void SetCursor(DateTime value, bool animate)
    {
        int before = MonthOrdinal(_cursor);
        _cursor = value;
        Rebuild();
        if (animate) PlayNavigateAnimation(Math.Sign(MonthOrdinal(_cursor) - before));
    }

    private static int MonthOrdinal(DateTime date)
    {
        try { return Calendar.GetYear(date) * 12 + Calendar.GetMonth(date); }
        catch { return date.Year * 12 + date.Month; }
    }

    /// <summary>Short slide+fade of the month days panel: forward slides up,
    /// back slides down. Header and weekday row stay put.</summary>
    private void PlayNavigateAnimation(int direction)
    {
        if (IsSuspended) return;
        try
        {
            try { _navStoryboard?.Stop(); } catch { }
            var slide = new TranslateTransform();
            _dayGrid.RenderTransform = slide;
            double from = direction > 0 ? 30 : direction < 0 ? -30 : 0;
            var move = new DoubleAnimation(from, 0, new Duration(TimeSpan.FromMilliseconds(210)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            var fade = new DoubleAnimation(0.45, 1.0, new Duration(TimeSpan.FromMilliseconds(210)));
            var sb = new Storyboard();
            Storyboard.SetTarget(move, slide);
            Storyboard.SetTargetProperty(move, new PropertyPath(TranslateTransform.YProperty));
            Storyboard.SetTarget(fade, _dayGrid);
            Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(move);
            sb.Children.Add(fade);
            _navStoryboard = sb;
            sb.Begin(_dayGrid, true);
        }
        catch { }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: MoveSelection(-1); break;
            case Key.Right: MoveSelection(1); break;
            case Key.Up: MoveSelection(-7); break;
            case Key.Down: MoveSelection(7); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool years = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int step = e.Delta > 0 ? -1 : 1;
        MoveMonths(years ? step * 12 : step);
        e.Handled = true;
    }

    private Grid BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        header.Children.Add(NavButton("«", 0, () => MoveMonths(-12), "Previous year"));
        header.Children.Add(NavButton("‹", 1, () => MoveMonths(-1), "Previous month"));

        _title.FontSize = 16;
        _title.FontWeight = FontWeights.SemiBold;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Cursor = Cursors.Hand;
        _title.ToolTip = "Go to today";
        _title.PreviewMouseLeftButtonUp += (_, _) => GoToToday();
        Grid.SetColumn(_title, 2);
        header.Children.Add(_title);

        header.Children.Add(NavButton("›", 3, () => MoveMonths(1), "Next month"));
        header.Children.Add(NavButton("»", 4, () => MoveMonths(12), "Next year"));
        Grid.SetRow(header, 0);
        return header;
    }

    private Border NavButton(string text, int column, Action action, string tooltip)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 18,
            Foreground = _header,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _navLabels.Add(label);
        var border = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            ToolTip = tooltip,
            Cursor = Cursors.Hand,
        };
        border.MouseEnter += (_, _) => border.Background = _hoverFill;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.PreviewMouseLeftButtonUp += (_, _) => action();
        Grid.SetColumn(border, column);
        return border;
    }

    private Grid BuildWeekdays()
    {
        var row = new Grid { Margin = new Thickness(4, 0, 4, 2) };
        for (int i = 0; i < 7; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DayOfWeek first = Format.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            DayOfWeek day = (DayOfWeek)(((int)first + i) % 7);
            // Shortest day name: the single distinctive letter ("S" / "س" / …).
            // Truncating AbbreviatedDayNames breaks languages sharing prefixes
            // (Arabic: السبت/الأحد/… all start with "ال").
            string name;
            try { name = Format.GetShortestDayName(day); } catch { name = ""; }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Format.AbbreviatedDayNames[(int)day];
                if (name.Length > 2) name = name.Substring(0, 2);
            }
            var label = new TextBlock
            {
                Text = name.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, i);
            row.Children.Add(label);
            _weekdayLabels.Add(label);
        }
        Grid.SetRow(row, 1);
        return row;
    }

    private readonly List<TextBlock> _weekdayLabels = new();
    private readonly List<TextBlock> _navLabels = new();

    private void Rebuild()
    {
        int era;
        try { era = Calendar.GetEra(_cursor); }
        catch { era = Calendar.Eras.Length > 0 ? Calendar.Eras[0] : 1; }
        int year = Calendar.GetYear(_cursor);
        int month = Calendar.GetMonth(_cursor);
        string monthName;
        try { monthName = Format.MonthNames[month - 1]; }
        catch { monthName = month.ToString(Culture); }
        _title.Text = $"{monthName} {year}";

        foreach (var label in _weekdayLabels) label.Foreground = _weekday;

        DateTime firstOfMonth;
        try { firstOfMonth = Calendar.ToDateTime(year, month, 1, 0, 0, 0, 0, era); }
        catch { firstOfMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); }
        int offset = ((int)Calendar.GetDayOfWeek(firstOfMonth) - (int)Format.FirstDayOfWeek + 7) % 7;
        DateTime cell = firstOfMonth.AddDays(-offset);

        _dayGrid.Children.Clear();
        for (int i = 0; i < 42; i++, cell = cell.AddDays(1))
        {
            bool inMonth;
            try { inMonth = Calendar.GetMonth(cell) == month && Calendar.GetYear(cell) == year; }
            catch { inMonth = cell.Month == DateTime.Today.Month; }
            int dayNumber;
            try { dayNumber = Calendar.GetDayOfMonth(cell); }
            catch { dayNumber = cell.Day; }
            bool isToday = cell.Date == _today.Date;
            bool isSelected = _selected.HasValue && cell.Date == _selected.Value.Date;
            _dayGrid.Children.Add(BuildDayCell(cell, dayNumber, inMonth, isToday, isSelected, i));
        }
    }

    private Border BuildDayCell(DateTime date, int dayNumber, bool inMonth, bool isToday, bool isSelected, int index)
    {
        var label = new TextBlock
        {
            Text = dayNumber.ToString(Culture),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = !inMonth ? _dim : isSelected ? _selectedText : _text,
            Opacity = inMonth ? 1.0 : 0.6,
        };
        var cell = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(1.5),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(isToday ? 1.5 : 0),
            BorderBrush = _todayRing,
        };
        if (isSelected)
        {
            cell.Background = _selectedFill;
        }
        else
        {
            cell.MouseEnter += (_, _) => cell.Background = _hoverCell;
            cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        }
        DateTime captured = date;
        cell.PreviewMouseLeftButtonUp += (_, _) =>
        {
            SelectDate(captured);
            try { Focus(); } catch { } // keep keyboard arrows working after mouse use
        };
        Grid.SetRow(cell, index / 7);
        Grid.SetColumn(cell, index % 7);
        return cell;
    }

    private void CheckDayRollover()
    {
        if (DateTime.Today == _today) return;
        _today = DateTime.Today;
        // Refresh only if the new today could be visible.
        try
        {
            if (Calendar.GetYear(_cursor) == Calendar.GetYear(_today) &&
                Calendar.GetMonth(_cursor) == Calendar.GetMonth(_today))
                Rebuild();
        }
        catch { Rebuild(); }
    }

    protected override void OnSuspend()
    {
        try { _midnightCheck.Stop(); } catch { }
    }

    protected override void OnResume()
    {
        CheckDayRollover();
        try { _midnightCheck.Start(); } catch { }
    }

    protected override void OnApplyTheme(string? theme)
    {
        bool light = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        _panelBg = light
            ? new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(185, 0x0C, 0x0C, 0x16));
        _panelBorder = light
            ? new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8))
            : new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x44));
        _panel.Background = _panelBg;
        _panel.BorderBrush = _panelBorder;
        _text = light ? new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)) : Brushes.WhiteSmoke;
        _dim = light ? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) : new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x9C));
        _header = light ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)) : Brushes.White;
        _weekday = light ? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)) : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xB0));
        _selectedFill = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x42));
        _selectedText = Brushes.White;
        _todayRing = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x42));
        _hoverFill = light ? new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)) : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3A));
        _hoverCell = light ? new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)) : new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x36));
        _title.Foreground = _header;
        foreach (var label in _navLabels) label.Foreground = _header;
        Rebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _midnightCheck.Stop(); } catch { }
        }
        base.Dispose(disposing);
    }
}
