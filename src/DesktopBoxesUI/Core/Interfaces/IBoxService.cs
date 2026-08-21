using System.Collections.Generic;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Manages the collection of <see cref="Box"/> instances: creation, lookup, persistence hooks.
/// Platform-agnostic; the concrete implementation may persist to disk later.
/// </summary>
public interface IBoxService
{
    IReadOnlyList<Box> GetBoxes();

    Box? GetBox(Guid id);

    /// <summary>Creates (and registers) a new Box with the given geometry.</summary>
    Box CreateBox(string name, double left, double top, double width, double height);

    /// <summary>Registers an already-constructed Box (e.g. loaded from a snapshot).</summary>
    void AddBox(Box box);

    void UpdateBox(Box box);

    void RemoveBox(Guid id);

    /// <summary>Removes every registered box (used when rebuilding from scratch).</summary>
    void Clear();
}
