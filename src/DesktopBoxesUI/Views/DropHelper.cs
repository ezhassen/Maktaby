using System.Windows;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Shared drag/drop logic for accepting items into a <see cref="BoxViewModel"/>, used by both a
/// Box (BoxControl) and the empty-area <see cref="DesktopSurface"/>. Handles internal Box-item
/// moves and external file drops (Start Menu / File Explorer / desktop).
/// </summary>
internal static class DropHelper
{
    public static DragDropEffects GetEffect(DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DesktopBoxesItem"))
        {
            return DragDropEffects.Move;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return DragDropEffects.Copy;
        }

        return DragDropEffects.None;
    }

    /// <summary>Applies the dropped data to <paramref name="target"/>. Sets <see cref="DragEventArgs.Handled"/>.</summary>
    public static void AddToBox(BoxViewModel target, MainViewModel? host, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DesktopBoxesItem"))
        {
            if (e.Data.GetData("DesktopBoxesItem") is BoxItemViewModel item)
            {
                var source = host?.Boxes.FirstOrDefault(b => b.Items.Contains(item));
                if (source != null && source != target)
                {
                    source.RemoveItem(item.Model);
                    target.AddItem(item.Model);
                }

                e.Handled = true;
                return;
            }
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
            {
                var boxItem = BoxItemFactory.FromPath(path);
                if (boxItem is not null)
                {
                    target.AddItem(boxItem);
                }
            }

            e.Handled = true;
        }
    }
}
