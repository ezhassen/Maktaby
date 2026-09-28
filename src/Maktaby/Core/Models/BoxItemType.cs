namespace Maktaby.Core.Models;

/// <summary>
/// The kind of item that lives inside a <see cref="Box"/>.
/// Kept intentionally small; extend as real desktop discovery is implemented.
/// </summary>
public enum BoxItemType
{
    Unknown = 0,
    File,
    Folder,
    Shortcut,
    Drive,
    SpecialFolder,
    App,
}
