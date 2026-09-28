using System.Collections.Generic;
using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Manages <see cref="BoxRule"/>s: storage, the always-present default rule, and matching a file name
/// to the box its rule feeds. Platform-agnostic and in-memory; the snapshot layer persists the rules.
/// </summary>
public interface IRuleService
{
    IReadOnlyList<BoxRule> GetRules();

    BoxRule GetDefaultRule();

    BoxRule? GetRule(System.Guid id);

    void AddRule(BoxRule rule);

    /// <summary>Removes a rule. The built-in default rule is protected and never removed.</summary>
    void RemoveRule(System.Guid id);

    void LoadRules(IEnumerable<BoxRule> rules);

    /// <summary>
    /// Ensures a default rule exists. If one is present but has no target, <paramref name="targetBoxId"/>
    /// is assigned to it.
    /// </summary>
    void EnsureDefaultRule(System.Guid? targetBoxId);

    /// <summary>Returns the target box id for a file name, or null if no rule applies.</summary>
    System.Guid? MatchTargetBoxId(string fileName);
}
