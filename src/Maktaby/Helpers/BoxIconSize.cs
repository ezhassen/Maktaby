using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Maktaby.Helpers;

/// <summary>
/// The one place that resolves the icon size a box's items are displayed at.
/// </summary>
/// <remarks>
/// Three sources, in order: the box's own override, the user's default, and the live desktop icon
/// size (what Explorer is using right now). The box's view-models and the control that shows them
/// must agree on it — when they disagree, an item resolves its icon twice: once at the wrong size
/// and again when the correct one arrives.
/// </remarks>
internal static class BoxIconSize
{
    public const int Min = 16;
    public const int Max = 128;

    /// <summary>Default when nothing says otherwise.</summary>
    public const int Fallback = 32;

    /// <summary>
    /// The icon size the folder portal's Details view uses. Fixed: a list row is a text row, and a
    /// large icon in it is decoration that also costs a bigger bitmap per file.
    /// </summary>
    public const int DetailsViewIconSize = 32;

    /// <summary>The display size (in device-independent pixels) for a box's items.</summary>
    public static int Resolve(Box? box)
    {
        int? resolved = null;

        try
        {
            resolved = box?.IconSize
                ?? App.Services.GetRequiredService<ISettingsService>().UserSettings.DefaultBoxIconSize
                ?? App.Services.GetRequiredService<IDesktopIconSizeService>().Current;
        }
        catch
        {
            // The services are not up yet (or a box is being built during shutdown): the default
            // is better than failing to draw.
        }

        return Math.Clamp(resolved ?? Fallback, Min, Max);
    }

    /// <summary>
    /// The icon size in effect for a folder portal: the fixed small one in the Details view, the
    /// user's size in the Icons view.
    /// </summary>
    /// <remarks>
    /// The view-models and the control that shows them must agree on the size — when they
    /// disagree, an item resolves its icon twice: once at the wrong size and again when the
    /// correct one arrives. That is why this lives here rather than in the control.
    /// </remarks>
    public static int ResolveForView(Box? box, FolderPortalViewMode viewMode) =>
        viewMode == FolderPortalViewMode.Details ? DetailsViewIconSize : Resolve(box);
}
