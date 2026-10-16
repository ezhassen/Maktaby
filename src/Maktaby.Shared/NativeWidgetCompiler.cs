using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.IO;
using System.Text;
using Maktaby.WidgetSdk;

namespace Maktaby.Shared;

/// <summary>
/// The ONE implementation of "compile a native widget's sources into a plugin DLL".
/// </summary>
/// <remarks>
/// <para>
/// Two hosts call this and they must not diverge:
/// </para>
/// <list type="bullet">
///   <item><description><b>Runtime</b> (<c>NativeWidgetService</c>) compiles user widgets on
///   demand, into a content-hash cache directory.</description></item>
///   <item><description><b>Build time</b> (<c>Maktaby.WidgetPrebuild</c>) compiles the
///   app-provided widgets ahead of time, next to their sources, so the first launch does not
///   pay Roslyn and a broken widget fails the build instead of the user's desktop.</description></item>
/// </list>
/// <para>
/// That is why this lives in Shared instead of being retyped in the tool. The compile is
/// sensitive to details that look incidental and are not: the exact implicit-usings list, the
/// reference set, and the XAML resources. A tool with its own copy compiles something subtly
/// different from what the host would have produced - and the difference only shows up as an
/// <c>MissingMethodException</c> in a widget, days later.
/// </para>
/// </remarks>
public static class NativeWidgetCompiler
{
    /// <summary>Name the runtime expects inside a cache directory.</summary>
    public const string PluginFileName = "plugin.dll";

    /// <summary>Name the prebuild tool writes next to a widget's sources.</summary>
    public const string PrebuiltAssemblyFileName = "plugin.dll";

