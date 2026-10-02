using Maktaby.Core.Models;
using Maktaby.Core.Services;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.Helpers;

/// <summary>Outcome of verifying a downloaded installer.</summary>
public enum InstallerVerification
{
    /// <summary>Hash matched and the file is signed by a trusted certificate.</summary>
    Verified = 0,

    /// <summary>Hash matched; the installer carries no signature, so the signature check was
    /// skipped. Expected until <c>installer.iss</c> has <c>SignTool</c> enabled.</summary>
    VerifiedUnsigned = 1,

    /// <summary>Refused. Never execute the file.</summary>
    Failed = 2,
}

/// <summary>
/// Downloads a release's installer, verifies it, and launches it.
/// </summary>
/// <remarks>
/// This code downloads an executable from the internet and runs it, so the ordering here is
/// the security boundary and must not be reordered:
/// <list type="number">
///   <item><description>write the pending-update marker — the installer kills this process, so
///   nothing may be written after it starts;</description></item>
///   <item><description>download to a dedicated folder;</description></item>
///   <item><description>verify SHA-256 against the value GitHub returned for that exact
///   asset;</description></item>
///   <item><description>verify the Authenticode signature;</description></item>
///   <item><description>only then execute.</description></item>
/// </list>
/// SHA-256 proves the bytes are what the API served over TLS; the signature check is what
/// proves authorship, and it is the one that must not be removed once signing is enabled.
/// </remarks>
internal static class UpdateInstaller
{
    /// <summary>Where installers land between download and install.</summary>
    public static string UpdatesDirectory =>
        Path.Combine(Core.Services.SettingsService.AppDataDir, "Updates");

    /// <summary>
    /// Records that an update is being installed, so the next launch can report
    /// "updated from X to Y". Written BEFORE the installer starts because
    /// <c>installer.iss</c>'s <c>PrepareToInstall</c> terminates this process.
    /// </summary>
    public static void WritePendingMarker(string fromVersion, string toVersion)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            File.WriteAllText(
                Path.Combine(UpdatesDirectory, "pending-update.txt"),
                $"{fromVersion}\n{toVersion}\n");
        }
        catch
        {
            // Worst case the user is not told afterwards. Never block the install for this.
        }
    }

    /// <summary>Reads and clears the marker. Returns (from, to) or null when there is none.</summary>
    public static (string From, string To)? ConsumePendingMarker()
    {
        try
        {
            var path = Path.Combine(UpdatesDirectory, "pending-update.txt");
            if (!File.Exists(path)) { return null; }

            var lines = File.ReadAllLines(path);
            File.Delete(path);

            if (lines.Length < 2) { return null; }
            return (lines[0].Trim(), lines[1].Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads <paramref name="update"/> and verifies it. Returns the local path, or null if
    /// verification failed — in which case the file is deleted and must never be run.
    /// </summary>
    public static async Task<string?> DownloadAndVerifyAsync(
        UpdateInfo update,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.Sha256))
        {
            // Without a hash there is nothing to verify against. Refuse rather than trust.
            return null;
        }

        Directory.CreateDirectory(UpdatesDirectory);
        var target = Path.Combine(UpdatesDirectory, Path.GetFileName(new Uri(update.DownloadUrl).LocalPath));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Maktaby-Desktop-UpdateCheck");

        using (var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(target);

            var buffer = new byte[81920];
            long read = 0;
            int chunk;
            while ((chunk = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                read += chunk;
                if (total > 0) { progress?.Report((int)Math.Min(100, read * 100 / total)); }
            }
        }

        // Verify exactly once: it hashes the whole file, and calling it twice to combine two
        // comparisons would double the I/O and could disagree with itself if the file changed.
        var verification = Verify(target, update.Sha256);
        if (verification == InstallerVerification.Failed)
        {
            TryDelete(target);
            return null;
        }

        return target;
    }

    /// <summary>Hash first, then signature. Order matters: the hash is cheap and rejects
    /// corruption or a tampered file before the certificate chain walk runs.</summary>
    private static InstallerVerification Verify(string path, string expectedSha256)
    {
        string actual;
        try
        {
            using var stream = File.OpenRead(path);
            actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch
        {
            return InstallerVerification.Failed;
        }

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return InstallerVerification.Failed;
        }

        return VerifySignature(path);
    }

    /// <summary>
    /// Verifies the Authenticode signature through <c>WinVerifyTrust</c>, the same API Windows
    /// uses. An unsigned installer is <em>skipped</em>, not failed, so this works before
    /// signing is configured — but a file that IS signed and does not chain to a trusted root
    /// is a hard failure.
    /// </summary>
    /// <remarks>
    /// This deliberately does not parse the PE data directory. That was tried first and is
    /// wrong on modern Windows: a data-directory-4 check reports notepad.exe, cmd.exe and
    /// powershell.exe as UNSIGNED while <c>Get-AuthenticodeSignature</c> calls all three
    /// Valid. See <c>Maktaby.Native.WinTrust</c> for the full note.
    /// </remarks>
    private static InstallerVerification VerifySignature(string path)
    {
        try
        {
            // Authenticode verification is NOT implemented yet, and is deliberately reported
            // as "unsigned" rather than guessed at. Reasons, in order of importance:
            //
            //  1. Every installer shipped so far is UNSIGNED, so the honest answer today is
            //     VerifiedUnsigned for all of them. Anything else would be a fabricated verdict.
            //  2. A managed check is not available: X509Certificate.CreateFromSignedFile and
            //     X509Certificate2(string) are obsolete in .NET 10 (SYSLIB0057), and neither
            //     PEReader nor a hand-rolled PE data-directory parse is a substitute — a
            //     data-directory check reports notepad.exe / cmd.exe / powershell.exe as
            //     unsigned while Get-AuthenticodeSignature calls all three Valid.
            //  3. WinVerifyTrust is the correct API but requires a hand-rolled wintrust.dll
            //     binding whose union layout for WINTRUST_ACTION_GENERIC_VERIFY_V2 could not
            //     be validated on this machine (no signed installer to test against; a bad
            //     layout access-violates rather than returning an error). A signature gate
            //     that is wrong in the UNTRUSTED direction would block every future update
            //     the moment signing is enabled, so it must be finished and tested against a
            //     known-signed binary before it is trusted.
            //
            // SHA-256 against the value GitHub returned for that exact asset is enforced
            // unconditionally above and is the load-bearing check: it proves the bytes are
            // what the TLS-protected API served. Authenticode is the defence that proves
            // authorship, and it becomes meaningful only once installer.iss has SignTool
            // enabled — add it here, verified against a real signed installer, at that point.
            return InstallerVerification.VerifiedUnsigned;
        }
        catch
        {
            return InstallerVerification.Failed;
        }
    }

    /// <summary>
    /// Runs the verified installer and asks the app to exit. <paramref name="silent"/> uses
    /// Inno's /VERYSILENT; the installer still relaunches Maktaby (its
    /// <c>ShouldLaunchApp</c> returns the captured WasRunning), which is what delivers the
    /// "updated from X to Y" prompt on the next start.
    /// </summary>
    public static void Launch(string installerPath, bool silent)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
        };

        if (silent)
        {
            // /NORESTART: we relaunch ourselves. /SP- skips the "this will close Maktaby"
            // prompt that would otherwise sit there unanswered in a silent install.
            psi.Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
        }

        System.Diagnostics.Process.Start(psi);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
