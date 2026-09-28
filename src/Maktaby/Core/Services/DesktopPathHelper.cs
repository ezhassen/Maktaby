using System;
using System.IO;

namespace Maktaby.Core.Services;

/// <summary>
/// Keeps <see cref="BoxItem.Path"/> portable across users/machines. Filesystem paths that live under a
/// known, per-user (or per-machine) root — the current or public desktop, the user profile folder
/// (C:\Users\&lt;user&gt;) and its known subfolders such as Music/Downloads/Documents, or the public
/// profile folder (C:\Users\Public) — are stored as a short tokenised relative form; everything else is
/// stored verbatim. The in-memory <see cref="BoxItem.Path"/> always holds a fully resolved path.
/// </summary>
public static class DesktopPathHelper
{
    public const string UserDesktopPrefix = "desktop://";
    public const string CommonDesktopPrefix = "common-desktop://";
    public const string UserProfilePrefix = "profile://";
    public const string PublicProfilePrefix = "public://";

    private static readonly string UserDesktop =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private static readonly string CommonDesktop =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string PublicProfile =
        !string.IsNullOrEmpty(CommonDesktop) ? Path.GetDirectoryName(CommonDesktop) ?? CommonDesktop : string.Empty;

    /// <summary>Converts a full path into its portable stored form.</summary>
    public static string ToPortable(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return fullPath;
        }

        // Check the most specific roots first so e.g. desktop items keep their own token rather than
        // collapsing to a profile-relative one.
        if (TryGetRelative(fullPath, UserDesktop, out var rel))
        {
            return UserDesktopPrefix + rel;
        }

        if (TryGetRelative(fullPath, CommonDesktop, out rel))
        {
            return CommonDesktopPrefix + rel;
        }

        if (TryGetRelative(fullPath, PublicProfile, out rel))
        {
            return PublicProfilePrefix + rel;
        }

        if (TryGetRelative(fullPath, UserProfile, out rel))
        {
            return UserProfilePrefix + rel;
        }

        return fullPath;
    }

    /// <summary>Resolves a stored (possibly portable) path back into a full filesystem path.</summary>
    public static string ToFullPath(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        if (stored.StartsWith(UserDesktopPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Combine(UserDesktop, stored.Substring(UserDesktopPrefix.Length));
        }

        if (stored.StartsWith(CommonDesktopPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Combine(CommonDesktop, stored.Substring(CommonDesktopPrefix.Length));
        }

        if (stored.StartsWith(PublicProfilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Combine(PublicProfile, stored.Substring(PublicProfilePrefix.Length));
        }

        if (stored.StartsWith(UserProfilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Combine(UserProfile, stored.Substring(UserProfilePrefix.Length));
        }

        return stored;
    }

    private static bool TryGetRelative(string fullPath, string baseDir, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        if (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fullPath.Length == baseDir.Length)
        {
            relative = string.Empty;
            return true;
        }

        var sep = fullPath[baseDir.Length];
        if (sep != Path.DirectorySeparatorChar && sep != Path.AltDirectorySeparatorChar)
        {
            return false;
        }

        relative = fullPath.Substring(baseDir.Length + 1);
        return true;
    }

    private static string Combine(string baseDir, string relative)
    {
        if (string.IsNullOrEmpty(baseDir))
        {
            return relative;
        }

        if (string.IsNullOrEmpty(relative))
        {
            return baseDir;
        }

        var trimmed = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) ? baseDir : Path.Combine(baseDir, trimmed);
    }
}
