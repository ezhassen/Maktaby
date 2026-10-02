using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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

    /// <summary>Asset naming convention the release workflow publishes:
    /// <c>Maktaby-&lt;version&gt;-&lt;arch&gt;-setup.exe</c>. The architecture token is NOT
    /// hard-coded - it is matched against the running process, so adding an arm64 or x86
    /// release to the same tag needs no change here.</summary>
    private const string InstallerAssetSuffix = "-setup.exe";

    /// <summary>Architecture token in a release asset name -> the process architecture it
    /// serves. Extended (not replaced) as new runtimes ship: an unknown token simply matches
    /// nothing, and the check degrades to "no installer for this machine" rather than
    /// offering the wrong build.</summary>
    private static readonly IReadOnlyDictionary<string, Architecture> s_archTokens =
        new Dictionary<string, Architecture>(StringComparer.OrdinalIgnoreCase)
        {
            ["x64"] = Architecture.X64,
            ["amd64"] = Architecture.X64,
            ["x86"] = Architecture.X86,
            ["win32"] = Architecture.X86,
            ["arm64"] = Architecture.Arm64,
        };

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

    /// <summary>
    /// Picks the installer for the architecture this process is running on.
    /// </summary>
    /// <remarks>
    /// Ordered by decreasing confidence, because picking the wrong one is worse than picking
    /// none - a user would be offered an arm64 setup on an x64 machine and it would fail
    /// mid-install:
    /// <list type="number">
    ///   <item><description>the exact <c>-&lt;arch&gt;-setup.exe</c> the workflow publishes;</description></item>
    ///   <item><description>any other arch-tagged <c>.exe</c> for this architecture (covers the
    ///   older <c>Maktaby.1.0.32-beta-x64.exe</c> naming);</description></item>
    ///   <item><description>a single un-tagged <c>.exe</c>, only when it is unambiguously the
    ///   only one;</description></item>
    ///   <item><description>nothing - several candidates, or none for this architecture.</description></item>
    /// </list>
    /// </remarks>
    private static ReleaseAssetDto? SelectInstaller(ReleaseAssetDto[]? assets)
    {
        if (assets is null || assets.Length == 0) { return null; }

        var running = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

        // 1. The conventional name for this architecture.
        var exact = assets.FirstOrDefault(a =>
            a.Name is not null
            && a.Name.EndsWith($"{ArchitectureToken(running)}{InstallerAssetSuffix}", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) { return exact; }

        var executables = assets
            .Where(a => !string.IsNullOrEmpty(a.Name) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // 2. Any arch-tagged executable that matches the running process.
        var tagged = executables.FirstOrDefault(a => TokenFor(a.Name!) == running);
        if (tagged is not null) { return tagged; }

        // 3. A lone untagged executable is safe only when there is exactly one; two or more
        //    would be a guess between architectures.
        var untagged = executables.Where(a => TokenFor(a.Name!) is null).ToArray();
        if (untagged.Length == 1) { return untagged[0]; }

        return null;
    }

    /// <summary>The architecture token for the running process, e.g. <c>x64</c>.</summary>
    private static string ArchitectureToken(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Reads the architecture out of an asset name, or null when it carries none.
    /// Only tokens that actually separate words count, so a version like "1.0.3" is not
    /// mistaken for an architecture.
    /// </summary>
    private static Architecture? TokenFor(string assetName)
    {
        var stem = Path.GetFileNameWithoutExtension(assetName);
        if (string.IsNullOrEmpty(stem)) { return null; }

        foreach (var (token, architecture) in s_archTokens)
        {
            // Require a separator before the token, so "x64" matches in "beta-x64" and
            // "-x64-setup" but never inside a longer word.
            if (stem.Contains($"-{token}", StringComparison.OrdinalIgnoreCase)
                || stem.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return architecture;
            }
        }

        return null;
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
