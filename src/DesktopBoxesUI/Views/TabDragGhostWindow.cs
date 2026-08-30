using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Ghost for Chrome-like tab drag: tab visual follows cursor constrained to tab-strip height.
/// </summary>
internal sealed class TabDragGhostWindow : Window
{
    public TabDragGhostWindow(string title, bool isFolder, double width = 140, double height = 32)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = width > 0 ? width : 140;
        Height = height > 0 ? height : 32;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        ShowActivated = false;

        var border = new Border
        {
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = (Brush)Application.Current.Resources["BoxBackground"],
            BorderBrush = (Brush)Application.Current.Resources["BoxBorder"],
            BorderThickness = new Thickness(1, 1, 1, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new SymbolIcon
                    {
                        Symbol = SymbolRegular.Folder24,
                        Width = 14,
                        Height = 14,
                        Margin = new Thickness(0,0,6,0),
                        Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed
                    },
                    new System.Windows.Controls.TextBlock
                    {
                        Text = title,
                        Foreground = (Brush)Application.Current.Resources["BoxForeground"],
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 100
                    }
                }
            }
        };

        // subtle shadow
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = 0.35,
            BlurRadius = 8,
            ShadowDepth = 2,
            Direction = 270
        };

        Content = border;
    }
}
