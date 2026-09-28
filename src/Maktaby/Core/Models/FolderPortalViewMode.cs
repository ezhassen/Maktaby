namespace Maktaby.Core.Models;

/// <summary>Visual layout for a <see cref="BoxType.FolderPortal"/> box.</summary>
public enum FolderPortalViewMode
{
    Icons = 0,
    Details = 1,
}

/// <summary>Sort key for FolderPortal Details/Icons view.</summary>
public enum FolderSortMode
{
    Name = 0,
    DateModified = 1,
    Size = 2,
    Type = 3,
}
