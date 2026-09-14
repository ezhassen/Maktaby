using DesktopBoxes.WidgetSdk;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace DesktopBoxesUI.Core.Services;

/// <summary>Discovery + instantiation for native (DLL / source-compiled) widgets living in
/// the shared UserWidgets root. Folder identity is the slug; <c>nwidget.json</c> presence
/// decides ownership (see <see cref="WidgetFolder"/>) so web folders are never touched
/// here. Heavy work (Roslyn compile, assembly load) happens off the UI thread — callers
/// must offload it; instantiation and thumbnail rendering require the UI thread.</summary>
public sealed class NativeWidgetService : INativeWidgetService
{
    private readonly ISettingsService _settings;

    public NativeWidgetService(ISettingsService settings)
    {
        _settings = settings;
    }
    public string UserWidgetsRoot => Path.Combine(SettingsService.AppDataDir, "UserWidgets");
    public string NativeCacheRoot => Path.Combine(SettingsService.AppDataDir, "NativeCache");

    public string AppWidgetsRoot
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "NativeWidgets");
            if (Directory.Exists(candidate)) return candidate;
            try
            {
                var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "DesktopBoxesUI", "NativeWidgets"));
                if (Directory.Exists(dev)) return dev;
                var dev2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "NativeWidgets"));
                if (Directory.Exists(dev2)) return dev2;
            }
            catch { }
            return candidate;
        }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _trust;

    public IReadOnlyList<NativeWidgetInfo> GetAvailableWidgets()
    {
        var list = new List<NativeWidgetInfo>();
        list.AddRange(GetAppWidgets());
        list.AddRange(GetUserWidgets());
        return list;
    }

    public IReadOnlyList<NativeWidgetInfo> GetAppWidgets() => EnumerateWidgets(AppWidgetsRoot, NativeWidgetSource.App);
    public IReadOnlyList<NativeWidgetInfo> GetUserWidgets() => EnumerateWidgets(UserWidgetsRoot, NativeWidgetSource.User);

    private IReadOnlyList<NativeWidgetInfo> EnumerateWidgets(string root, NativeWidgetSource source)
    {
        var result = new List<NativeWidgetInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue; // e.g. .cache
                if (WidgetFolder.PeekKind(dir) != WidgetFolderKind.Native) continue;
                var info = TryGetWidget(name, source);
                if (info is not null) result.Add(info);
            }
            catch { }
        }
        return result;
    }

    public NativeWidgetInfo? TryGetWidget(string slug, NativeWidgetSource source)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        slug = SanitizeSlug(slug);
        var root = source == NativeWidgetSource.App ? AppWidgetsRoot : UserWidgetsRoot;
        var folder = Path.Combine(root, slug);
        if (!Directory.Exists(folder)) return null;
        if (WidgetFolder.PeekKind(folder) != WidgetFolderKind.Native) return null;

        var manifestPath = Path.Combine(folder, WidgetFolder.NativeManifestFileName);
        if (!File.Exists(manifestPath))
            return new NativeWidgetInfo(slug, source, folder, NativeWidgetManifest.DefaultFor(slug),
                ResolveThumbnail(folder, null, source), "Missing nwidget.json.");
        NativeWidgetManifest? manifest;
        try
        {
            manifest = NativeWidgetManifest.TryParse(File.ReadAllText(manifestPath, Encoding.UTF8), slug, out var error);
            if (manifest is null)
                return new NativeWidgetInfo(slug, source, folder, NativeWidgetManifest.DefaultFor(slug),
                    ResolveThumbnail(folder, null, source), error ?? "Unparsable nwidget.json.");
        }
        catch (Exception ex)
        {
            return new NativeWidgetInfo(slug, source, folder, NativeWidgetManifest.DefaultFor(slug),
                ResolveThumbnail(folder, null, source), ex.Message);
        }
        return new NativeWidgetInfo(slug, source, folder, manifest, ResolveThumbnail(folder, manifest.Thumbnail, source), null);
    }

    public NativeWidgetInfo? TryGetWidget(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return TryGetWidget(slug, NativeWidgetSource.User) ?? TryGetWidget(slug, NativeWidgetSource.App);
    }

    public NativeWidgetInfo? TryGetWidgetForContainer(DesktopItemContainer container)
    {
        if (container.Type != DesktopItemContainerType.NativeWidget) return null;
        if (string.IsNullOrWhiteSpace(container.NativeWidgetName)) return null;
        return TryGetWidget(container.NativeWidgetName);
    }

    public bool WidgetExists(string slug) =>
        !string.IsNullOrWhiteSpace(slug) && Directory.Exists(Path.Combine(UserWidgetsRoot, SanitizeSlug(slug)));

    public void DeleteWidget(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        slug = SanitizeSlug(slug);
        lock (_gate) UnloadLocked(slug);
        Forget(slug);
        var folder = Path.Combine(UserWidgetsRoot, slug);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    public void Refresh()
    {
        lock (_gate)
        {
            foreach (var slug in _contexts.Keys.ToList()) UnloadLocked(slug);
        }
    }

    // ---- trust-on-first-use consent store -----------------------------------------------
    //
    // There is no in-process sandbox on modern .NET: a plugin is full-trust code. The
    // shippable control is consent + fail-closed identity: user widgets load only after
    // the user trusted that exact content hash (gallery Place prompts once per hash);
    // changed content returns to untrusted and windows refuse to load it until re-placed.
    // Built-in (App) widgets ship with the app and are implicitly trusted.

    private string TrustFilePath => Path.Combine(SettingsService.AppDataDir, "NativeTrust.json");

    private Dictionary<string, string> TrustMap()
    {
        lock (_gate)
        {
            if (_trust is not null) return _trust;
            try
            {
                if (File.Exists(TrustFilePath))
                {
                    var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(TrustFilePath, Encoding.UTF8));
                    if (map is not null)
                    {
                        _trust = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
                        return _trust;
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning($"Native trust store unreadable, starting empty: {ex.Message}");
            }
            _trust = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _trust;
        }
    }

    private void SaveTrustLocked()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDataDir);
            File.WriteAllText(TrustFilePath,
                System.Text.Json.JsonSerializer.Serialize(_trust, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Native trust store save failed: {ex.Message}");
        }
    }

    public bool IsTrusted(NativeWidgetInfo widget)
    {
        try
        {
            if (widget.Source == NativeWidgetSource.App) return true;
            var hash = GetContentHash(widget);
            if (hash is null) return false;
            lock (_gate) return TrustMap().TryGetValue(widget.Slug, out var trusted) && trusted == hash;
        }
        catch { return false; }
    }

    public void Trust(NativeWidgetInfo widget)
    {
        try
        {
            var hash = GetContentHash(widget);
            if (hash is null) return;
            lock (_gate)
            {
                TrustMap()[widget.Slug] = hash;
                SaveTrustLocked();
            }
            Serilog.Log.Information($"Native widget trusted: {widget.Slug} ({hash[..12]})");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning($"Native trust record failed: {ex.Message}");
        }
    }

    public void Forget(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        lock (_gate)
        {
            if (TrustMap().Remove(slug)) SaveTrustLocked();
        }
    }

    public Task<string?> GenerateThumbnailAsync(NativeWidgetInfo widget, int width = 480, int height = 270, bool force = false)
    {
        // No async work inside (single offscreen frame); the Task shape mirrors the web
        // path so gallery code stays uniform. Must run on the UI thread (STA).
        return Task.FromResult(GenerateThumbnail(widget, width, height, force));
    }

    private string? GenerateThumbnail(NativeWidgetInfo widget, int width, int height, bool force)
    {
        try
        {
            if (widget.LoadError is not null) return null;
            // Rendering executes plugin code: trusted content only (same gate as hosting).
            if (!IsTrusted(widget)) return null;
            // Never write into the app install dir: built-in widgets render into the
            // shared %AppData% thumbnail cache, user widgets next to their manifest.
            string thumbPath = widget.Source == NativeWidgetSource.App
                ? Path.Combine(SettingsService.AppDataDir, "WidgetThumbnails", widget.Slug + ".png")
                : Path.Combine(widget.FolderPath,
                    string.IsNullOrWhiteSpace(widget.Manifest.Thumbnail) ? "thumbnail.png" : widget.Manifest.Thumbnail!);
            if (!force && File.Exists(thumbPath)) return thumbPath;
            width = Math.Clamp(width, 16, 1024);
            height = Math.Clamp(height, 16, 1024);

            INativeWidget plugin;
            try { plugin = CreateInstance(GetAssemblyPath(widget), widget); }
            catch { return null; }
            try
            {
                FrameworkElement visual;
                try { visual = plugin.Visual; }
                catch { return null; }
                if (widget.Manifest.SupportsTheme)
                {
                    try { plugin.ApplyTheme(ResolveTheme()); } catch { }
                }
                // Offscreen layout at the target size: never parented, never visible.
                visual.Measure(new Size(width, height));
                visual.Arrange(new Rect(0, 0, width, height));
                visual.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
                if (File.Exists(thumbPath)) File.Delete(thumbPath);
                using var stream = File.Create(thumbPath);
                encoder.Save(stream);
                Serilog.Log.Information($"Native widget thumbnail: {widget.Slug} -> {thumbPath}");
                return thumbPath;
            }
            finally { try { plugin.Dispose(); } catch { } }
        }
        catch { return null; }
    }

    private string? ResolveTheme()
    {
        string? global = null;
        try { global = _settings.UserSettings.DefaultWebWidgetsTheme?.Trim().ToLowerInvariant(); } catch { }
        if (global == "dark") return "dark";
        if (global == "light") return "light";
        try
        {
            var app = ApplicationThemeManager.GetAppTheme();
            if (app == ApplicationTheme.Dark) return "dark";
            if (app == ApplicationTheme.Light) return "light";
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? "dark" : "light";
        }
        catch { return null; }
    }

    public string? GetContentHash(NativeWidgetInfo widget)
    {
        try
        {
            // Assembly mode: hash the exact DLL bytes about to load.
            if (!string.IsNullOrWhiteSpace(widget.Manifest.Assembly))
            {
                var explicit_ = Path.Combine(widget.FolderPath, widget.Manifest.Assembly!);
                if (File.Exists(explicit_)) return HashFile(explicit_);
            }
            var dlls = Directory.EnumerateFiles(widget.FolderPath, "*.dll", SearchOption.TopDirectoryOnly).ToList();
            var sources = Directory.EnumerateFiles(widget.FolderPath, "*.cs", SearchOption.TopDirectoryOnly).ToList();
            if (sources.Count > 0) return ComputeSourceHash(widget.FolderPath);
            if (dlls.Count == 1) return HashFile(dlls[0]);
            // Ambiguous or missing: ResolveAssembly reports the actionable error.
            return null;
        }
        catch { return null; }
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }

    /// <summary>Content hash for a source folder: file names + bytes + SDK version.
    /// Shared by the trust check and the compile cache so both agree on identity.</summary>
    private static string ComputeSourceHash(string folder)
    {
        var sources = Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        using var sha = SHA256.Create();
        void Feed(byte[] bytes) => sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        foreach (var file in sources)
        {
            Feed(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0"));
            Feed(File.ReadAllBytes(file));
            Feed([(byte)0]);
        }
        foreach (var xaml in Directory.EnumerateFiles(folder, "*.xaml", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            Feed(Encoding.UTF8.GetBytes(Path.GetFileName(xaml) + "\0"));
            Feed(File.ReadAllBytes(xaml));
            Feed([(byte)0]);
        }
        Feed(Encoding.UTF8.GetBytes("sdk:" + typeof(INativeWidget).Assembly.GetName().Version));
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    /// <summary>Resolves the plugin assembly, compiling sources first when needed.
    /// Thread-agnostic but BLOCKING (compile can take seconds) — call off the UI thread.</summary>
    public string GetAssemblyPath(NativeWidgetInfo widget)
    {
        if (widget.LoadError is not null)
            throw new NativeWidgetLoadException(widget.Slug, widget.LoadError);
        try
        {
            return ResolveAssembly(widget);
        }
        catch (NativeWidgetLoadException ex)
        {
            Serilog.Log.Warning(ex, $"Native widget resolve failed: {widget.Slug}");
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, $"Native widget resolve failed: {widget.Slug}");
            throw new NativeWidgetLoadException(widget.Slug, ex.Message, ex);
        }
    }

    /// <summary>Loads the assembly, resolves the widget type and instantiates it. MUST run
    /// on an STA/UI thread: plugin constructors build WPF visuals, and constructing them
    /// on an MTA thread throws (surfaced by Activator as TargetInvocationException).
    /// Keep constructors fast — this runs on the UI thread by design.</summary>
    public INativeWidget CreateInstance(string assemblyPath, NativeWidgetInfo widget)
    {
        if (widget.LoadError is not null)
            throw new NativeWidgetLoadException(widget.Slug, widget.LoadError);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new NativeWidgetLoadException(widget.Slug,
                "CreateInstance must run on the UI (STA) thread. Resolve the assembly path " +
                "off-thread first, then instantiate on the UI thread.");
        try
        {
            var alc = GetOrLoadContext(widget, assemblyPath);
            var type = ResolveWidgetType(alc, assemblyPath, widget);
            var instance = Activator.CreateInstance(type);
            if (instance is not INativeWidget plugin)
                throw new NativeWidgetLoadException(widget.Slug, $"Type '{type.FullName}' does not implement INativeWidget.");
            Serilog.Log.Information($"Native widget instantiated: {widget.Slug} ({type.FullName})");
            return plugin;
        }
        catch (NativeWidgetLoadException ex)
        {
            Serilog.Log.Warning(ex, $"Native widget load failed: {widget.Slug}");
            throw;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, $"Native widget load failed: {widget.Slug}");
            throw new NativeWidgetLoadException(widget.Slug, ex.Message, ex);
        }
    }

    /// <summary>Legacy single-call shape (resolve + instantiate). Requires STA for the
    /// same reason as <see cref="CreateInstance(string, NativeWidgetInfo)"/>; prefer the
    /// split for UI responsiveness.</summary>
    public INativeWidget CreateInstance(NativeWidgetInfo widget) =>
        CreateInstance(GetAssemblyPath(widget), widget);

    private static string? ResolveThumbnail(string folder, string? name, NativeWidgetSource source)
    {
        try
        {
            var candidate = Path.Combine(folder, string.IsNullOrWhiteSpace(name) ? "thumbnail.png" : name!);
            if (File.Exists(candidate)) return candidate;
            var fallback = Path.Combine(folder, "thumbnail.png");
            if (!string.Equals(candidate, fallback, StringComparison.OrdinalIgnoreCase) && File.Exists(fallback))
                return fallback;
            // Built-ins live in the read-only install dir: generated thumbnails land in
            // the shared %AppData% cache instead (same convention as web widgets).
            if (source == NativeWidgetSource.App)
            {
                var cached = Path.Combine(SettingsService.AppDataDir, "WidgetThumbnails",
                    Path.GetFileName(folder) + ".png");
                if (File.Exists(cached)) return cached;
            }
        }
        catch { }
        return null;
    }

    // ---- assembly resolution (explicit DLL vs compiled sources) ---------------------------

    private string ResolveAssembly(NativeWidgetInfo widget)
    {
        var folder = widget.FolderPath;
        if (!string.IsNullOrWhiteSpace(widget.Manifest.Assembly))
        {
            var explicit_ = Path.Combine(folder, widget.Manifest.Assembly!);
            if (!File.Exists(explicit_))
                throw new NativeWidgetLoadException(widget.Slug, $"Assembly '{widget.Manifest.Assembly}' not found.");
            return explicit_;
        }
        var sources = Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly).ToList();
        if (sources.Count > 0) return CompileSources(widget, sources);
        var dlls = Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).ToList();
        if (dlls.Count == 1) return dlls[0];
        if (dlls.Count == 0)
            throw new NativeWidgetLoadException(widget.Slug, "No plugin DLL and no .cs sources. Add 'assembly', a single .dll, or sources.");
        throw new NativeWidgetLoadException(widget.Slug, $"Ambiguous: {dlls.Count} DLLs. Name one via 'assembly' in widget.json.");
    }

    private PluginLoadContext GetOrLoadContext(NativeWidgetInfo widget, string assemblyPath)
    {
        lock (_gate)
        {
            string key = widget.Slug + "|" + assemblyPath;
            if (_contexts.TryGetValue(key, out var existing)) return existing;
            var alc = new PluginLoadContext(Path.GetDirectoryName(assemblyPath)!);
            try
            {
                // Force the load now so failures surface here with the slug attached.
                alc.LoadFromAssemblyPath(assemblyPath);
            }
            catch (Exception ex)
            {
                try { alc.Unload(); } catch { }
                throw new NativeWidgetLoadException(widget.Slug, $"Assembly load failed: {ex.Message}", ex);
            }
            _contexts[key] = alc;
            return alc;
        }
    }

    private static Type ResolveWidgetType(PluginLoadContext alc, string assemblyPath, NativeWidgetInfo widget)
    {
        Assembly assembly;
        try
        {
            assembly = alc.LoadFromAssemblyName(AssemblyName.GetAssemblyName(assemblyPath));
        }
        catch (Exception ex)
        {
            throw new NativeWidgetLoadException(widget.Slug, $"Assembly load failed: {ex.Message}", ex);
        }
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
            foreach (var loaderEx in ex.LoaderExceptions.Where(e => e is not null))
                Serilog.Log.Warning($"Native widget '{widget.Slug}' type load: {loaderEx!.Message}");
        }
        var candidates = types
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(INativeWidget).IsAssignableFrom(t))
            .ToList();
        if (!string.IsNullOrWhiteSpace(widget.Manifest.Type))
        {
            var named = candidates.FirstOrDefault(t =>
                string.Equals(t.FullName, widget.Manifest.Type, StringComparison.Ordinal) ||
                string.Equals(t.Name, widget.Manifest.Type, StringComparison.Ordinal));
            if (named is null)
                throw new NativeWidgetLoadException(widget.Slug,
                    $"Type '{widget.Manifest.Type}' not found or does not implement INativeWidget. " +
                    $"Reference DesktopBoxes.WidgetSdk {typeof(INativeWidget).Assembly.GetName().Version} (host contracts).");
            return named;
        }
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0)
            throw new NativeWidgetLoadException(widget.Slug,
                "No INativeWidget implementation found. Reference DesktopBoxes.WidgetSdk " +
                $"{typeof(INativeWidget).Assembly.GetName().Version} (host contracts) and implement INativeWidget " +
                "(or derive NativeWidgetControl).");
        throw new NativeWidgetLoadException(widget.Slug,
            $"Ambiguous: {candidates.Count} INativeWidget types ({string.Join(", ", candidates.Select(t => t.FullName))}). " +
            "Name one via 'type' in widget.json.");
    }

    private void UnloadLocked(string slug)
    {
        foreach (var key in _contexts.Keys.Where(k => k.StartsWith(slug + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try { _contexts[key].Unload(); } catch { }
            _contexts.Remove(key);
        }
    }

    // ---- source compilation (Roslyn, hashed cache) ----------------------------------------

    /// <summary>Mirrors the SDK's implicit usings so plugin sources read like a normal
    /// project file without boilerplate headers.</summary>
    private const string ImplicitUsings = """
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

    private string CompileSources(NativeWidgetInfo widget, List<string> sources)
    {
        sources.Sort(StringComparer.OrdinalIgnoreCase);
        string hash = ComputeSourceHash(widget.FolderPath);
        string dir = Path.Combine(NativeCacheRoot, widget.Slug, hash);
        string dllPath = Path.Combine(dir, "plugin.dll");
        if (File.Exists(dllPath))
        {
            Serilog.Log.Information($"Native widget cache hit: {widget.Slug} ({hash[..12]})");
            return dllPath;
        }
        Serilog.Log.Information($"Compiling native widget: {widget.Slug} ({sources.Count} files)");
        Directory.CreateDirectory(dir);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        // Attach the encoding explicitly: string-parsed trees default to none and PDB
        // emit fails on them (CS8055).
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
        var references = TrustedReferences(widget.FolderPath);
        references.Add(MetadataReference.CreateFromFile(typeof(INativeWidget).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            $"NativeWidget_{widget.Slug}",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable));

        var xamls = Directory.EnumerateFiles(widget.FolderPath, "*.xaml", SearchOption.TopDirectoryOnly).ToList();
        var resources = xamls.Select(f => new ResourceDescription(
            $"{widget.Slug}/{Path.GetFileName(f)}",
            () => File.OpenRead(f),
            isPublic: true)).ToList();

        string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        EmitResult emit;
        using (var dllStream = File.Create(dllPath))
        using (var pdbStream = File.Create(pdbPath))
        {
            emit = compilation.Emit(dllStream, pdbStream, manifestResources: resources);
        }
        if (!emit.Success)
        {
            try { File.Delete(dllPath); File.Delete(pdbPath); } catch { }
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(5)
                .Select(d => d.ToString());
            throw new NativeWidgetLoadException(widget.Slug, "Compile failed: " + string.Join(" | ", errors));
        }
        Serilog.Log.Information($"Native widget compiled: {widget.Slug} -> {dllPath}");
        return dllPath;
    }

    private static List<MetadataReference> TrustedReferences(string pluginFolder)
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        var list = new List<MetadataReference>(256);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in tpa.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var name = Path.GetFileName(path);
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
                    names.Add(Path.GetFileNameWithoutExtension(name));
                }
            }
            catch { }
        }
        // Third-party DLLs shipped next to the sources (SkiaSharp, Vortice, …) so source
        // plugins can reference them. Host-owned assemblies are skipped to force type
        // unification with the running host; unloadable (native) files are skipped quietly.
        try
        {
            foreach (var path in Directory.EnumerateFiles(pluginFolder, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var simple = Path.GetFileNameWithoutExtension(path);
                    if (simple.StartsWith("DesktopBoxes.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!names.Add(simple)) continue;
                    list.Add(MetadataReference.CreateFromFile(path));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static string SanitizeSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "widget";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (invalid.Contains(c) || c == ' ') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString();
        if (s.Length > 64) s = s[..64];
        return string.IsNullOrWhiteSpace(s) ? "widget" : s;
    }

    /// <summary>Collectible per-plugin load context. Shared/host assemblies resolve from
    /// the default context first (unifying WidgetSdk + framework types); plugin-private
    /// DLLs resolve from the plugin folder.</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public PluginLoadContext(string dir) : base($"NativeWidget:{Path.GetFileName(dir)}", isCollectible: true)
        {
            _dir = dir;
            Resolving += OnResolving;
        }

        private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
                if (loaded is not null) return loaded;
                var candidate = Path.Combine(_dir, name.Name + ".dll");
                if (File.Exists(candidate)) return LoadFromAssemblyPath(candidate);
            }
            catch { }
            return null;
        }
    }
}
