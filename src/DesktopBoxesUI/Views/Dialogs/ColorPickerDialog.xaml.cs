using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Views.Dialogs
{
    public partial class ColorPickerDialog : FluentWindow
    {
        private bool _isUpdating;
        private static readonly Color[] StandardColors = new[]
        {
            Colors.Transparent, Colors.Black, Colors.White, Colors.Gray,
            Colors.Red, Colors.Green, Colors.Blue, Colors.Yellow,
            Colors.Orange, Colors.Purple, Colors.Cyan, Colors.Magenta
        };

        public Color SelectedColor { get; private set; }

        public ColorPickerDialog(Color initial)
        {
            InitializeComponent();
            SelectedColor = initial;
            Loaded += OnLoaded;
        }

        public ColorPickerDialog(Window owner, Color initial) : this(initial)
        {
            Owner = owner;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Init sliders without triggering updates
            _isUpdating = true;
            RSlider.Value = SelectedColor.R;
            GSlider.Value = SelectedColor.G;
            BSlider.Value = SelectedColor.B;
            ASlider.Value = SelectedColor.A;
            HexBox.Text = ColorToHex(SelectedColor);
            PreviewBrush.Color = SelectedColor;
            _isUpdating = false;

            BuildPalette();
            UpdatePreview();
        }

        private void BuildPalette()
        {
            StandardPanel.Children.Clear();
            RecentPanel.Children.Clear();
            foreach (var c in StandardColors)
            {
                var btn = new Border
                {
                    Width = 24,
                    Height = 24,
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(c),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = ColorToHex(c)
                };
                var col = c;
                btn.MouseLeftButtonDown += (_, _) => SetColor(col);
                StandardPanel.Children.Add(btn);
            }
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating) return;
            var c = Color.FromArgb((byte)ASlider.Value, (byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value);
            SetColor(c, updateSliders: false);
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating) return;
            var hex = HexBox.Text?.Trim();
            if (TryParseHex(hex, out var c))
            {
                ValidationText.Visibility = Visibility.Collapsed;
                SetColor(c, updateHex: false);
            }
            else
            {
                ValidationText.Visibility = string.IsNullOrWhiteSpace(hex) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void SetColor(Color c, bool updateSliders = true, bool updateHex = true)
        {
            SelectedColor = c;
            PreviewBrush.Color = c;
            if (updateSliders)
            {
                _isUpdating = true;
                RSlider.Value = c.R;
                GSlider.Value = c.G;
                BSlider.Value = c.B;
                ASlider.Value = c.A;
                _isUpdating = false;
            }
            if (updateHex)
            {
                _isUpdating = true;
                HexBox.Text = ColorToHex(c);
                ValidationText.Visibility = Visibility.Collapsed;
                _isUpdating = false;
            }
        }

        private static string ColorToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static bool TryParseHex(string? hex, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex.Trim());
                color = c;
                return true;
            }
            catch { return false; }
        }

        private void UpdatePreview()
        {
            PreviewBrush.Color = SelectedColor;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
