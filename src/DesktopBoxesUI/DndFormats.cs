namespace DesktopBoxesUI;

/// <summary>
/// Format names used for the in-process drag/drop payloads (container/tab merge and move).
/// Keeping them in one place avoids string mismatches between the drag source and the drop target.
/// </summary>
internal static class DndFormats
{
    /// <summary>The source <see cref="ContainerViewModel"/> being dragged.</summary>
    public const string SourceContainer = "DesktopBoxes/SourceContainer";

    /// <summary>The specific <see cref="Core.Models.Box"/> being dragged (tab move); absent for a whole-container merge.</summary>
    public const string Box = "DesktopBoxes/Box";

    /// <summary>The list of <see cref="ViewModels.BoxItemViewModel"/> being dragged (internal item move).</summary>
    public const string BoxItems = "DesktopBoxes/BoxItems";
}
