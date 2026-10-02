using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Maktaby.Helpers;

/// <summary>
/// Whether this copy of Maktaby is a real, installer-managed installation — which decides
/// whether the in-app updater is available at all.
/// </summary>
/// <remarks>
/// <para>
/// Two independent signals, and the portable answer wins if either says so:
/// </para>
/// <list type="number">
///   <item><description><b>The Inno Setup uninstaller.</b> <c>installer.iss</c> declares a
///   fixed <c>AppId</c>, so a setup run always leaves <c>unins000.exe</c> and
///   <c>unins000.dat</c> in <c>{app}</c>. Their presence proves that <i>this directory</i> is
///   installer-managed. The <c>.dat</c> is additionally content-checked for the
///   "Inno Setup Uninstall Log" header, so an unrelated file that merely happens to be named
///   <c>unins*.dat</c> cannot masquerade as an installation. Both are globbed rather than
///   hard-coded to <c>unins000</c>, because Inno increments the number when a directory is
///   shared with a second installation.</description></item>
///   <item><description><b>The build flag.</b> <c>-p:PortableBuild=true</c> stamps
///   <c>PortableBuild</c> assembly metadata. This is the only signal that survives the case
///   the uninstaller check gets wrong: a user zipping the installed folder onto a USB stick
///   carries the <c>unins*</c> files with it, and only the build flag still says
///   "portable".</description></item>
/// </list>
/// <para>
/// The uninstaller check also covers a case the build flag cannot see: <b>a developer build
/// run from the repository</b> has no uninstaller in its folder, so it must not be offered a
/// release — the installer would kill the running dev process and install a second copy
/// beside it.
/// </para>
/// <para>
/// Runtime <i>location</i> is deliberately not consulted. "Am I under Program Files"
/// misjudges a per-user install and an installed copy the user moved to another drive, both
/// of which are perfectly updatable.
/// </para>
/// </remarks>
internal static class AppBuildInfo
{
    /// <summary>Header of an Inno Setup uninstall log. Verified against the real install this
    /// was written for: "Inno Setup Uninstall Log (b) 64-bit{GUID}".</summary>
    private const string InnoLogMagic = "Inno Setup Uninstall Log";

    private static bool? _isPortable;

    public static bool IsPortable
    {
        get
        {
            _isPortable ??= GlobalFeaturesSwitches.SimulateUpdateAvailable ? false : HasPortableBuildFlag() || !HasInstallerMarker();
            return _isPortable.Value;
        }
    }

    /// <summary>True when this build was produced with <c>-p:PortableBuild=true</c>.</summary>
    public static bool HasPortableBuildFlag()
    {
        try
        {
            var attribute = Assembly.GetEntryAssembly()?
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, "PortableBuild", StringComparison.OrdinalIgnoreCase));

            return attribute is not null
                && bool.TryParse(attribute.Value, out var value)
                && value;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when an Inno Setup uninstaller for this app lives in the running executable's
    /// directory, proving the copy is installer-managed.
    /// </summary>
    public static bool HasInstallerMarker()
    {
        try
        {
            var directory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            // The uninstaller binary is proof on its own.
            if (Directory.EnumerateFiles(directory, "unins*.exe").Any())
            {
                return true;
            }

            // Otherwise verify the log's content, so an unrelated file named unins*.dat is
            // not mistaken for an installation. Only the header is read.
            foreach (var log in Directory.EnumerateFiles(directory, "unins*.dat"))
            {
                try
                {
                    using var stream = File.OpenRead(log);
                    var header = new byte[InnoLogMagic.Length];
                    if (stream.Read(header, 0, header.Length) == header.Length
                        && System.Text.Encoding.ASCII.GetString(header)
                            .Equals(InnoLogMagic, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Unreadable log: fall through and try the next candidate.
                }
            }

            return false;
        }
        catch
        {
            // Cannot inspect the directory. Assume installed rather than silently disabling
            // the updater for a genuine installation.
            return true;
        }
    }
}
