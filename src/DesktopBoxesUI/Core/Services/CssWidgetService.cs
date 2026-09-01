using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace DesktopBoxesUI.Core.Services;

public sealed class CssWidgetService : ICssWidgetService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string UserWidgetsRoot => Path.Combine(SettingsService.AppDataDir, "UserWidgets");
    public string AppWidgetsRoot
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "CSSWidgets");
            if (Directory.Exists(candidate)) return candidate;
            try
            {
                var dev = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "DesktopBoxesUI", "CSSWidgets"));
                if (Directory.Exists(dev)) return dev;
                var dev2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "CSSWidgets"));
                if (Directory.Exists(dev2)) return dev2;
            }
            catch { }
            return candidate;
        }
    }

    public void EnsureUserWidgetsRoot()
    {
        try { Directory.CreateDirectory(UserWidgetsRoot); } catch { }
    }

    public IReadOnlyList<CssWidgetInfo> GetAvailableWidgets()
    {
        var list = new List<CssWidgetInfo>();
        list.AddRange(GetAppWidgets());
        list.AddRange(GetUserWidgets());
        return list;
    }

    public IReadOnlyList<CssWidgetInfo> GetAppWidgets() => EnumerateWidgets(AppWidgetsRoot, CssWidgetSource.App);
    public IReadOnlyList<CssWidgetInfo> GetUserWidgets() => EnumerateWidgets(UserWidgetsRoot, CssWidgetSource.User);

    private IReadOnlyList<CssWidgetInfo> EnumerateWidgets(string root, CssWidgetSource source)
    {
        var result = new List<CssWidgetInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var slug = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var info = TryGetWidget(slug, source);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    public CssWidgetInfo? TryGetWidget(string slug, CssWidgetSource source)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        slug = SanitizeSlug(slug);
        var root = source == CssWidgetSource.App ? AppWidgetsRoot : UserWidgetsRoot;
        var folder = Path.Combine(root, slug);
        if (!Directory.Exists(folder)) return null;

        var manifestPath = Path.Combine(folder, "widget.json");
        CssWidgetManifest manifest;
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                manifest = JsonSerializer.Deserialize<CssWidgetManifest>(json, JsonOpts) ?? CssWidgetManifest.DefaultFor(slug);
            }
            catch { manifest = CssWidgetManifest.DefaultFor(slug); }
        }
        else
        {
            manifest = CssWidgetManifest.DefaultFor(slug);
        }
        manifest.Name ??= slug;

        var htmlPath = Path.Combine(folder, "index.html");
        var cssPath = Path.Combine(folder, "style.css");
        var jsPath = Path.Combine(folder, "script.js");
        var thumbName = string.IsNullOrWhiteSpace(manifest.Thumbnail) ? "thumbnail.png" : manifest.Thumbnail!;
        var thumbPath = Path.Combine(folder, thumbName);
        if (!File.Exists(thumbPath)) thumbPath = Path.Combine(folder, "thumbnail.png");
        string? thumb = File.Exists(thumbPath) ? thumbPath : null;
        if (thumb == null && source == CssWidgetSource.App)
        {
            var cacheThumb = Path.Combine(SettingsService.AppDataDir, "WidgetThumbnails", slug + ".png");
            if (File.Exists(cacheThumb)) thumb = cacheThumb;
        }
        return new CssWidgetInfo(slug, source, folder, manifest, htmlPath, cssPath, jsPath, thumb);
    }

    public CssWidgetInfo? TryGetWidgetForContainer(DesktopItemContainer container)
    {
        if (container.Type != DesktopItemContainerType.CssWidget) return null;
        var slug = container.CssWidgetName;
        var source = container.CssWidgetSource ?? CssWidgetSource.User;
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return TryGetWidget(slug, source);
    }

    public bool UserWidgetExists(string slug) => Directory.Exists(Path.Combine(UserWidgetsRoot, SanitizeSlug(slug)));

    public string CreateUserWidget(string desiredName, string html, string css, string js, CssWidgetManifest manifest, byte[]? thumbnailBytes = null)
    {
        EnsureUserWidgetsRoot();
        var slug = SanitizeSlug(desiredName);
        if (string.IsNullOrWhiteSpace(slug)) slug = "widget";
        var baseSlug = slug;
        int n = 1;
        while (UserWidgetExists(slug))
        {
            slug = $"{baseSlug}{n++}";
        }
        var folder = Path.Combine(UserWidgetsRoot, slug);
        Directory.CreateDirectory(folder);
        WriteWidgetFolder(folder, html, css, js, manifest, thumbnailBytes, slug);
        return slug;
    }

    public void UpdateUserWidget(string slug, string html, string css, string js, CssWidgetManifest manifest, byte[]? thumbnailBytes = null)
    {
        slug = SanitizeSlug(slug);
        var folder = Path.Combine(UserWidgetsRoot, slug);
        Directory.CreateDirectory(folder);
        WriteWidgetFolder(folder, html, css, js, manifest, thumbnailBytes, slug);
    }

    public void DeleteUserWidget(string slug)
    {
        slug = SanitizeSlug(slug);
        var folder = Path.Combine(UserWidgetsRoot, slug);
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }

    private void WriteWidgetFolder(string folder, string html, string css, string js, CssWidgetManifest manifest, byte[]? thumbnailBytes, string slug)
    {
        manifest.Name ??= slug;
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        File.WriteAllText(Path.Combine(folder, "widget.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, "index.html"), html ?? string.Empty, Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, "style.css"), css ?? string.Empty, Encoding.UTF8);
        File.WriteAllText(Path.Combine(folder, "script.js"), js ?? string.Empty, Encoding.UTF8);
        if (thumbnailBytes is not null && thumbnailBytes.Length > 0)
        {
            var thumbName = string.IsNullOrWhiteSpace(manifest.Thumbnail) ? "thumbnail.png" : manifest.Thumbnail!;
            File.WriteAllBytes(Path.Combine(folder, thumbName), thumbnailBytes);
        }
    }

    public string BuildDocument(CssWidgetInfo widget)
    {
        string html = File.Exists(widget.HtmlPath) ? File.ReadAllText(widget.HtmlPath, Encoding.UTF8) : "<div>No content</div>";
        string css = File.Exists(widget.CssPath) ? File.ReadAllText(widget.CssPath, Encoding.UTF8) : string.Empty;
        string js = File.Exists(widget.JsPath) ? File.ReadAllText(widget.JsPath, Encoding.UTF8) : string.Empty;
        bool isFullDoc = html.Contains("<html", StringComparison.OrdinalIgnoreCase);
        if (isFullDoc)
        {
            if (!string.IsNullOrWhiteSpace(css))
            {
                var styleTag = $"<style>{css}</style>";
                if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                    html = html.Replace("</head>", styleTag + "</head>", StringComparison.OrdinalIgnoreCase);
                else if (html.Contains("<head>", StringComparison.OrdinalIgnoreCase))
                    html = html.Replace("<head>", "<head>" + styleTag, StringComparison.OrdinalIgnoreCase);
                else
                    html = styleTag + html;
            }
            if (!string.IsNullOrWhiteSpace(js))
            {
                var scriptTag = $"<script>{js}</script>";
                if (html.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    html = html.Replace("</body>", scriptTag + "</body>", StringComparison.OrdinalIgnoreCase);
                else
                    html += scriptTag;
            }
            return html;
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.Append("<style>html, body { margin: 0; padding: 0; overflow: hidden; width: 100%; height: 100%; }</style>");
            if (!string.IsNullOrWhiteSpace(css)) sb.Append($"<style>{css}</style>");
            sb.Append("</head><body>");
            sb.Append(html);
            if (!string.IsNullOrWhiteSpace(js)) sb.Append($"<script>{js}</script>");
            sb.Append("</body></html>");
            return sb.ToString();
        }
    }

    public async Task<string?> GenerateThumbnailAsync(CssWidgetInfo widget, int width = 480, int height = 270, bool force = false)
    {
        if (!string.IsNullOrEmpty(widget.ThumbnailPath) && File.Exists(widget.ThumbnailPath) && !force)
            return widget.ThumbnailPath;

        string outputPath;
        if (widget.Source == CssWidgetSource.App)
        {
            var cacheDir = Path.Combine(SettingsService.AppDataDir, "WidgetThumbnails");
            Directory.CreateDirectory(cacheDir);
            outputPath = Path.Combine(cacheDir, widget.Slug + ".png");
            //if (File.Exists(outputPath)) return outputPath;
        }
        else
        {
            outputPath = Path.Combine(widget.FolderPath, "thumbnail.png");
        }

        var doc = BuildDocument(widget);

        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            return await Application.Current.Dispatcher.InvokeAsync(() => GenerateThumbnailAsync(widget, width, height)).Task.Unwrap();
        }
        if (Application.Current == null) return null;

        Window? win = null;
        WebView2? web = null;
        try
        {
            // Check if widget CSS has transparent background
            var info = TryGetWidget(widget.Slug, widget.Source);
            var css = info != null && File.Exists(info.CssPath) ? File.ReadAllText(info.CssPath) : "";
            var hasTransparentBg = !string.IsNullOrWhiteSpace(css) &&
                (css.Contains("transparent", StringComparison.OrdinalIgnoreCase) ||
                 css.Contains("rgba", StringComparison.OrdinalIgnoreCase));

            var windowBackground = hasTransparentBg
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent)
                : System.Windows.Media.Brushes.White;

            win = new Window
            {
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Visibility = Visibility.Hidden,
                Background = windowBackground,
                AllowsTransparency = hasTransparentBg,
                Top = -10000,
                Left = -10000
            };
            web = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            win.Content = web;
            //EBWebView created by default in target folder
            var userData = SettingsService.AppDataDir;//Path.Combine(SettingsService.AppDataDir, "EBWebView");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            win.Show();
            await web.EnsureCoreWebView2Async(env);
            try
            {
                web.DefaultBackgroundColor = hasTransparentBg
                    ? System.Drawing.Color.Transparent
                    : System.Drawing.Color.White;
            }
            catch { }

            var navTcs = new TaskCompletionSource<bool>();
            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                web.CoreWebView2.NavigationCompleted -= Handler;
                navTcs.TrySetResult(e.IsSuccess);
            }
            web.CoreWebView2.NavigationCompleted += Handler;
            web.NavigateToString(doc);

            var completed = await Task.WhenAny(navTcs.Task, Task.Delay(5000));
            if (completed != navTcs.Task || !navTcs.Task.Result)
            {
                try { web.Visibility = Visibility.Collapsed; } catch { }
                try { (web as IDisposable)?.Dispose(); } catch { }
                try { win.Close(); } catch { }
                return null;
            }
            await Task.Delay(800);

            // Measure actual DOM content size
            try
            {
                var sizeJson = await web.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({ width: document.body.scrollWidth, height: document.body.scrollHeight })");
                if (!string.IsNullOrEmpty(sizeJson) && sizeJson.Length > 2)
                {
                    sizeJson = sizeJson.Trim('"').Replace("\\\"", "\"");
                    var jsonDoc = System.Text.Json.JsonDocument.Parse(sizeJson);
                    var root = jsonDoc.RootElement;
                    int contentWidth = root.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    int contentHeight = root.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                    if (contentWidth > 0 && contentHeight > 0)
                    {
                        // Use actual content size or manifest size, whichever is larger
                        var actualWidth = Math.Max(widget.Manifest.Width ?? width, contentWidth);
                        var actualHeight = Math.Max(widget.Manifest.Height ?? height, contentHeight);

                        if (actualWidth != width || actualHeight != height)
                        {
                            win.Width = actualWidth;
                            win.Height = actualHeight;
                            web.Width = actualWidth;
                            web.Height = actualHeight;
                            await Task.Delay(1000); // Wait for layout to settle
                        }
                    }
                }
            }
            catch { }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            using var fs = new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            await fs.FlushAsync();
            try { web.Visibility = Visibility.Collapsed; } catch { }
            try { (web as IDisposable)?.Dispose(); } catch { }
            try { win.Close(); } catch { }
            return outputPath;
        }
        catch
        {
            try { if (web != null) { try { web.Visibility = Visibility.Collapsed; } catch { } try { (web as IDisposable)?.Dispose(); } catch { } } } catch { }
            try { win?.Close(); } catch { }
            return null;
        }
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
}
