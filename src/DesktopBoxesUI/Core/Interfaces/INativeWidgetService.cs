using DesktopBoxes.WidgetSdk;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>Thrown when a native widget cannot be instantiated (missing/broken manifest,
/// no sources, compile errors, load failure, no widget type). Carries the folder slug so
/// callers can show it and keep going — a broken plugin must never take down the host.</summary>
public sealed class NativeWidgetLoadException(string slug, string reason, Exception? inner = null)
    : Exception($"Native widget '{slug}': {reason}", inner)
{
    public string Slug { get; } = slug;
    public string Reason { get; } = reason;
}

/// <summary>A discovered native widget folder. <see cref="LoadError"/> is set when the
/// folder claims native (has <c>nwidget.json</c>) but cannot be used; the gallery shows
/// those with an error badge and blocks placement.</summary>
public sealed record NativeWidgetInfo(
    string Slug,
    NativeWidgetSource Source,
    string FolderPath,
    NativeWidgetManifest Manifest,
    string? ThumbnailPath,
    string? LoadError);

public interface INativeWidgetService
{
    /// <summary>Shared with CSS widgets: %AppData%/DesktopBoxes/UserWidgets.</summary>
    string UserWidgetsRoot { get; }

    /// <summary>Built-in widgets shipped next to the app: &lt;BaseDir&gt;/NativeWidgets.</summary>
    string AppWidgetsRoot { get; }

    /// <summary>Compiled-plugin cache: %AppData%/DesktopBoxes/NativeCache.</summary>
    string NativeCacheRoot { get; }

    IReadOnlyList<NativeWidgetInfo> GetAvailableWidgets();
    IReadOnlyList<NativeWidgetInfo> GetAppWidgets();
    IReadOnlyList<NativeWidgetInfo> GetUserWidgets();

    NativeWidgetInfo? TryGetWidget(string slug, NativeWidgetSource source);

    /// <summary>User folder first, then built-in (user shadows app).</summary>
    NativeWidgetInfo? TryGetWidget(string slug);

    NativeWidgetInfo? TryGetWidgetForContainer(DesktopItemContainer container);

    bool WidgetExists(string slug);

    void DeleteWidget(string slug);

    /// <summary>Resolves the plugin assembly path, compiling sources first when needed.
    /// Thread-agnostic but BLOCKING (compile can take seconds) — call off the UI thread.
    /// Throws <see cref="NativeWidgetLoadException"/> on any failure.</summary>
    string GetAssemblyPath(NativeWidgetInfo widget);

    /// <summary>Loads the assembly, resolves the widget type and instantiates it. MUST run
    /// on an STA/UI thread: plugin constructors build WPF visuals. Keep constructors fast.
    /// Throws <see cref="NativeWidgetLoadException"/> on any failure.</summary>
    INativeWidget CreateInstance(string assemblyPath, NativeWidgetInfo widget);

    /// <summary>Legacy single-call shape (resolve + instantiate). Requires STA; prefer the
    /// split above for UI responsiveness.</summary>
    INativeWidget CreateInstance(NativeWidgetInfo widget);

    /// <summary>Unloads cached assemblies so edited sources recompile on next create.
    /// Already-open windows keep their loaded version until closed.</summary>
    void Refresh();

    /// <summary>Trust-on-first-use consent store (see docs). App widgets are implicitly
    /// trusted (shipped with the app); user widgets must be trusted once per content
    /// hash before any code loads. Unload-safe: trust is content, not instance.</summary>
    bool IsTrusted(NativeWidgetInfo widget);
    void Trust(NativeWidgetInfo widget);
    void Forget(string slug);

    /// <summary>Content hash the trust decision keys on (source hash or DLL bytes).
    /// Null when indeterminable — treated as untrusted.</summary>
    string? GetContentHash(NativeWidgetInfo widget);

    /// <summary>Renders the widget offscreen (never parented/visible) and saves a PNG
    /// thumbnail (manifest thumbnail name, default <c>thumbnail.png</c>). Trusted widgets
    /// only — rendering executes plugin code. Must run on the UI thread (STA). Existing
    /// thumbnails are kept unless <paramref name="force"/>. Returns the saved path or null.</summary>
    Task<string?> GenerateThumbnailAsync(NativeWidgetInfo widget, int width = 480, int height = 270, bool force = false);
}
