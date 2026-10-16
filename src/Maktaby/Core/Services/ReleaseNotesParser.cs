using Maktaby.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Maktaby.Core.Services;

/// <summary>
/// Turns a GitHub release body into the few lines a user actually wants: what changed.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous behaviour took the first five non-empty lines that did not
/// start with <c>**</c>. On the notes the release workflow generates, those five lines are the
/// title, the pre-release caveat, and the <em>download table</em> - a header row and two separator
/// rows. The user was shown a table of links and never saw a single change.
/// </para>
/// <para>
/// The body we generate has this shape:
/// <code>
/// ### Maktaby 1.0.34
/// &gt; **Pre-release** ...
/// **Download**
/// | Asset | Size | ... |
/// **SHA-256**
/// \`\`\`
/// abc123  Maktaby-....exe
/// \`\`\`
/// ### Changes
/// **Highlights**
/// - feat: add a thing
/// **Fixed**
/// - fix: correct another thing
/// **Internal**
/// - chore: bump something
/// </code>
/// so the parser deliberately reads only what follows <c>### Changes</c>, and drops everything
/// that is not a bullet or a heading. It stays tolerant: a body without a Changes heading yields
/// nothing rather than throwing, because the prompt degrades to "see the release page".
/// </para>
/// <para>
/// <b>Internal is dropped.</b> Dependency bumps and CI edits are real changes, but they are not
/// news, and a user reading an update prompt is skimming for what might affect them.
/// </para>
/// </remarks>
public static class ReleaseNotesParser
{
    /// <summary>Headings that introduce the change list, matched case-insensitively.</summary>
    private static readonly string[] ChangesHeadings =
    {
        "changes", "what's changed", "whats changed", "change log", "changelog",
    };

    /// <summary>Section headings that exist for maintainers, not for users.</summary>
    private static readonly string[] InternalHeadings =
    {
        "internal", "chores", "infra", "build", "ci",
    };

    private const int MaxItemsPerSection = 6;

    // Markdown to strip from an item. Links become their label: a bullet that reads as a URL is
    // noise in a 300px-wide dialog, and the section heading already told you what it was.
    private static readonly Regex LinkPattern = new(@"\[[^\]]*\]\((?<url>[^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex BareUrlPattern = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex ConventionalPrefix = new(
        @"^(?<type>[a-z]+)(\((?<scope>[^)]*)\))?!?:\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a release body into display-ready sections. Never throws; returns an empty list for
    /// anything it does not recognize.
    /// </summary>
    public static IReadOnlyList<ReleaseNotesSection> Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) { return Array.Empty<ReleaseNotesSection>(); }

        var lines = body.Replace("\r\n", "\n").Split('\n');

        int start = FindChangesStart(lines);
        if (start < 0) { return Array.Empty<ReleaseNotesSection>(); }

        var sections = new List<ReleaseNotesSection>();
        string? title = null;
        var items = new List<string>();
        int hidden = 0;
        bool inFence = false;

        void Flush()
        {
            if (title is null) { return; }
            if (items.Count > 0)
            {
                sections.Add(new ReleaseNotesSection
                {
                    Title = title,
                    Items = items.ToArray(),
                    HiddenItemCount = hidden,
                });
            }
            title = null;
            items.Clear();
            hidden = 0;
        }

        for (int i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                // The SHA-256 block lives up here in some bodies. Its contents are hashes, and
                // rendering 64 hex characters per installer as a bullet helps nobody.
                inFence = !inFence;
                continue;
            }
            if (inFence || line.Length == 0) { continue; }

            if (line.StartsWith("|", StringComparison.Ordinal)) { continue; }   // table row
            if (line.StartsWith("![", StringComparison.Ordinal)) { continue; }   // badge
            if (IsCompareFooter(line)) { continue; }

            var heading = HeadingText(line);
            if (heading is not null)
            {
                Flush();
                if (!IsInternal(heading)) { title = heading; }
                continue;
            }

            var item = BulletText(line);
            if (item is not null)
            {
                if (title is null) { continue; }   // stray bullet with no section: skip
                if (items.Count >= MaxItemsPerSection) { hidden++; continue; }
                items.Add(item);
            }
        }

        Flush();
        return sections;
    }

    /// <summary>Index of the first line after the changes heading, or -1.</summary>
    private static int FindChangesStart(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var heading = HeadingText(lines[i].Trim());
            if (heading is null) { continue; }
            if (ChangesHeadings.Contains(heading!.ToLowerInvariant())) { return i + 1; }
        }
        return -1;
    }

    /// <summary>
    /// The text of a markdown heading - <c>###</c>, <c>##</c> or a bold label - or null if the
    /// line is not a heading. Bold is included because that is what the workflow emits for its
    /// buckets.
    /// </summary>
    private static string? HeadingText(string line)
    {
        if (line.StartsWith("#", StringComparison.Ordinal))
        {
            return line.TrimStart('#').Trim('*', ' ', '\t').Trim();
        }

        if (line.Length > 4 && line[0] == '*' && line[^1] == '*' && line[1] == '*' && line[^2] == '*')
        {
            var text = line.Trim('*').Trim();
            // "**Pre-release** - built from..." is a bold phrase inside a sentence, not a heading.
            if (text.Length == 0 || line.Length > text.Length + 8) { return null; }
            return text;
        }

        return null;
    }

    private static string? BulletText(string line)
    {
        if (line.Length < 3) { return null; }
        if (!(line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
        {
            return null;
        }

        var text = Clean(line[2..].Trim());
        return text.Length == 0 ? null : text;
    }

    private static bool IsInternal(string heading) =>
        InternalHeadings.Contains(heading.Trim().ToLowerInvariant());

    private static bool IsCompareFooter(string line) =>
        line.StartsWith("[Compare", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("**Full Changelog", StringComparison.OrdinalIgnoreCase);

    /// <summary>Markdown and conventional-commit scaffolding off one bullet.</summary>
    private static string Clean(string text)
    {
        // "... (scope): subject" -> "subject". The type is already encoded by the section it is in,
        // so "fix: " in front of a bullet under Fixed is pure noise.
        text = ConventionalPrefix.Replace(text, string.Empty);

        text = LinkPattern.Replace(text, "${url}");
        text = BareUrlPattern.Replace(text, string.Empty);
        text = text.Replace("`", string.Empty);
        text = text.Replace("**", string.Empty).Replace("*", string.Empty);

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // "(#123)" issue references and a trailing full stop read badly in a one-line bullet.
        text = Regex.Replace(text, @"\s*\(#\d+\)", string.Empty);
        text = text.TrimEnd('.').Trim();

        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
