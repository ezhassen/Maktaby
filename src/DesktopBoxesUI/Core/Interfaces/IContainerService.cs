using System.Collections.Generic;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Manages the in-memory collection of <see cref="DesktopItemContainer"/>s (creation, lookup, removal).
/// Persistence of the collection is handled separately by <see cref="IPersistenceService"/>.
/// </summary>
public interface IContainerService
{
    IReadOnlyList<DesktopItemContainer> GetContainers();

    DesktopItemContainer? GetContainer(System.Guid id);

    /// <summary>Creates (and registers) a new container with the given geometry and child.</summary>
    DesktopItemContainer CreateContainer(
        DesktopItemContainerType type,
        double left,
        double top,
        double width,
        double height,
        BoxContainer? childContainer = null,
        string? customTypeName = null,
        string? customData = null);

    DesktopItemContainer CreateCssWidgetContainer(
        double left,
        double top,
        double width,
        double height,
        string slug,
        CssWidgetSource source);

    void AddContainer(DesktopItemContainer container);

    void RemoveContainer(System.Guid id);
}
