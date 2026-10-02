using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.Core.Services;

/// <summary>
/// Checks GitHub Releases for a newer Maktaby build.
/// </summary>
/// <remarks>
/// Unauthenticated by design. A shipped desktop app cannot hold a GitHub token — it would be
/// readable by every user of the app — so the repository has to stay public for this to work
/// at all, and a private repo answers 404 (which surfaces as
/// <see cref="UpdateCheckStatus.Unavailable"/>, never as "no update").
/// <para>
/// Every failure mode is a value, not an exception: a desktop app that throws because a Wi-Fi
/// router rebooted would be reporting its own bug as an outage.
/// </para>
/// </remarks>
public sealed class UpdateService : IUpdateService, IDisposable
{
    private const string RepoOwner = "ezhassen";
    private const string RepoName = "Maktaby";
    private const string UserAgent = "Maktaby-Desktop-UpdateCheck";

    /// <summary>Only the release channel is needed, not every release ever.</summary>
    private const int ReleasesToFetch = 10;

    /// <summary>The single asset shape the release workflow publishes.</summary>
    private const string InstallerAssetSuffix = "-x64-setup.exe";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,

        // REQUIRED, not cosmetic: GitHub's REST API returns snake_case (tag_name,
        // browser_download_url, published_at). PropertyNameCaseInsensitive only folds CASE -
        // it does not fold underscores - so without this policy every DTO field silently
        // deserialises to its default and the checker reports "up to date" against a repo
        // that clearly has newer releases. Caught by running the real service against
        // api.github.com/repos/ezhassen/Maktaby/releases.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public UpdateService() : this(null) { }

    public UpdateService(HttpMessageHandler? handler)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        // GitHub rejects API requests without a User-Agent outright (403).
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateChannel channel,
        string currentVersion,
        string? skippedVersion,
        CancellationToken cancellationToken = default)
    {
        if (!SemVersion.TryParse(currentVersion, out var current))
        {
            // A dev build (no tag reachable) has no meaningful comparison. Not an error.
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.Unavailable,
                Diagnostic = $"Cannot parse the running version '{currentVersion}'.",
            };
        }

        var effective = ResolveChannel(channel, current);

        List<ReleaseDto> releases;
        try
        {
            releases = await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException, not OCE.
            return Fail($"Request timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"Malformed release JSON: {ex.Message}");
        }

        if (releases.Count == 0)
        {
            return Fail("No releases returned.");
        }

        foreach (var release in releases)
        {
            if (release.Draft) { continue; }

            // The two channels are a strict partition, not a subset of each other:
            //   Stable -> only published releases
            //   Beta   -> only pre-releases
            // Beta deliberately EXCLUDES stable releases. Accepting them would mean a beta
            // tester is silently moved onto the stable line by a version they never opted
            // into - the release workflow's "promote" step is the user's decision to make,
            // made by switching the channel, not something a background check should do.
            if (release.Prerelease != (effective == UpdateChannel.Beta)) { continue; }

            if (!SemVersion.TryParse(release.TagName, out var candidate)) { continue; }

            // Drafts and non-upgrades are not "newer", so keep walking back through history
            // rather than stopping at the first release the API returned.
            if (!current.IsOlderThan(candidate)) { continue; }

            var asset = SelectInstaller(release.Assets);
            if (asset is null)
            {
                return Fail($"Release {release.TagName} has no '{InstallerAssetSuffix}' asset.");
            }

            if (!string.IsNullOrWhiteSpace(skippedVersion) &&
                SemVersion.TryParse(skippedVersion, out var skipped) &&
                skipped.CompareTo(candidate) == 0)
            {
                // "Skip this version" applies to exactly this version, not to everything newer.
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.Skipped,
                    Update = BuildInfo(release, candidate, asset),
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                Update = BuildInfo(release, candidate, asset),
            };
        }

        return new UpdateCheckResult { Status = UpdateCheckStatus.UpToDate };
    }

    /// <summary>Auto follows the installed build: a beta install keeps getting betas.</summary>
    private static UpdateChannel ResolveChannel(UpdateChannel requested, SemVersion current)
    {
        if (requested != UpdateChannel.Auto) { return requested; }
        return current.IsPreRelease ? UpdateChannel.Beta : UpdateChannel.Stable;
    }

    private async Task<List<ReleaseDto>> FetchAsync(CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page={ReleasesToFetch}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Deliberately does not throw; the status is folded into a diagnostic.
            throw new HttpRequestException(
                $"GitHub API returned {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<ReleaseDto>>(json, s_json) ?? new List<ReleaseDto>();
    }

    private static ReleaseAssetDto? SelectInstaller(ReleaseAssetDto[]? assets)
    {
        if (assets is null || assets.Length == 0) { return null; }

        // Prefer the conventional x64 setup name published by release.yml, then fall back to
        // any .exe so a renamed asset does not silently disable updates.
        return assets.FirstOrDefault(a =>
                   a.Name?.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase) == true)
               ?? assets.FirstOrDefault(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static UpdateInfo BuildInfo(ReleaseDto release, SemVersion candidate, ReleaseAssetDto asset)
    {
        return new UpdateInfo
        {
            // candidate.ToString() drops the 'v' prefix and any '+sha', so the displayed and
            // compared versions are the same string the app would have written to disk.
            Version = candidate.ToString(),
            TagName = release.TagName ?? candidate.ToString(),
            DownloadUrl = asset.BrowserDownloadUrl ?? string.Empty,
            Sha256 = ParseDigest(asset.Digest),
            SizeBytes = asset.Size,
            IsPreRelease = release.Prerelease,
            PublishedAt = release.PublishedAt,
            ReleasePageUrl = release.HtmlUrl ?? string.Empty,
            ReleaseNotes = SummarizeNotes(release.Body),
        };
    }

    /// <summary>GitHub returns <c>sha256:&lt;hex&gt;</c>; store the bare lowercase hex.</summary>
    private static string ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) { return string.Empty; }
        var value = digest.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0) { value = value[(colon + 1)..]; }
        return value.Trim().ToLowerInvariant();
    }

    /// <summary>First few non-empty, non-badge lines of the generated notes — enough for a
    /// prompt, not a full changelog (the release page link carries the rest).</summary>
    private static string? SummarizeNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) { return null; }

        var lines = body
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .Where(l => !l.StartsWith("**", StringComparison.Ordinal))
            .Take(5)
            .ToArray();

        return lines.Length == 0 ? null : string.Join('\n', lines);
    }

    private static UpdateCheckResult Fail(string diagnostic) => new()
    {
        Status = UpdateCheckStatus.Unavailable,
        Diagnostic = diagnostic,
    };

    public void Dispose() => _http.Dispose();

    // ---- DTOs: only the fields actually read, so a GitHub schema addition cannot break us.
    private sealed class ReleaseDto
    {
        public string? TagName { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public string? HtmlUrl { get; set; }
        public string? Body { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public ReleaseAssetDto[]? Assets { get; set; }
    }

    private sealed class ReleaseAssetDto
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? BrowserDownloadUrl { get; set; }

        /// <summary>Present on current GitHub (e.g. <c>sha256:ab12…</c>). Null on older ones
        /// and on API responses from a private repo.</summary>
        public string? Digest { get; set; }
    }
}
