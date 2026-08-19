using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Shell.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;

namespace DesktopBoxesUI.Controls;

/// <summary>
/// Renders the items inside a Box and handles dragging an item out (to another Box) and dropping
/// an item in (from another Box). Also opens an item when activated, matching the user's Explorer
/// single/double-click preference. The cross-box move and persistence are coordinated through the
/// shared <see cref="MainViewModel"/> and the <see cref="RequestSave"/> callback.
/// </summary>
public partial class BoxControl : UserControl
{
    private Point _dragStart;
    private bool _dragging;
    private bool _moved;
    private readonly bool _singleClick = ShellSettings.IsSingleClickToOpen();

    public BoxControl() => InitializeComponent();

    public MainViewModel? Host { get; set; }

    public IPersistenceService? Persistence { get; set; }

    public System.Action? RequestSave { get; set; }

    private void ItemBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragging = false;
        _moved = false;

        if (!_singleClick && e.ClickCount == 2 && sender is Border { DataContext: BoxItemViewModel item })
        {
            OpenItem(item);
        }
    }

    private void ItemBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging)
        {
            return;
        }

        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) <= 4 && Math.Abs(diff.Y) <= 4)
        {
            return;
        }

        _moved = true;
        if (sender is Border { DataContext: BoxItemViewModel item })
        {
            _dragging = true;
            var data = new DataObject("DesktopBoxesItem", item);
            DragDrop.DoDragDrop((Border)sender, data, DragDropEffects.Move);
            _dragging = false;
        }
    }

    private void ItemBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_singleClick && !_moved && !_dragging && sender is Border { DataContext: BoxItemViewModel item })
        {
            OpenItem(item);
        }
    }

    private static void OpenItem(BoxItemViewModel item)
    {
        var path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
        }
        catch
        {
            // Best-effort; ignore failures (e.g. access denied).
        }
    }

    private void BoxControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropHelper.GetEffect(e);
        e.Handled = true;
    }

    private void BoxControl_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not BoxViewModel targetBox)
        {
            return;
        }

        DropHelper.AddToBox(targetBox, Host, e);

        if (e.Handled)
        {
            RequestSave?.Invoke();
        }
    }
}
