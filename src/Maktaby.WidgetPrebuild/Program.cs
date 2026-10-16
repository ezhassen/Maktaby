using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maktaby.Shared;

namespace Maktaby.WidgetPrebuild;

/// <summary>
/// Compiles every app-provided native widget ahead of time, into a cached intermediate
/// directory. Invoked by <c>Maktaby.csproj</c> as a build step, so a widget that no longer
/// compiles fails the ordinary build instead of the user's desktop.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: without it the first time someone opens the gallery or drops a widget the
/// app runs Roslyn on its sources, and the first compile in a process also loads the compiler
/// itself. The cost belongs in the build.
/// </para>
/// <para>
/// Output goes to <c>obj/nativewidgets/&lt;rid&gt;/</c>, NOT next to the sources: those
/// intermediates are per-runtime and disposable, and writing build output into the source tree
/// is what makes "clean" ambiguous. <c>Maktaby.csproj</c> copies the result into its own output
/// and publish directory afterwards, which is where the app finds it at runtime.
/// </para>
/// <para>
/// Builds are incremental per widget. The stamp is a content hash of the widget's sources and
/// XAML plus this tool's own ModuleVersionId - never an assembly version, for the same reason
/// the runtime cache keys on the SDK's MVID: a version does not move for an uncommitted change,
/// so it would serve a stale build.
/// </para>
/// </remarks>
internal static class Program
{
    private const string ManifestFileName = "nwidget.json";
    private const string AssemblyKey = "assembly";
    private const string StampFileName = "build.stamp";
    private const int MaxReportedErrors = 5;

    private static int Main(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            // An empty output path used to arrive here as a crash deep in Path.GetFullPath,
            // which the MSBuild caller could only report as an unhandled-exception exit code.
            Console.Error.WriteLine("usage: Maktaby.WidgetPrebuild <sourceRoot> <outputRoot> [--force] [--contract <dir>]...");
            Console.Error.WriteLine("  (both arguments are required; got " + args.Length + ")");
            Console.Error.WriteLine("  --contract <dir>  repeatable; project whose sources define the compile");
            Console.Error.WriteLine("                    contract. Omit it and the tool's ModuleVersionId is used");
            Console.Error.WriteLine("                    instead (correct, but every commit invalidates the cache).");
            return 2;
        }

