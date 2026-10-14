using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Maktaby.Core.Services;

/// <summary>Which of the two published installer flavors a build is. They differ in exactly
/// one way - whether the .NET Desktop Runtime has to be installed first - and that one
/// difference is the whole reason this type exists.</summary>
public enum InstallFlavor
{
    /// <summary>Framework-dependent publish (the default). Small download, needs the
    /// matching .NET Desktop Runtime installed. Release asset name ends
    /// <c>-setup.exe</c>.</summary>
    Lean = 0,

    /// <summary>Self-contained publish. Large download, no prerequisite at all. Release
    /// asset name ends <c>-setup-full.exe</c>.</summary>
    Full = 1,
}

/// <summary>
/// Knows which flavor the running copy is, and owns the asset-name spelling that goes with
/// it. This is the single place that mapping exists: the release workflow, the website and
/// <see cref="UpdateService"/> all have to agree on it, and three hand-written copies of the
/// suffix is exactly how they would stop agreeing.
/// </summary>
/// <remarks>
/// The distinction has to be detected rather than configured, because nothing in the user's
/// settings records how the app was installed - the same folder can be dropped on a machine or
/// put there by either setup.
/// </remarks>
public static class InstallFlavorInfo
{
    /// <summary>Asset-name suffix of a framework-dependent installer.</summary>
    public const string LeanAssetSuffix = "-setup.exe";

    /// <summary>Asset-name suffix of a self-contained installer. Note it does NOT end with
    /// <see cref="LeanAssetSuffix"/>, so the two can never collide in an <c>EndsWith</c>
    /// test.</summary>
    public const string FullAssetSuffix = "-setup-full.exe";

    /// <summary>Flavor token used in file and folder names (publish dir, build matrix).</summary>
    public const string LeanToken = "lean";

    public const string FullToken = "full";

    // Static field initializer, not Lazy<T>: this reads a file on the slow path only, and a
    // Lazy would allocate to save work that happens at most twice per process.
    private static readonly InstallFlavor s_current = Detect();

    /// <summary>The flavor of the running copy.</summary>
    public static InstallFlavor Current => s_current;

    /// <summary>True when this copy carries its own runtime and needs no installed .NET.</summary>
    public static bool IsSelfContained => s_current == InstallFlavor.Full;

    /// <summary>The release asset suffix for a flavor.</summary>
    public static string AssetSuffix(InstallFlavor flavor) =>
        flavor == InstallFlavor.Full ? FullAssetSuffix : LeanAssetSuffix;

    /// <summary>The token used in build/publish names for a flavor.</summary>
    public static string Token(InstallFlavor flavor) =>
        flavor == InstallFlavor.Full ? FullToken : LeanToken;

    /// <summary>
    /// The flavor a release asset name carries, or null when it carries none - which is the
    /// case for assets published before this convention existed and for un-flavored names in
    /// general.
    /// </summary>
    public static InstallFlavor? FlavorFromAssetName(string? assetName)
    {
        if (string.IsNullOrEmpty(assetName)) { return null; }

        if (assetName.EndsWith(FullAssetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return InstallFlavor.Full;
        }

        if (assetName.EndsWith(LeanAssetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return InstallFlavor.Lean;
        }

        return null;
    }

    /// <summary>
    /// Reads the flavor out of how this process was published.
    /// </summary>
    /// <remarks>
    /// Two independent probes, because neither is sufficient alone:
    /// <list type="number">
    ///   <item><description><c>RuntimeInformation.FrameworkDescription</c> cannot be used:
    ///   both flavors report ".NET 10.0.x", so it says nothing about where the runtime came
    ///   from.</description></item>
    ///   <item><description>Where <c>System.Private.CoreLib</c> was loaded from. A
    ///   self-contained publish carries it in the app folder; a framework-dependent one
    ///   loads it from the shared runtime under <c>dotnet\shared\</c>. No I/O, but empty
    ///   when the assembly has no file location (single-file publish, some hosts).</description></item>
    ///   <item><description>The <c>.runtimeconfig.json</c> next to the exe, which is what the
    ///   host itself uses: self-contained writes <c>includedFrameworks</c>, framework-dependent
    ///   writes <c>framework</c>. This is the fallback, and it only costs one small file read
    ///   in the case where it is actually needed.</description></item>
    /// </list>
    /// </remarks>
    private static InstallFlavor Detect()
    {
        var probe = CoreLibBesideApp();
        if (probe is not null) { return probe.Value; }

        return FromRuntimeConfig() ?? InstallFlavor.Lean;
    }

    /// <summary>Null when the answer is not knowable from loaded assembly locations.</summary>
    private static InstallFlavor? CoreLibBesideApp()
    {
        string location;
        try
        {
            location = typeof(object).Assembly.Location;
        }
        catch
        {
            return null;
        }

        // No location at all (single-file, or an in-memory host): defer to the file probe.
        if (string.IsNullOrEmpty(location)) { return null; }

        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory)) { return null; }

        // AppContext.BaseDirectory always ends with a separator, so a plain prefix test is
        // enough - and it is case-insensitive because Windows paths are.
        return location.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase)
            ? InstallFlavor.Full
            : InstallFlavor.Lean;
    }

    private static InstallFlavor? FromRuntimeConfig()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly();
            if (entry is null) { return null; }

            var path = Path.Combine(AppContext.BaseDirectory, entry.GetName().Name + ".runtimeconfig.json");
            if (!File.Exists(path)) { return null; }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
            {
                return null;
            }

            // Self-contained publishes the bundled framework under "includedFrameworks";
            // framework-dependent names the one it expects under "framework". They are
            // mutually exclusive, which is what makes this a reliable signal.
            var bundlesRuntime = runtimeOptions.TryGetProperty("includedFrameworks", out _);
            var expectsRuntime = runtimeOptions.TryGetProperty("framework", out _);

            if (bundlesRuntime && !expectsRuntime) { return InstallFlavor.Full; }
            if (expectsRuntime && !bundlesRuntime) { return InstallFlavor.Lean; }

            // Neither or both: a configuration this app never publishes. Let the caller
            // fall back to its own default rather than guess.
            return null;
        }
        catch
        {
            // A missing or unreadable runtimeconfig is not worth failing an app launch over:
            // the worst case is offering the other flavor of installer, which still installs.
            return null;
        }
    }
}