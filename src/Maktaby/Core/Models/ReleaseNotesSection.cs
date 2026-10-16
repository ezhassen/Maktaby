using System;
using System.Collections.Generic;

namespace Maktaby.Core.Models;

/// <summary>
/// One heading's worth of release notes, already stripped of the markdown the release body is
/// written in. Produced by <c>ReleaseNotesParser</c>.
/// </summary>
/// <remarks>
/// A structure rather than a string because the update prompt renders headings and bullets as
/// text, and because the section title is the one part that benefits from being localized - the
/// generated notes are English-only, but an Arabic session should still read "أبرز التغييرات"
/// over a list of English bullets.
/// </remarks>
public sealed class ReleaseNotesSection
{
    /// <summary>
    /// Heading as written in the release body, e.g. <c>Highlights</c>. Kept verbatim so an
    /// unrecognized heading (a hand-written release, a renamed bucket) still displays.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>Bullet items, markdown removed.</summary>
    public required IReadOnlyList<string> Items { get; init; }

    /// <summary>
    /// Items dropped by the per-section cap. Non-zero means the list above is a summary; the
    /// release page has the rest, so the prompt says so rather than implying that is everything.
    /// </summary>
    public int HiddenItemCount { get; init; }
}
