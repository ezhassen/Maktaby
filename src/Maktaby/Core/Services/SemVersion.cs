using System;
using System.Collections.Generic;
using System.Globalization;

namespace Maktaby.Core.Services;

/// <summary>
/// A parsed semantic version, including the prerelease label MinVer produces
/// (<c>1.0.32-beta.4</c>) and the build metadata it appends (<c>+9a3f1c2</c>).
/// </summary>
/// <remarks>
/// This exists because the update checker's only real failure mode is a wrong comparison: a
/// plain <see cref="string"/> compare says "1.0.32-beta.10" &lt; "1.0.32-beta.9", so a user
/// would be told to "update" to an older build and be told to "update" forever.
/// <para>
/// Implements the precedence rules of semver.org §11 without taking a dependency:
/// numeric identifiers compare numerically, alphanumeric ones lexically in ASCII order, a
/// prerelease sorts BELOW the same version without one, and build metadata is ignored.
/// </para>
/// </remarks>
public sealed class SemVersion : IComparable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Dot-separated prerelease identifiers, empty for a stable release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>Build metadata after '+'. Ignored for ordering, kept for display.</summary>
    public string Build { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    public SemVersion(int major, int minor, int patch, IReadOnlyList<string>? preRelease = null, string build = "")
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease ?? Array.Empty<string>();
        Build = build ?? string.Empty;
    }

    /// <summary>
    /// Parses a version string, tolerating a leading <c>v</c> and a missing patch component
    /// (<c>v1.2</c> becomes <c>1.2.0</c>). Returns false rather than throwing, because the input
    /// comes from a third party (a GitHub tag).
    /// </summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) { return false; }

        var s = text.Trim().TrimStart('v', 'V');

        string build = string.Empty;
        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            build = s[(plus + 1)..];
            s = s[..plus];
        }

        string pre = string.Empty;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
        }

        var parts = s.Split('.');
        if (parts.Length is 0 or > 3) { return false; }

        var numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
            if (numbers[i] < 0) { return false; }
        }

        var identifiers = pre.Length == 0
            ? Array.Empty<string>()
            : pre.Split('.');

        version = new SemVersion(numbers[0], numbers[1], numbers[2], identifiers, build);
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than this version.</summary>
    public bool IsOlderThan(SemVersion candidate) => candidate.CompareTo(this) > 0;

    public int CompareTo(SemVersion? other)
    {
        if (other is null) { return 1; }

        int c = Major.CompareTo(other.Major);
        if (c != 0) { return c; }

        c = Minor.CompareTo(other.Minor);
        if (c != 0) { return c; }

        c = Patch.CompareTo(other.Patch);
        if (c != 0) { return c; }

        // A prerelease has LOWER precedence than the release it leads to:
        // 1.0.0-beta < 1.0.0.
        if (PreRelease.Count == 0 && other.PreRelease.Count == 0) { return 0; }
        if (PreRelease.Count == 0) { return 1; }
        if (other.PreRelease.Count == 0) { return -1; }

        // Compare identifier by identifier; a smaller set of otherwise-equal ids wins, so
        // 1.0.0-beta < 1.0.0-beta.1.
        int shared = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (int i = 0; i < shared; i++)
        {
            c = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (c != 0) { return c; }
        }

        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool an = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int ai);
        bool bn = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out int bi);

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (an && bn) { return ai.CompareTo(bi); }        // 10 > 9, not "10" < "9"
        if (an) { return -1; }
        if (bn) { return 1; }
        return string.CompareOrdinal(a, b);
    }

    public override string ToString()
    {
        var s = $"{Major}.{Minor}.{Patch}";
        if (PreRelease.Count > 0) { s += "-" + string.Join(".", PreRelease); }
        if (Build.Length > 0) { s += "+" + Build; }
        return s;
    }
}
