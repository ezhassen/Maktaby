namespace Maktaby.Core.Models;

/// <summary>
/// Categories of filesystem change notifications surfaced by <see cref="IFileSystemService"/>.
/// </summary>
public enum FileSystemChangeKind
{
    Unknown = 0,
    Created,
    Deleted,
    Changed,
    Renamed,
}

/// <summary>
/// A single filesystem change event, already normalized away from raw Win32/Shell data.
/// </summary>
public sealed record FileSystemChange(FileSystemChangeKind Kind, string Path, string? OldPath = null);