    /// <summary>
    /// Global usings injected ahead of every plugin source, so widget code reads like a normal
    /// project. Changing this changes the compiled IL for every widget: it is part of the
    /// contract, not a convenience.
    /// </summary>
    /// <remarks>
    /// It injects <c>System.IO</c> AND <c>System.Windows.Shapes</c>, so a widget that uses
    /// <c>Path</c> must disambiguate with <c>using Path = System.Windows.Shapes.Path;</c>.
    /// Do not "tidy" the list without recompiling every widget.
    /// </remarks>
    public const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Text;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        global using global::System.Windows;
        global using global::System.Windows.Controls;
        global using global::System.Windows.Media;
        global using global::System.Windows.Shapes;
        global using global::System.Windows.Threading;
        """;

    /// <summary>One widget's compile request.</summary>
    /// <param name="Slug">Folder identity; also the assembly name.</param>
    /// <param name="FolderPath">Widget folder. Sources, XAML and third-party DLLs are read from here.</param>
    /// <param name="SourceFiles">The .cs files, in any order - they are sorted here.</param>
    /// <param name="OutputDllPath">Where to emit. The parent directory is created if needed.</param>
    public sealed record Request(
        string Slug,
        string FolderPath,
        IReadOnlyList<string> SourceFiles,
        string OutputDllPath);

    /// <summary>
    /// Compiles the request and emits the DLL plus a PDB. Returns the compiler errors, or an
    /// empty list on success. Never throws for a compilation failure - the caller decides how
    /// loud that is (the runtime throws its load exception, the tool prints and exits non-zero).
    /// </summary>
    public static IReadOnlyList<string> Compile(Request request)
    {
        var sources = request.SourceFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0)
        {
            return new[] { $"Widget '{request.Slug}' has no .cs sources to compile." };
        }

        string? dir = System.IO.Path.GetDirectoryName(request.OutputDllPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        // Attach the encoding explicitly: string-parsed trees default to none and PDB emit
        // fails on them (CS8055).
        var trees = new List<SyntaxTree>
        {
            // SDK-style implicit usings, so plugin sources read like a normal project.
            CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(ImplicitUsings, Encoding.UTF8),
                parseOptions, path: "<implicit-usings>"),
        };
        trees.AddRange(sources.Select(f => CSharpSyntaxTree.ParseText(
            Microsoft.CodeAnalysis.Text.SourceText.From(File.ReadAllText(f, Encoding.UTF8), Encoding.UTF8),
            parseOptions, path: f)));

        var references = BuildReferences(request.FolderPath, request.OutputDllPath);
        references.Add(MetadataReference.CreateFromFile(typeof(INativeWidget).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            $"NativeWidget_{request.Slug}",
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));

        // XAML is embedded rather than compiled, and read lazily from disk so the file handle is
        // only open while emitting.
        var xamls = Directory.EnumerateFiles(request.FolderPath, "*.xaml", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        var resources = xamls.Select(f => new ResourceDescription(
            $"{request.Slug}/{System.IO.Path.GetFileName(f)}",
            () => File.OpenRead(f),
            isPublic: true)).ToList();

        string pdbPath = System.IO.Path.ChangeExtension(request.OutputDllPath, ".pdb");
        EmitResult emit;
        try
        {
            using var dllStream = File.Create(request.OutputDllPath);
            using var pdbStream = File.Create(pdbPath);
            emit = compilation.Emit(dllStream, pdbStream, manifestResources: resources);
        }
        catch (Exception ex)
        {
            TryDelete(request.OutputDllPath);
            TryDelete(pdbPath);
            return new[] { $"Emit failed: {ex.Message}" };
        }

        if (emit.Success) return Array.Empty<string>();

        // A half-written plugin.dll is worse than none: the next load would find it, hash it
        // against a value the cache key never saw, and fail for the wrong reason.
        TryDelete(request.OutputDllPath);
        TryDelete(pdbPath);
        return emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(5)
            .Select(d => d.ToString())
            .ToList();
    }

    /// <summary>
    /// References a plugin gets: the running host's platform assemblies (WPF + BCL), plus any
    /// third-party DLLs shipped in the widget folder.
    /// </summary>
    /// <param name="pluginFolder">Widget folder to scan for third-party DLLs.</param>
    /// <param name="outputDllPath">
    /// The DLL being produced, skipped during the scan. Without this the prebuilder references
    /// the previous run's output: plugin.dll sits in the widget folder, so on the SECOND run it
    /// would be handed to Roslyn as an input while also being the output - a self-reference that
    /// compiles against last build's IL instead of the sources in front of us.
    /// </param>
    /// <remarks>
    /// This reads <c>TRUSTED_PLATFORM_ASSEMBLIES</c> of the CURRENT process, so it always
    /// matches the host that is actually running - the app, or the prebuild tool. Both must be
    /// WPF assemblies with a reference to Maktaby.WidgetSdk for that to hold.
    /// </remarks>
    public static List<MetadataReference> BuildReferences(string pluginFolder, string? outputDllPath = null)
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        var list = new List<MetadataReference>(256);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in tpa.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var name = System.IO.Path.GetFileName(path);
                // WPF (incl. WinForms interop) + BCL surface. Everything else (WidgetSdk,
                // plugin deps) resolves through the plugin load context at runtime.
                if (name.StartsWith("System.", StringComparison.Ordinal) ||
                    name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                    string.Equals(name, "PresentationFramework.dll", StringComparison.Ordinal) ||
                    string.Equals(name, "PresentationCore.dll", StringComparison.Ordinal) ||
                    string.Equals(name, "WindowsBase.dll", StringComparison.Ordinal) ||
                    string.Equals(name, "WindowsFormsIntegration.dll", StringComparison.Ordinal) ||
                    string.Equals(name, "System.Xaml.dll", StringComparison.Ordinal) ||
                    string.Equals(name, "netstandard.dll", StringComparison.Ordinal) ||
                    string.Equals(name, "mscorlib.dll", StringComparison.Ordinal))
                {
                    list.Add(MetadataReference.CreateFromFile(path));
                    names.Add(System.IO.Path.GetFileNameWithoutExtension(name));
                }
            }
            catch { }
        }

        // Third-party DLLs shipped next to the sources (SkiaSharp, Vortice, ...) so source
        // plugins can reference them. Host-owned assemblies are skipped to force type
        // unification with the running host; unloadable (native) files are skipped quietly.
        try
        {
            foreach (var path in Directory.EnumerateFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var simple = System.IO.Path.GetFileNameWithoutExtension(path);
                    // The DLL this compile is about to write is not one of its inputs.
                    if (!string.IsNullOrEmpty(outputDllPath) &&
                        string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(outputDllPath),
                                      StringComparison.OrdinalIgnoreCase)) continue;
                    if (simple.StartsWith("Maktaby.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!names.Add(simple)) continue;
                    list.Add(MetadataReference.CreateFromFile(path));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}