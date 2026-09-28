using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;

namespace Maktaby.Core.Services;

/// <summary>
/// In-memory, non-persistent implementation of <see cref="IBoxService"/>.
/// Persistence (save/load to disk) will be added later without changing this contract.
/// </summary>
public sealed class BoxService : IBoxService
{
    private readonly ObservableCollection<Box> _boxes = new();
    private readonly object _gate = new();

    public IReadOnlyList<Box> GetBoxes()
    {
        lock (_gate)
        {
            return _boxes.ToArray();
        }
    }

    public Box? GetBox(Guid id)
    {
        lock (_gate)
        {
            foreach (var box in _boxes)
            {
                if (box.Id == id)
                {
                    return box;
                }
            }
            return null;
        }
    }

    public Box CreateBox(string name, double left, double top, double width, double height)
    {
        // Geometry now lives on BoxContainer, not Box; the position is only used as a hint when the
        // box is first placed into a container.
        var box = new Box
        {
            Name = name,
        };

        lock (_gate)
        {
            _boxes.Add(box);
        }

        return box;
    }

    public void AddBox(Box box)
    {
        lock (_gate)
        {
            if (_boxes.Any(b => b.Id == box.Id))
            {
                return;
            }

            _boxes.Add(box);
        }
    }

    public void UpdateBox(Box box)
    {
        // No-op for the in-memory stub; persistence layer will flush changes later.
    }

    public void RemoveBox(Guid id)
    {
        lock (_gate)
        {
            for (var i = 0; i < _boxes.Count; i++)
            {
                if (_boxes[i].Id == id)
                {
                    _boxes.RemoveAt(i);
                    return;
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _boxes.Clear();
        }
    }
}
