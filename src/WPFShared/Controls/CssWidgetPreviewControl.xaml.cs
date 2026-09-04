using DesktopBoxesUI.Core.Models;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace WPFShared.Controls;

public partial class CssWidgetControl : UserControl
{
    //private readonly ICssWidgetService _widgetService;
    private bool _isInitialized;
    private CssWidgetManifest? _currentManifest;
    private string? _pendingHtml;
    private Task? _initTask;
    private static Task<CoreWebView2Environment>? s_envTask;

    public event EventHandler? WidgetMouseEnter;
    public event EventHandler? WidgetMouseLeave;
    public event EventHandler? WidgetClicked;
    public event EventHandler? WidgetMouseDown;

    public CssWidgetControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        await EnsureWebViewAsync();
        if (!string.IsNullOrEmpty(_pendingHtml))
        {
            NavigateToHtml(_pendingHtml);
            _pendingHtml = null;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Keep WebView alive for performance; disposal is handled by parent window's OnClosed
        // to avoid CoreWebView2Controller.IsVisible race during shutdown
    }

    public void CleanupForShutdown()
    {
        try { _initTask = null; } catch { }
        try
        {
            if (WebView != null)
            {
                try { WebView.Visibility = Visibility.Collapsed; } catch { }
                try
                {
                    if (WebView.CoreWebView2 != null)
                    {
                        WebView.CoreWebView2.WebMessageReceived -= OnWebMessage;
                        WebView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                    }
                }
                catch { }
                // Detach from visual tree before dispose so WPF teardown doesn't try to set
                // CoreWebView2Controller.IsVisible on an already-disposed controller
                try
                {
                    // WebView is inside this UserControl's Grid; removing it prevents later IsVisible set
                    if (WebView.Parent is Panel p) p.Children.Remove(WebView);
                }
                catch { }
                try { (WebView as IDisposable)?.Dispose(); } catch { }
            }
        }
        catch { }
    }

    private async System.Threading.Tasks.Task EnsureWebViewAsync()
    {
        // Already initialized (including implicit init via Source)
        if (_isInitialized || WebView.CoreWebView2 != null)
        {
            _isInitialized = true;
            return;
        }
        if (_initTask != null)
        {
            try { await _initTask; } catch { }
            return;
        }

        _initTask = EnsureCoreAsync();
        try { await _initTask; } finally { _initTask = null; }
    }

