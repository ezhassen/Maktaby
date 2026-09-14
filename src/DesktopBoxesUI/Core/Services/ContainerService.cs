using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using System.Collections.Generic;
using System.Linq;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// In-memory store of <see cref="DesktopItemContainer"/>s. Thread-safe; the persistence layer flushes
/// the whole collection when saving.
/// </summary>
public sealed class ContainerService : IContainerService
{
    private readonly List<DesktopItemContainer> _containers = new();
    private readonly object _gate = new();

    public IReadOnlyList<DesktopItemContainer> GetContainers()
    {
        lock (_gate)
        {
            return _containers.AsReadOnly();
        }
    }

    public DesktopItemContainer? GetContainer(System.Guid id)
    {
        lock (_gate)
        {
            return _containers.FirstOrDefault(c => c.Id == id);
        }
    }

    public DesktopItemContainer CreateContainer(
        DesktopItemContainerType type,
        double left,
        double top,
        double width,
        double height,
        BoxContainer? childContainer = null,
        string? customTypeName = null,
        string? customData = null)
    {
        var bounds = RectD.FromXYWH(left, top, width, height);
        var container = new DesktopItemContainer
        {
            Type = type,
            Bounds = bounds,
            IsLocked = false,
            IsVisible = true,
        };

        if (type == DesktopItemContainerType.BoxContainer)
        {
            childContainer ??= new BoxContainer();
            ClampSelected(childContainer);
            container.ChildContainer = childContainer;
        }
        else if (type == DesktopItemContainerType.WebWidget)
        {
            // For WebWidget the slug/source are preferred via CreateWebWidgetContainer.
            // Fallback: treat customTypeName as slug and customData as source string.
            container.WebWidgetName = customTypeName;
            if (Enum.TryParse<WebWidgetSource>(customData, true, out var src))
                container.WebWidgetSource = src;
        }
        else
        {
            container.CustomTypeName = customTypeName;
            container.CustomData = customData;
        }

        lock (_gate)
        {
            _containers.Add(container);
        }

        return container;
    }

    public DesktopItemContainer CreateWebWidgetContainer(
        double left,
        double top,
        double width,
        double height,
        string slug,
        WebWidgetSource source)
    {
        var bounds = RectD.FromXYWH(left, top, width, height);
        var container = new DesktopItemContainer
        {
            Type = DesktopItemContainerType.WebWidget,
            Bounds = bounds,
            IsLocked = false,
            IsVisible = true,
            WebWidgetName = slug,
            WebWidgetSource = source
        };
        lock (_gate)
        {
            _containers.Add(container);
        }
        return container;
    }

    public DesktopItemContainer CreateNativeWidgetContainer(
        double left,
        double top,
        double width,
        double height,
        string slug)
    {
        var bounds = RectD.FromXYWH(left, top, width, height);
        var container = new DesktopItemContainer
        {
            Type = DesktopItemContainerType.NativeWidget,
            Bounds = bounds,
            IsLocked = false,
            IsVisible = true,
            NativeWidgetName = slug
        };
        lock (_gate)
        {
            _containers.Add(container);
        }
        return container;
    }

    public void AddContainer(DesktopItemContainer container)
    {
        lock (_gate)
        {
            if (_containers.Any(c => c.Id == container.Id))
            {
                return;
            }

            _containers.Add(container);
        }
    }

    public void RemoveContainer(System.Guid id)
    {
        lock (_gate)
        {
            for (var i = 0; i < _containers.Count; i++)
            {
                if (_containers[i].Id == id)
                {
                    _containers.RemoveAt(i);
                    return;
                }
            }
        }
    }

    private static void ClampSelected(BoxContainer container)
    {
        if (container.Boxes.Count == 0)
        {
            container.SelectedIndex = 0;
        }
        else if (container.SelectedIndex < 0 || container.SelectedIndex >= container.Boxes.Count)
        {
            container.SelectedIndex = 0;
        }
    }
}
