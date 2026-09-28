using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Maktaby.Views.HelpersViews;

/// <summary>
/// A borderless, transparent, topmost window that follows the cursor during a drag operation, showing
/// a ghost of the dragged item's icon and name. Implemented as a top-level window (rather than a Popup)
/// so it stays above all of the app's box windows while dragging across containers.
/// </summary>
internal sealed class DragGhostWindow : Window
{
    private readonly Image _image = new()
    {
        Width = 36,
        Height = 36,
        Stretch = Stretch.Uniform,
    };

    private readonly TextBlock _label = new()
    {
        Foreground = Brushes.White,
        FontSize = 11,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 96,
        Margin = new Thickness(0, 2, 0, 0),
    };

    public DragGhostWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 62;
        Height = 62;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(_image);
        stack.Children.Add(_label);
        Content = stack;
    }

    public void SetItem(ImageSource? icon, string name)
    {
        _image.Source = icon;
        _label.Text = name;
    }

    public void SetItems(ImageSource? icon, int count)
    {
        _image.Source = icon;
        _label.Text = count == 1 ? "1 item" : $"{count} items";
    }
}