    private async System.Threading.Tasks.Task EnsureCoreAsync()
    {
        // Check again if WebView was initialized while we were waiting (e.g. Source set)
        if (WebView.CoreWebView2 != null)
        {
            _isInitialized = true;
            return;
        }
        try
        {
            // Ensure DefaultBackgroundColor is set before EnsureCoreWebView2Async for transparency
            try { WebView.DefaultBackgroundColor = Color.Transparent; } catch { }

            //var userData = SettingsService.AppDataDir;//Path.Combine(SettingsService.AppDataDir, "EBWebView");
            var userData = Path.Combine(Path.GetTempPath(), "EzzDesktopBoxesUI_EBWebView");
            try { Directory.CreateDirectory(userData); } catch { }

            // Share the same CoreWebView2Environment across all widget controls.
            // Creating a new environment with same path but different instance is considered
            // "different" by EnsureCoreWebView2Async and throws ArgumentException.
            s_envTask ??= CoreWebView2Environment.CreateAsync(null, userData);
            var env = await s_envTask;

            // If WebView was initialized implicitly in the meantime (e.g. NavigateToString), skip
            if (WebView.CoreWebView2 != null)
            {
                _isInitialized = true;
                return;
            }

            await WebView.EnsureCoreWebView2Async(env);
            if (WebView.CoreWebView2 == null) return;
            var settings = WebView.CoreWebView2.Settings;
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            // DOM mouse/activation bridge: WebView2CompositionControl does not reliably
            // raise WPF MouseEnter/MouseLeave (especially on inactive windows), so all
            // hover/click detection comes from the DOM via postMessage.
            //if (WebView is WebView2CompositionControl)
            //{
            //    this.MouseEnter += (ss, ee) => { WidgetMouseEnter?.Invoke(this, EventArgs.Empty); };
            //    this.MouseLeave += (ss, ee) => { WidgetMouseLeave?.Invoke(this, EventArgs.Empty); };
            //    //WebView.GotFocus += (ss, ee) => { WidgetClicked?.Invoke(this, EventArgs.Empty); };
            //    this.PreviewMouseUp += (ss, ee) => { WidgetClicked?.Invoke(this, EventArgs.Empty); };
            //}
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            const string domBridge = """(() => { let inside = false; function sendEnter() { if (!inside) { inside = true; try{chrome.webview.postMessage("enter");}catch(e){} } } function sendLeave() { if (inside) { inside = false; try{chrome.webview.postMessage("leave");}catch(e){} } } window.addEventListener("pointerenter", sendEnter); window.addEventListener("pointerleave", sendLeave); window.addEventListener("mouseenter", sendEnter); window.addEventListener("mouseleave", sendLeave); window.addEventListener("mousemove", () => { if (!inside) sendEnter(); }, { passive: true }); window.addEventListener("click", () => { try{chrome.webview.postMessage("click");}catch(e){} }); window.addEventListener("mousedown", (e) => { if (e.button !== 0) return; if (e.target.closest('button, a, input, select, textarea, [data-no-drag]')) return; try{chrome.webview.postMessage("mousedown");}catch(e){} }, { capture: true }); })();""";
            try
            {
                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(domBridge);
                // Also try immediate ExecuteScriptAsync if document already exists (NavigateToString case)
                //try { await WebView.CoreWebView2.ExecuteScriptAsync(domBridge); } catch { }
            }
            catch { }
            // Apply initial filter based on current manifest
            if (_currentManifest is not null) ApplyNetworkFilter(_currentManifest);
            _isInitialized = true;
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            // If WebView was already initialized with a different env (e.g. Source set earlier),
            // we can still proceed — CoreWebView2 is already available.
            if (WebView.CoreWebView2 != null)
            {
                _isInitialized = true;
                ErrorBar.IsOpen = false;
                return;
            }
            ErrorBar.Title = "WebView2 init failed";
            ErrorBar.Message = ex.Message.Contains("Edge") ? "Microsoft Edge WebView2 Runtime not found. Install Evergreen." : ex.Message;
            ErrorBar.IsOpen = true;
            PlaceholderText.Visibility = Visibility.Visible;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var msg = e.TryGetWebMessageAsString();
            if (msg == "enter") WidgetMouseEnter?.Invoke(this, EventArgs.Empty);
            else if (msg == "leave") WidgetMouseLeave?.Invoke(this, EventArgs.Empty);
            else if (msg == "click") WidgetClicked?.Invoke(this, EventArgs.Empty);
            else if (msg == "mousedown") WidgetMouseDown?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_currentManifest?.IsNetworkAllowed == true) return;
        if (WebView.CoreWebView2 == null) return;
        var uri = e.Request.Uri;
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            // Block network when not allowed, but allow data: and file: for local assets
            e.Response = WebView.CoreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null, 403, "Blocked", "Content-Type: text/plain");
        }
    }

    /* public async void LoadWidget(string slug, CssWidgetSource source)
     {
         var info = _widgetService.TryGetWidget(slug, source);
         if (info is null)
         {
             ShowPlaceholder($"Widget '{slug}' not found");
             return;
         }
         _currentManifest = info.Manifest;
         ApplyNetworkFilter(info.Manifest);
         var doc = _widgetService.BuildDocument(info);
         await EnsureAndNavigate(doc);
     }*/

    public async void LoadDirect(string html, string css, string js, CssWidgetManifest? manifest = null)
    {
        _currentManifest = manifest ?? new CssWidgetManifest { Resizable = true, AllowNetwork = false };
        ApplyNetworkFilter(_currentManifest);
        // Build doc similarly to service but from strings
        string doc;
        if (html.Contains("<html", StringComparison.OrdinalIgnoreCase))
        {
            doc = html;
            if (!string.IsNullOrWhiteSpace(css))
            {
                var tag = $"<style>{css}</style>";
                if (doc.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                    doc = doc.Replace("</head>", tag + "</head>", StringComparison.OrdinalIgnoreCase);
                else doc = tag + doc;
            }
            if (!string.IsNullOrWhiteSpace(js))
            {
                var tag = $"<script>{js}</script>";
                if (doc.Contains("</body>", StringComparison.OrdinalIgnoreCase))
                    doc = doc.Replace("</body>", tag + "</body>", StringComparison.OrdinalIgnoreCase);
                else doc += tag;
            }
        }
        else
        {
            doc = $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>{css}</style></head><body>{html}<script>{js}</script></body></html>";
        }
        await EnsureAndNavigate(doc);
    }

    private void ApplyNetworkFilter(CssWidgetManifest manifest)
    {
        if (!_isInitialized || WebView.CoreWebView2 is null) return;
        try
        {
            WebView.CoreWebView2.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
        catch { }
        if (manifest.IsNetworkAllowed) return;
        try
        {
            WebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        }
        catch { }
    }

    private async System.Threading.Tasks.Task EnsureAndNavigate(string html)
    {
        if (!_isInitialized)
        {
            _pendingHtml = html;
            await EnsureWebViewAsync();
            if (_pendingHtml is not null)
            {
                NavigateToHtml(_pendingHtml);
                _pendingHtml = null;
            }
            return;
        }
        NavigateToHtml(html);
    }

    private void NavigateToHtml(string html)
    {
        try
        {
            PlaceholderText.Visibility = Visibility.Collapsed;
            ErrorBar.IsOpen = false;
            WebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            ShowPlaceholder(ex.Message);
        }
    }

    private void ShowPlaceholder(string msg)
    {
        PlaceholderText.Text = msg;
        PlaceholderText.Visibility = Visibility.Visible;
    }

    public void Reload()
    {
        try { WebView.Reload(); } catch { }
    }

    private void Grid_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        Debug.WriteLine("Mouse Enter");
    }
}
