namespace Maktaby.Core.Models;

/// <summary>
/// A rule that automatically routes newly created/deleted desktop files of a given type into a target
/// <see cref="Box"/>. The default rule (Name = "default", FileType = "*.*") catches every file and feeds
/// the default box; it cannot be deleted, and its target box cannot be deleted either.
/// </summary>
public sealed class BoxRule
{
    public System.Guid Id { get; init; } = System.Guid.NewGuid();

    public string Name { get; set; } = "default";

    /// <summary>File-type patterns, semicolon-separated (e.g. "*.txt", "*.png;*.jpg", "*.*").</summary>
    public string FileType { get; set; } = "*.*";

    /// <summary>True for the built-in catch-all rule. The default rule cannot be removed.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The box this rule feeds. For the default rule this is the default box.</summary>
    public System.Guid? TargetBoxId { get; set; }
}
