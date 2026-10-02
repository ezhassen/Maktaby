using Maktaby.Core.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.Core.Interfaces;

/// <summary>Reads published releases and decides whether one is an upgrade.</summary>
public interface IUpdateService
{
    /// <summary>
    /// Fetches the newest release on <paramref name="channel"/> and compares it with
    /// <paramref name="currentVersion"/>. Never throws for network or API conditions — those
    /// come back as <see cref="UpdateCheckStatus.Unavailable"/> — because a background check
    /// failing is normal, not exceptional.
    /// </summary>
    /// <param name="currentVersion">The running build, e.g. <c>1.0.32-beta.4</c>.</param>
    /// <param name="skippedVersion">A version the user chose to skip, or null.</param>
    Task<UpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        string currentVersion,
        string? skippedVersion,
        CancellationToken cancellationToken = default);
}
