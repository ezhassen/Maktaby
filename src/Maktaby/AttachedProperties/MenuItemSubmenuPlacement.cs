using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Maktaby.AttachedProperties;

/// <summary>
/// Vertically centers a MenuItem's submenu on its parent row. A fixed <c>VerticalOffset</c> cannot
/// do this: the offset that looks right when the menu drops DOWN from the top detaches the
/// submenu (and kills hover) when the menu opens UPWARD from the bottom, because the geometry
/// flips. This installs a <see cref="Popup.CustomPopupPlacementCallback"/> on each submenu Popup,
/// computed from the real item/popup sizes at open time, so the submenu is centered on its row in
/// both directions and the cursor always enters it while still beside the parent item.
/// <para>
/// Hooked on <see cref="ContextMenu.Opened"/> (a class handler registered once), NOT on
/// MenuItem.Loaded: a Loaded class handler does not reliably fire for templated MenuItems, so the
/// callback was never installed. By Opened time every item's template is applied, so FindName
/// resolves the "Popup" reliably. Walked per open (cheap: only the items that actually have a
/// submenu), so a freshly built menu (tray, box) is always wired.
/// </para>
/// </summary>
public static class MenuItemSubmenuPlacement
{
    /// <summary>
    /// Installs the submenu-centering handler for every ContextMenu. Idempotent; call once at
    /// startup.
    /// </summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(
            typeof(ContextMenu),
            ContextMenu.OpenedEvent,
            new RoutedEventHandler(OnContextMenuOpened));
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            ApplyToItems(menu.Items);
        }
    }

    private static void ApplyToItems(System.Collections.IEnumerable items)
    {
        foreach (var obj in items)
        {
            if (obj is not MenuItem item) continue;
            if (item.Template?.FindName("Popup", item) is Popup popup)
            {
                popup.Placement = PlacementMode.Custom;
                popup.CustomPopupPlacementCallback = CenterOnTarget;
            }
            // Recurse so nested submenus are covered too.
            if (item.HasItems) ApplyToItems(item.Items);
        }
    }

    /// <summary>
    /// Places the submenu to the right of its row, TOP-aligned with the row (y = 0) so it opens
    /// from the top of the parent item, matching the requested behavior. Top alignment is also
    /// direction-safe: it never opens a vertical gap the cursor must cross, so hover tracking
    /// stays continuous. The centered and bottom-aligned entries are screen-fit fallbacks WPF
    /// picks only when top alignment would clip off-screen.
    /// </summary>
    private static CustomPopupPlacement[] CenterOnTarget(Size popupSize, Size targetSize, Point offset)
    {
        return new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width, 0), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(targetSize.Width, (targetSize.Height - popupSize.Height) / 2.0), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(targetSize.Width, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Horizontal),
        };
    }
}
