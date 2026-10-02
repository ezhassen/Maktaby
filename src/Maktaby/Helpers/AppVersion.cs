using System;
using System.Reflection;

namespace Maktaby.Helpers;

/// <summary>
/// The running build's version, read once and cached.
/// </summary>
/// <remarks>
/// MinVer writes <see cref="AssemblyInformationalVersionAttribute"/> as
/// <c>1.0.32-beta.4+9a3f1c2</c>; the <c>+sha</c> part is build metadata and is dropped so the
/// string matches what the update service compares against and what a tag would say.
/// This was previously inlined in <c>AboutView</c> — it lives here now so the About window
/// and the update checker cannot disagree about what version is running.
/// </remarks>
internal static class AppVersion
{
    private static string? _cached;

    /// <summary>Version without the 'v' prefix or '+sha', e.g. <c>1.0.32-beta.4</c>.</summary>
    public static string Current
    {
        get
        {
            if (_cached is not null) { return _cached; }

            string version;
            try
            {
                var informational = Assembly.GetEntryAssembly()?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;

                version = (informational ?? string.Empty).Split('+')[0].Trim();
            }
            catch
            {
                version = string.Empty;
            }

            // An untagged dev build has no comparable version. Reporting 0.0.0 keeps the
            // checker's parse-failure path exercised rather than pretending there is a version.
            _cached = string.IsNullOrEmpty(version) ? "0.0.0" : version;
            return _cached;
        }
    }

    /// <summary>True when this build looks like a pre-release, which drives the default
    /// "follow the installed version" channel (a beta install tracks betas).</summary>
    public static bool IsPreRelease
    {
        get
        {
            var v = Current;
            return v.Contains('-', StringComparison.Ordinal);
        }
    }
}
