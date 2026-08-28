using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

public sealed record CssWidgetInfo(
    string Slug,
    CssWidgetSource Source,
    string FolderPath,
    CssWidgetManifest Manifest,
    string HtmlPath,
    string CssPath,
    string JsPath,
    string? ThumbnailPath);

public interface ICssWidgetService
{
    string UserWidgetsRoot { get; }
    string AppWidgetsRoot { get; }

    IReadOnlyList<CssWidgetInfo> GetAvailableWidgets();
    IReadOnlyList<CssWidgetInfo> GetAppWidgets();
    IReadOnlyList<CssWidgetInfo> GetUserWidgets();

    CssWidgetInfo? TryGetWidget(string slug, CssWidgetSource source);
    CssWidgetInfo? TryGetWidgetForContainer(DesktopItemContainer container);

    /// <summary>Creates a new user widget folder with given payload. Returns slug.</summary>
    string CreateUserWidget(string desiredName, string html, string css, string js, CssWidgetManifest manifest, byte[]? thumbnailBytes = null);
    void UpdateUserWidget(string slug, string html, string css, string js, CssWidgetManifest manifest, byte[]? thumbnailBytes = null);
    void DeleteUserWidget(string slug);
    bool UserWidgetExists(string slug);

    /// <summary>Builds a single HTML document string for WebView2 NavigateToString.</summary>
    string BuildDocument(CssWidgetInfo widget);

    /// <summary>Ensure UserWidgets directory exists and optionally seed defaults.</summary>
    void EnsureUserWidgetsRoot();

    /// <summary>Generates a thumbnail for the widget (renders offscreen and saves PNG). Returns saved path or null.</summary>
    Task<string?> GenerateThumbnailAsync(CssWidgetInfo widget, int width = 480, int height = 270);
}
