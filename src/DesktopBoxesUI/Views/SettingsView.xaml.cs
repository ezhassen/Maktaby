using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Modal-less settings editor opened from the tray menu. Edits are applied/saved only when
/// "Save" is clicked; "Cancel" discards. Hosted in a <see cref="FluentWindow"/> so it follows
/// the WPF-UI application theme. The left vertical list navigates a single scrollable column of
/// sections (Appearance / Boxes / General / Snapshot).
/// </summary>
public partial class SettingsView : FluentWindow
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        ApplyDesktopBackground();
        BuildPreviewItems();

        // Keep the preview in sync with every edit (the VM raises PropertyChanged per field).
        vm.PropertyChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private static readonly string[] _sectionNames =
        { "SectionPreview", "SectionAppearance", "SectionBoxes", "SectionGeneral", "SectionSnapshot" };

    // Clicking a tab scrolls its section to the top of the viewport and plays a brief orange focus border.
    private void CategoryList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null || item.Tag is not string name)
        {
            return;
        }

        if (FindName(name) is Control section)
        {
            ScrollSectionToTop(section);
            FlashSection(section);
        }
    }

    // Brings a section's top to the very top of the scroll viewport (BringIntoView only ensures
    // visibility, so it does nothing when the section is already partly on screen).
    private void ScrollSectionToTop(FrameworkElement section)
    {
        if (ContentScroll.Content is not FrameworkElement content)
        {
            section.BringIntoView();
            return;
        }

        var topInContent = section.TransformToVisual(content).Transform(new Point(0, 0)).Y;
        ContentScroll.ScrollToVerticalOffset(topInContent);//+ ContentScroll.Padding.Top (no padding)
    }

    // Scroll-spy: keep the active tab in sync with whatever section is at the top of the viewport.
    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // When scrolled to the very bottom the last section can never reach the top band, so force-select it.
        if (ContentScroll.ScrollableHeight > 1 &&
            ContentScroll.VerticalOffset >= ContentScroll.ScrollableHeight - 1)
        {
            SetActiveTag(_sectionNames[^1]);
            return;
        }

        string? active = null;
        double bestTop = double.NegativeInfinity;

        foreach (var name in _sectionNames)
        {
            if (FindName(name) is not FrameworkElement section)
            {
                continue;
            }

            var top = section.TransformToVisual(ContentScroll).Transform(new Point(0, 0)).Y;
            if (top <= 40 && top > bestTop)
            {
                bestTop = top;
                active = name;
            }
        }

        if (active is null)
        {
            active = _sectionNames[0];
        }

        if (active is not null)
        {
            SetActiveTag(active);
        }
    }

    private void SetActiveTag(string name)
    {
        foreach (ListBoxItem item in CategoryList.Items)
        {
            if (item.Tag is string t && t == name && !item.IsSelected)
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    // Sizes the bottom spacer so the last section can always scroll all the way to the top of the
    // viewport (otherwise there isn't enough content below it to reach the top band).
    private void ContentScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSpacerHeight();

    private void UpdateSpacerHeight()
    {
        if (BottomSpacer is null || ContentScroll.ViewportHeight <= 0)
        {
            return;
        }

        if (FindName(_sectionNames[^1]) is not FrameworkElement last)
        {
            return;
        }

        if (ContentScroll.Content is not FrameworkElement content)
        {
            return;
        }

        var lastTop = last.TransformToVisual(content).Transform(new Point(0, 0)).Y;
        var contentWithoutSpacer = lastTop + last.ActualHeight + last.Margin.Bottom;
        var needed = lastTop + ContentScroll.Padding.Top + ContentScroll.ViewportHeight;
        BottomSpacer.Height = Math.Max(0, needed - contentWithoutSpacer);
    }

    // Per-section state for the focus flash, so a repeat click can cancel the previous animation.
    private readonly Dictionary<Control, (Brush OrigBorder, Thickness OrigThickness, DropShadowEffect Glow)> _flashes = new();

    // Plays a glowing orange focus halo around the section that fades out over time. A glow (rather than
    // a solid border fade) reads well in both light and dark themes; a hard orange border looks muddy as
    // it fades over a dark background.
    private void FlashSection(Control section)
    {
        // Cancel any in-progress flash on this section so rapid clicks restart cleanly.
        if (_flashes.TryGetValue(section, out var prev))
        {
            prev.Glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            section.Effect = null;
            section.BorderBrush = prev.OrigBorder;
            section.BorderThickness = prev.OrigThickness;
            _flashes.Remove(section);
        }

        var origBorder = section.BorderBrush;
        var origThickness = section.BorderThickness;

        var glow = new DropShadowEffect
        {
            Color = Colors.Orange,
            ShadowDepth = 0,
            BlurRadius = 18,
            Opacity = 1
        };
        section.Effect = glow;

        var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(0.5)));
        anim.Completed += (_, _) =>
        {
            if (_flashes.TryGetValue(section, out var cur) && cur.Glow == glow)
            {
                section.Effect = null;
                section.BorderBrush = cur.OrigBorder;
                section.BorderThickness = cur.OrigThickness;
                _flashes.Remove(section);
            }
        };

        _flashes[section] = (origBorder, origThickness, glow);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, anim);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T t)
            {
                return t;
            }

            obj = VisualTreeHelper.GetParent(obj);
        }

        return null;
    }

    private void UpdatePreview()
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        var palette = BoxAppearance.Resolve(vm.ToUserSettings());

        PreviewBox.Background = palette.Back;
        PreviewBox.BorderBrush = palette.Border;
        PreviewBox.BorderThickness = palette.Thickness;
        PreviewHeader.Background = palette.HeaderBack;
        PreviewTitle.Foreground = palette.HeaderFore;

        // Re-apply the resolved foreground to every dynamically-built preview item (real icons keep
        // their own bitmap; only the fallback rectangles/label text are tinted to match the theme).
        foreach (var child in PreviewBody.Children.OfType<StackPanel>())
        {
            foreach (var tb in child.Children.OfType<System.Windows.Controls.TextBlock>())
            {
                tb.Foreground = palette.Fore;
            }

            foreach (var rc in child.Children.OfType<System.Windows.Shapes.Rectangle>())
            {
                rc.Fill = palette.Fore;
            }
        }
    }

    // Paints the area behind the preview box with the user's actual desktop background (wallpaper
    // image, or the solid desktop color) so the mock box looks like it sits on the real desktop.
    private void ApplyDesktopBackground()
    {
        var brush = GetDesktopBackgroundBrush();
        if (brush is not null)
        {
            PreviewDesktop.Background = brush;
        }
    }

    private static Brush? GetDesktopBackgroundBrush()
    {
        try
        {
            using var desk = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var wallpaper = desk?.GetValue("Wallpaper") as string;
            if (!string.IsNullOrEmpty(wallpaper) && File.Exists(wallpaper))
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(wallpaper);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                return new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            }

            using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            var bg = colors?.GetValue("Background") as string;
            if (!string.IsNullOrEmpty(bg))
            {
                var parts = bg.Split(' ');
                if (parts.Length == 3 &&
                    byte.TryParse(parts[0], out var r) &&
                    byte.TryParse(parts[1], out var g) &&
                    byte.TryParse(parts[2], out var b))
                {
                    return new SolidColorBrush(Color.FromRgb(r, g, b));
                }
            }
        }
        catch
        {
            // Best-effort: fall back to the default (unset) background if the registry read fails.
        }

        return null;
    }

    // Builds the preview's item row. When a real box container is open its actual items (icons + names)
    // are mirrored for realistic visuals; otherwise a few placeholder tiles are shown.
    private void BuildPreviewItems()
    {
        PreviewBody.Children.Clear();

        var mainVm = App.Services?.GetService<MainViewModel>();
        BoxViewModel? source = null;
        if (mainVm is not null)
        {
            foreach (var container in mainVm.Containers)
            {
                if (container.Type == DesktopItemContainerType.BoxContainer &&
                    container.ActiveBox?.Items is { Count: > 0 })
                {
                    source = container.ActiveBox;
                    break;
                }
            }
        }

        if (source is not null)
        {
            foreach (var item in source.Items.Take(8))
            {
                PreviewBody.Children.Add(MakePreviewItem(item, item.DisplayName));
            }
        }

        if (PreviewBody.Children.Count == 0)
        {
            for (int i = 0; i < 3; i++)
            {
                PreviewBody.Children.Add(MakePreviewItem(null, "Item"));
            }
        }
    }

    private static UIElement MakePreviewItem(BoxItemViewModel? item, string name)
    {
        var sp = new StackPanel { Width = 64, Margin = new Thickness(4) };

        if (item is not null)
        {
            // Bind to Icon so the image updates once the (asynchronously loaded) icon arrives.
            var img = new System.Windows.Controls.Image
            {
                Width = 32,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            img.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding(nameof(BoxItemViewModel.Icon)) { Source = item });
            sp.Children.Add(img);
        }
        else
        {
            sp.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 32,
                Height = 32,
                RadiusX = 4,
                RadiusY = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = Brushes.Gray
            });
        }

        sp.Children.Add(new System.Windows.Controls.TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 11,
            Text = name,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 60
        });

        return sp;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.Save();
        }

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #region Snapshot actions

    private static DesktopManager? Manager => App.Services?.GetRequiredService<DesktopManager>();

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Back up snapshot",
            Filter = "JSON snapshot (*.json)|*.json",
            FileName = "DesktopBoxes.snapshot.backup.json",
            DefaultExt = ".json"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var manager = Manager;
        if (manager is null)
        {
            return;
        }

        try
        {
            await manager.BackupAsync(dlg.FileName);
            System.Windows.MessageBox.Show("Snapshot backed up.", "Backup", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Backup failed: {ex.Message}", "Backup", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Restore snapshot",
            Filter = "JSON snapshot (*.json)|*.json",
            Multiselect = false
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var manager = Manager;
        if (manager is null)
        {
            return;
        }

        try
        {
            await manager.RestoreAsync(dlg.FileName);
            System.Windows.MessageBox.Show("Snapshot restored.", "Restore", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Restore failed: {ex.Message}", "Restore", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ResetSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "This will delete all boxes and rebuild from scratch. Continue?",
            "Confirm reset",
            System.Windows.MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var manager = Manager;
        if (manager is null)
        {
            return;
        }

        try
        {
            await manager.ResetAsync();
            System.Windows.MessageBox.Show("Snapshot reset.", "Reset", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Reset failed: {ex.Message}", "Reset", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion
}
