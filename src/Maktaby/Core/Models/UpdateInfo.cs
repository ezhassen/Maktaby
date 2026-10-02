using System;

namespace Maktaby.Core.Models;

/// <summary>Which releases the update checker is willing to offer. The two real channels are
/// a strict partition: Stable and Beta never overlap, so neither can leak into the
/// other.</summary>
public enum UpdateChannel
{
    /// <summary>Follow the installed build: a prerelease install tracks prereleases, a
    /// stable install tracks stable releases. This is the default.</summary>
    Auto = 0,

    /// <summary>Only published, non-prerelease releases.</summary>
    Stable = 1,

    /// <summary>Only pre-releases. Stable releases are NOT offered here, so a beta
    /// tester stays on the beta line until they deliberately switch this to
    /// <see cref="Stable"/> (or install a stable build).</summary>
    Beta = 2,
}

/// <summary>A published release the checker has decided is worth offering.</summary>
public sealed class UpdateInfo
{
    /// <summary>Clean version, no leading 'v' and no '+sha' build metadata.</summary>
    public required string Version { get; init; }

    /// <summary>The GitHub tag, e.g. <c>v1.0.33-beta.1</c>.</summary>
    public required string TagName { get; init; }

    /// <summary>Direct download URL of the verified installer asset.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>SHA-256 from the release asset's <c>digest</c>, lowercase hex, no prefix.
    /// Empty when GitHub did not supply one — the downloader then refuses to run the file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size in bytes, used to show progress.</summary>
    public required long SizeBytes { get; init; }

    public required bool IsPreRelease { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Page a user can read for the changelog.</summary>
    public required string ReleasePageUrl { get; init; }

    /// <summary>First lines of the generated release notes, for display in the prompt.</summary>
    public string? ReleaseNotes { get; init; }
}

/// <summary>Why a check produced no update, for logging and the manual "check now" result.</summary>
public enum UpdateCheckStatus
{
    /// <summary>A newer release was found and is being offered.</summary>
    UpdateAvailable = 0,

    /// <summary>The newest release on the channel is the one already installed.</summary>
    UpToDate = 1,

    /// <summary>The user chose "Skip this version" for exactly this version.</summary>
    Skipped = 2,

    /// <summary>No release could be read (network down, rate limited, repo not reachable).
    /// Never surfaced to the user on a background check — logged and ignored.</summary>
    Unavailable = 3,
}

/// <summary>Outcome of one update check.</summary>
public sealed class UpdateCheckResult
{
    public required UpdateCheckStatus Status { get; init; }
    public UpdateInfo? Update { get; init; }

    /// <summary>Short technical reason, for the log only. Never shown to the user on a
    /// background check — a silent no-op is correct when the network is simply unavailable.</summary>
    public string? Diagnostic { get; init; }
}
