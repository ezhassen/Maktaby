using System;
using System.Collections.Generic;
using System.Linq;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// In-memory, thread-safe implementation of <see cref="IRuleService"/>. The default rule
/// (Name = "default", FileType = "*.*") is always present and cannot be removed; it acts as the
/// fallback for any file not matched by a more specific (non-default) rule.
/// </summary>
public sealed class RuleService : IRuleService
{
    private readonly List<BoxRule> _rules = new();
    private readonly object _gate = new();

    public IReadOnlyList<BoxRule> GetRules()
    {
        lock (_gate)
        {
            return _rules.ToArray();
        }
    }

    public BoxRule GetDefaultRule()
    {
        lock (_gate)
        {
            return _rules.FirstOrDefault(r => r.IsDefault)
                ?? new BoxRule { Name = "default", FileType = "*.*", IsDefault = true };
        }
    }

    public BoxRule? GetRule(Guid id)
    {
        lock (_gate)
        {
            return _rules.FirstOrDefault(r => r.Id == id);
        }
    }

    public void AddRule(BoxRule rule)
    {
        lock (_gate)
        {
            if (_rules.Any(r => r.Id == rule.Id))
            {
                return;
            }

            _rules.Add(rule);
        }
    }

    public void RemoveRule(Guid id)
    {
        lock (_gate)
        {
            var existing = _rules.FirstOrDefault(r => r.Id == id);
            if (existing != null && !existing.IsDefault)
            {
                _rules.Remove(existing);
            }
        }
    }

    public void LoadRules(IEnumerable<BoxRule> rules)
    {
        lock (_gate)
        {
            _rules.Clear();
            _rules.AddRange(rules);
        }
    }

    public void EnsureDefaultRule(Guid? targetBoxId)
    {
        lock (_gate)
        {
            var def = _rules.FirstOrDefault(r => r.IsDefault);
            if (def == null)
            {
                _rules.Add(new BoxRule
                {
                    Name = "default",
                    FileType = "*.*",
                    IsDefault = true,
                    TargetBoxId = targetBoxId,
                });
            }
            else if (def.TargetBoxId == null && targetBoxId != null)
            {
                def.TargetBoxId = targetBoxId;
            }
        }
    }

    public Guid? MatchTargetBoxId(string fileName)
    {
        lock (_gate)
        {
            // Non-default rules take priority; the default rule is the fallback.
            foreach (var rule in _rules)
            {
                if (rule.IsDefault || rule.TargetBoxId is null)
                {
                    continue;
                }

                if (Matches(rule.FileType, fileName))
                {
                    return rule.TargetBoxId;
                }
            }

            var def = _rules.FirstOrDefault(r => r.IsDefault);
            if (def != null && def.TargetBoxId != null && Matches(def.FileType, fileName))
            {
                return def.TargetBoxId;
            }
        }

        return null;
    }

    private static bool Matches(string fileType, string fileName)
    {
        foreach (var raw in fileType.Split(';'))
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }

            if (GlobMatch(pattern, fileName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Case-insensitive simple glob supporting '*' wildcards only (e.g. "*.txt", "*.*").</summary>
    private static bool GlobMatch(string pattern, string name)
    {
        pattern = pattern.ToLowerInvariant();
        name = name.ToLowerInvariant();

        if (pattern == "*.*" || pattern == "*")
        {
            return true;
        }

        int pi = 0, ni = 0;
        int star = -1, mark = -1;

        while (ni < name.Length)
        {
            if (pi < pattern.Length && pattern[pi] == name[ni])
            {
                pi++;
                ni++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi;
                mark = ni;
                pi++;
            }
            else if (star != -1)
            {
                pi = star + 1;
                ni = mark + 1;
                mark++;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*')
        {
            pi++;
        }

        return pi == pattern.Length;
    }
}