        string sourceRoot = Path.GetFullPath(args[0]);
        string outputRoot = Path.GetFullPath(args[1]);
        bool force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);

        // The projects that define HOW a widget is compiled: this tool and the shared compiler it
        // delegates to. Their sources are hashed into the stamp instead of this tool's MVID.
        var contractRoots = ContractRoots(args);

        if (!Directory.Exists(sourceRoot))
        {
            Console.Error.WriteLine($"Native widget root not found: {sourceRoot}");
            return 2;
        }

        Directory.CreateDirectory(outputRoot);

        Console.WriteLine($"Native widget prebuild");
        Console.WriteLine($"  source : {sourceRoot}");
        Console.WriteLine($"  output : {outputRoot}");
        Console.WriteLine(contractRoots.Count == 0
            ? "  stamp  : tool ModuleVersionId (no --contract given)"
            : $"  stamp  : sources of {contractRoots.Count} contract project(s)");

        int built = 0, cached = 0, skipped = 0, failed = 0;

        foreach (var folder in Directory.EnumerateDirectories(sourceRoot)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(folder, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;

            var sources = Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly).ToList();
            if (sources.Count == 0)
            {
                Console.WriteLine($"  skip   {Path.GetFileName(folder)}: no sources");
                skipped++;
                continue;
            }

            var slug = Path.GetFileName(folder);
            var outDir = Path.Combine(outputRoot, slug);
            var output = Path.Combine(outDir, NativeWidgetCompiler.PrebuiltAssemblyFileName);
            var stampPath = Path.Combine(outDir, StampFileName);
            var stamp = ComputeStamp(folder, sources, contractRoots);

            if (!force && File.Exists(output) && File.Exists(stampPath) && ReadAll(stampPath) == stamp)
            {
                Console.WriteLine($"  cached {slug,-20} ({sources.Count} sources unchanged)");
                cached++;
                StampAssembly(manifestPath);
                continue;
            }

            var errors = NativeWidgetCompiler.Compile(
                new NativeWidgetCompiler.Request(slug, folder, sources, output));

            if (errors.Count > 0)
            {
                // A compile error here is a BUILD failure on purpose - that is the entire point
                // of running at build time. Name the widget and print the diagnostics verbatim.
                Console.Error.WriteLine($"  FAIL   {slug}");
                foreach (var e in errors.Take(MaxReportedErrors))
                {
                    Console.Error.WriteLine($"           {e}");
                }
                failed++;
                continue;
            }

            File.WriteAllText(stampPath, stamp);
            StampAssembly(manifestPath);
            Console.WriteLine($"  build  {slug,-20} ({sources.Count} sources, {new FileInfo(output).Length / 1024.0:F0} KB)");
            built++;
        }

        Console.WriteLine($"{built} built, {cached} up to date, {skipped} skipped, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reads the repeatable <c>--contract &lt;dir&gt;</c> options: the projects whose sources
    /// decide how a widget compiles.
    /// </summary>
    private static List<string> ContractRoots(string[] args)
    {
        var roots = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--contract", StringComparison.OrdinalIgnoreCase)) continue;
            string dir = args[i + 1];
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (Directory.Exists(dir)) roots.Add(Path.GetFullPath(dir));
            else Console.Error.WriteLine($"WARN   --contract directory not found, ignored: {dir}");
        }
        return roots;
    }

    /// <summary>
    /// Identity of a widget's compiled output: its sources and XAML, plus the compile CONTRACT.
    /// </summary>
    /// <remarks>
    /// The contract is the sources of this tool and of the shared compiler it delegates to. It was
    /// originally this tool's own <c>ModuleVersionId</c>, which looked right and was quietly
    /// expensive: MinVer stamps <c>AssemblyInformationalVersion</c> with the commit sha
    /// (<c>91.0.36-alpha.0.1+ae9c041</c>), and the MVID is a hash of that metadata - so every
    /// commit changed the MVID, every widget's stamp, and forced a rebuild of ALL widgets even
    /// when one source file changed. Hashing the contract's <em>content</em> keeps the guard that
    /// matters (a compiler change must invalidate the prebuilts) while moving only when the
    /// compiler actually changes.
    /// <para>
    /// The MVID is still used when no <c>--contract</c> is supplied, so a manual invocation stays
    /// correct - just less selective.
    /// </para>
    /// <para>
    /// <c>*.cs</c> and <c>*.csproj</c>: the project files are inputs too, because a package added
    /// to either project changes the reference set <c>BuildReferences</c> hands the compiler.
    /// </para>
    /// </remarks>
    private static string ComputeStamp(string folder, IReadOnlyList<string> sources, IReadOnlyList<string> contractRoots)
    {
        using var sha = SHA256.Create();
        void Feed(byte[] bytes) => sha.TransformBlock(bytes, 0, bytes.Length, null, 0);

        if (contractRoots.Count == 0)
        {
            Feed(Encoding.UTF8.GetBytes(typeof(Program).Module.ModuleVersionId.ToString()));
        }
        else
        {
            foreach (var root in contractRoots)
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                                           .Where(IsContractFile)
                                           .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    Feed(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0"));
                    Feed(File.ReadAllBytes(file));
                    Feed([0]);
                }
            }
        }
        Feed([0]);

        foreach (var file in sources.Concat(
                     Directory.EnumerateFiles(folder, "*.xaml", SearchOption.TopDirectoryOnly))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            Feed(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0"));
            Feed(File.ReadAllBytes(file));
            Feed([0]);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    /// <summary>
    /// Contract inputs, skipping build output and anything under bin/obj - otherwise the hash
    /// would change every time the tool itself is compiled.
    /// </summary>
    private static bool IsContractFile(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return false;

        var segments = path.Replace('/', '\\').Split('\\');
        return !segments.Any(s =>
            s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadAll(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return ""; }
    }

    /// <summary>
    /// Writes <c>"assembly"</c> into the manifest so the runtime prefers the prebuilt DLL.
    /// </summary>
    /// <remarks>
    /// Without this the key is pointless: <c>ResolveAssembly</c> only takes the explicit-assembly
    /// branch when the manifest names one, and would otherwise see the .cs files sitting next to
    /// the DLL and compile from source anyway, leaving the prebuilt binary unused. Idempotent - it
    /// only rewrites the file when the value actually differs, so it does not churn the tree.
    /// </remarks>
    private static void StampAssembly(string manifestPath)
    {
        string fileName = NativeWidgetCompiler.PrebuiltAssemblyFileName;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            if (root[AssemblyKey]?.GetValue<string>() == fileName) return;

            root[AssemblyKey] = fileName;
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(manifestPath, root.ToJsonString(options) + Environment.NewLine);
            Console.WriteLine($"         manifest: \"{AssemblyKey}\" = \"{fileName}\" (added)");
        }
        catch (Exception ex)
        {
            // Not fatal to the compile, but the widget would then be loaded from source, so it
            // is worth saying out loud.
            Console.Error.WriteLine($"  WARN   {Path.GetFileName(Path.GetDirectoryName(manifestPath))}: " +
                                   $"could not stamp \"{AssemblyKey}\": {ex.Message}");
        }
    }
}