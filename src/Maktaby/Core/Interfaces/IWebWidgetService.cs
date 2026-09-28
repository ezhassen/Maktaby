using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

public sealed record WebWidgetInfo(
    string Slug,
    WebWidgetSource Source,
    string FolderPath,
    WebWidgetManifest Manifest,
    string HtmlPath,
    string CssPath,
    string JsPath,
    string? ThumbnailPath);

public interface IWebWidgetService
{
    string UserWidgetsRoot { get; }
    string AppWidgetsRoot { get; }

    IReadOnlyList<WebWidgetInfo> GetAvailableWidgets();
    IReadOnlyList<WebWidgetInfo> GetAppWidgets();
    IReadOnlyList<WebWidgetInfo> GetUserWidgets();

    WebWidgetInfo? TryGetWidget(string slug, WebWidgetSource source);
    WebWidgetInfo? TryGetWidgetForContainer(DesktopItemContainer container);

    /// <summary>Creates a new user widget folder with given payload. Returns slug.</summary>
    string CreateUserWidget(string desiredName, string html, string css, string js, WebWidgetManifest manifest, byte[]? thumbnailBytes = null);
    void UpdateUserWidget(string slug, string html, string css, string js, WebWidgetManifest manifest, byte[]? thumbnailBytes = null);
    void DeleteUserWidget(string slug);
    bool UserWidgetExists(string slug);

    /// <summary>Builds a single HTML document string for WebView2 NavigateToString.</summary>
    string BuildDocument(WebWidgetInfo widget);

    /// <summary>Ensure UserWidgets directory exists and optionally seed defaults.</summary>
    void EnsureUserWidgetsRoot();

    /// <summary>Generates a thumbnail for the widget (renders offscreen and saves PNG). Returns saved path or null.</summary>
    Task<string?> GenerateThumbnailAsync(WebWidgetInfo widget, int width = 480, int height = 270, bool force = false);
}
