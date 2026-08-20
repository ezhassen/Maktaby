using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Window for a <see cref="DesktopItemContainerType.BoxContainer"/>: shows the active tab's
/// <see cref="BoxControl"/>, a tab strip (only when more than one tab exists), and a header whose
/// title/menu operate on the selected box. Geometry and styling come from the <see cref="ContainerViewModel"/>
/// (i.e. the owning <see cref="DesktopItemContainer"/>). Movement/resize is delegated to
/// <see cref="WindowDragController"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class BoxContainerWindow : Window
{
    private readonly ContainerViewModel _vm;
    private readonly MainViewModel _host;
    private readonly IWindowPositioningService _positioning;
    private readonly Action _save;
    private readonly WindowDragController _drag;

    public BoxContainerWindow(ContainerViewModel vm, MainViewModel host, IWindowPositioningService positioning, Action save)
    {
        InitializeComponent();

        _vm = vm;
        _host = host;
        _positioning = positioning;
        _save = save;

        var monitor = App.Services.GetRequiredService<IMonitorService>();
        var dpi = App.Services.GetRequiredService<IDpiService>();
        var snapping = App.Services.GetRequiredService<IWindowSnappingService>();

        DataContext = vm;

        ApplyTransparency();

        Left = vm.Left;
        Top = vm.Top;
        Width = vm.Width;
        Height = vm.Height;

        _drag = new WindowDragController(
            this,
            monitor,
            dpi,
            snapping,
            _positioning,
            () => RectD.FromXYWH(_vm.Left, _vm.Top, _vm.Width, _vm.Height),
            r =>
            {
                _vm.Left = r.X;
                _vm.Top = r.Y;
                _vm.Width = r.Width;
                _vm.Height = r.Height;
            },
            () => _host.Containers.Where(c => c.Id != _vm.Id).Select(c => c.Bounds).ToList(),
            _save,
            () => HeaderBorder.ActualHeight);

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyType();
        UpdateBody();
        _drag.Attach();
    }

    /// <summary>Adjusts which parts of the window are visible based on the container type.</summary>
    private void ApplyType()
    {
        bool isBox = _vm.BoxContainerVm != null;
        AddRemoveButtons.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        BoxContent.Visibility = isBox ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Visibility = isBox ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e) => _drag.Detach();

    private void ApplyTransparency()
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        double? global = settings.GetValue<double>(SettingsKeys.DefaultBoxTransparency);
        double? effective = _vm.Transparency ?? global;
        var opacity = System.Math.Clamp(1.0 - (effective ?? SettingsKeys.DefaultBoxTransparencyValue), 0.0, 1.0);
        RootBorder.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)) { Opacity = opacity };
        HeaderBorder.Background = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)) { Opacity = opacity };
        TabStrip.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x37)) { Opacity = opacity };
    }

    private void UpdateBody()
    {
        if (_vm.ActiveBox != null)
        {
            BoxContent.DataContext = _vm.ActiveBox;
            BoxContent.RequestSave = _save;
            BoxContent.Host = _host;
        }
    }

    private void TitleArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _drag.BeginTitleDrag(e);

    private void TitleArea_MouseMove(object sender, MouseEventArgs e) => _drag.TitleDrag(e);

    private void TitleArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _drag.EndTitleDrag(e);

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        _vm.AddTabCommand.Execute(null);
        UpdateBody();
    }

    private void RemoveTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActiveBox is { Items.Count: > 0 })
        {
            var result = MessageBox.Show(
                "This box contains items. Delete it anyway?",
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _vm.RemoveTabCommand.Execute(null);
        UpdateBody();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is BoxViewModel box && _vm.BoxContainerVm != null)
        {
            int idx = _vm.BoxContainerVm.Tabs.IndexOf(box);
            if (idx >= 0)
            {
                _vm.BoxContainerVm.SelectedIndex = idx;
                UpdateBody();
            }
        }
    }

    private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _vm.ActiveBox != null)
        {
            TitleEdit.Text = _vm.ActiveBox.Name;
            TitleText.Visibility = Visibility.Collapsed;
            TitleEdit.Visibility = Visibility.Visible;
            TitleEdit.Focus();
            TitleEdit.SelectAll();
        }
    }

    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
        }
        else if (e.Key == Key.Escape)
        {
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }
    }

    private void CommitRename()
    {
        if (_vm.ActiveBox != null)
        {
            _vm.ActiveBox.Name = TitleEdit.Text;
        }

        TitleText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();

        TitleEdit.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        _save();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        BoxMenu.PlacementTarget = MenuButton;
        BoxMenu.IsOpen = true;
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.BoxContainerVm != null && _vm.BoxContainerVm.Tabs.Any(t => t.Items.Count > 0))
        {
            var result = MessageBox.Show(
                "This container contains items. Delete it anyway?",
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _host.RemoveContainer(_vm);
        _save();
        Close();
    }

    private void MenuHide_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsVisible = false;
        _save();
        Close();
    }

    private void MenuLock_Click(object sender, RoutedEventArgs e)
    {
        _vm.IsLocked = !_vm.IsLocked;
        _save();
    }
}
