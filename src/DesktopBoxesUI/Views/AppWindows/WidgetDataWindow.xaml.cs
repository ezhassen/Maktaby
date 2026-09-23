using DesktopBoxesUI.Controls.ContainersControls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Views.Containers;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WPFShared.Interfaces;

namespace DesktopBoxesUI.Views;

public partial class WidgetDataWindow : AppWindows.AppFluentWindow, IContentDialogHostProvider
{
    private readonly IWebWidgetService _svc;
    private readonly IDialogService _dialogs;
    private readonly string? _originalSlug;
    private readonly bool _isNew;
    private readonly DispatcherTimer _debounce;
    private WebWidgetControl? _previewControl;

    public WidgetDataWindow(string? slug, bool isNew)
    {
        InitializeComponent();
        _svc = App.Services.GetRequiredService<IWebWidgetService>();
        _dialogs = App.Services.GetRequiredService<IDialogService>();
        _originalSlug = slug;
        _isNew = isNew;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RefreshPreview(); };
        Loaded += OnLoaded;
        HtmlBox.TextChanged += (_, _) => _debounce.Start();
        CssBox.TextChanged += (_, _) => _debounce.Start();
        JsBox.TextChanged += (_, _) => _debounce.Start();
        ApplyEditorTheme();
        try { ApplicationThemeManager.Changed += OnAppThemeChanged; } catch { }
    }

    private void OnAppThemeChanged(ApplicationTheme _, System.Windows.Media.Color __)
    {
        try { Dispatcher.BeginInvoke(new Action(ApplyEditorTheme)); } catch { }
    }

    // The global dialog service renders WPF-UI content dialogs on this host.
    public ContentDialogHost DialogHost => RootContentDialogHost;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _previewControl = new WebWidgetControl();
        PreviewHost.Content = _previewControl;

        if (!_isNew && !string.IsNullOrWhiteSpace(_originalSlug))
        {
            var info = _svc.TryGetWidget(_originalSlug!, WebWidgetSource.User);
            if (info is null)
            {
                await _dialogs.ShowMessageAsync($"Widget '{_originalSlug}' not found.", "Error");
                Close();
                return;
            }
            // Clone data into editors (so cancel doesn't affect files)
            NameBox.Text = info.Slug;
            NameBox.IsEnabled = false; // don't rename on edit to keep folder stable; could allow rename with move
            AuthorBox.Text = info.Manifest.Author ?? "";
            DescBox.Text = info.Manifest.Description ?? "";
            VersionBox.Text = info.Manifest.Version ?? "1.0.0";
            WidthBox.Text = (info.Manifest.Width ?? 300).ToString();
            HeightBox.Text = (info.Manifest.Height ?? 220).ToString();
            ResizableBox.IsChecked = info.Manifest.IsResizable;
            NetworkBox.IsChecked = info.Manifest.IsNetworkAllowed;
            ThemeSwitchBox.IsChecked = info.Manifest.CanSwitchTheme;
            HtmlBox.Text = File.Exists(info.HtmlPath) ? File.ReadAllText(info.HtmlPath) : "";
            CssBox.Text = File.Exists(info.CssPath) ? File.ReadAllText(info.CssPath) : "";
            JsBox.Text = File.Exists(info.JsPath) ? File.ReadAllText(info.JsPath) : "";
            Title = $"Edit Widget - {info.Slug}";
        }
        else
        {
            Title = "New Widget";
            NameBox.Text = "";
            AuthorBox.Text = "";
            DescBox.Text = "";
            VersionBox.Text = "1.0.0";
            WidthBox.Text = "300";
            HeightBox.Text = "220";
            ResizableBox.IsChecked = true;
            NetworkBox.IsChecked = false;
            ThemeSwitchBox.IsChecked = null;
            HtmlBox.Text = "<div style=\"display:flex;align-items:center;justify-content:center;height:100%;font-family:sans-serif;font-size:18px;\">Hello Widget</div>";
            CssBox.Text = "body { margin:0; background:transparent; }";
            JsBox.Text = "// console.log('loaded');";
        }
        RefreshPreview();
    }

    private void ThemeSwitchBox_Changed(object sender, RoutedEventArgs e) => _debounce.Start();

    private void RefreshPreview()
    {
        if (_previewControl is null) return;
        var html = HtmlBox.Text ?? "";
        var css = CssBox.Text ?? "";
        var js = JsBox.Text ?? "";
        var manifest = BuildManifestFromFields();
        _previewControl.LoadDirect(html, css, js, manifest);
    }

    private void PreviewRefresh_Click(object sender, RoutedEventArgs e) => RefreshPreview();

    #region Editor context menu (Cut / Copy / Paste / Select All)

    private static ICSharpCode.AvalonEdit.TextEditor? MenuEditor(object sender)
        => ((sender as System.Windows.Controls.MenuItem)?.Parent as ContextMenu)?.PlacementTarget as ICSharpCode.AvalonEdit.TextEditor;

    private void EditorCut_Click(object sender, RoutedEventArgs e)
    {
        try { MenuEditor(sender)?.Cut(); } catch { }
    }

    private void EditorCopy_Click(object sender, RoutedEventArgs e)
    {
        try { MenuEditor(sender)?.Copy(); } catch { }
    }

    private void EditorPaste_Click(object sender, RoutedEventArgs e)
    {
        try { MenuEditor(sender)?.Paste(); } catch { }
    }

    private void EditorSelectAll_Click(object sender, RoutedEventArgs e)
    {
        try { MenuEditor(sender)?.SelectAll(); } catch { }
    }

    private void EditorMenu_Opened(object sender, RoutedEventArgs e)
    {
        try
        {
            var menu = sender as ContextMenu;
            var editor = menu?.PlacementTarget as ICSharpCode.AvalonEdit.TextEditor;
            bool hasSelection = editor != null && !editor.TextArea.Selection.IsEmpty;
            // Named fields are not generated for items inside a resource dictionary:
            // resolve Cut/Copy by header instead.
            if (menu != null)
            {
                foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
                {
                    if (item.Header as string is "Cut" or "Copy")
                        item.IsEnabled = hasSelection;
                }
            }
        }
        catch { }
    }

    #endregion

    private WebWidgetManifest BuildManifestFromFields()
    {
        int.TryParse(WidthBox.Text, out int w);
        int.TryParse(HeightBox.Text, out int h);
        return new WebWidgetManifest
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "widget" : NameBox.Text.Trim(),
            Author = AuthorBox.Text?.Trim(),
            Description = DescBox.Text?.Trim(),
            Version = VersionBox.Text?.Trim(),
            Width = w > 0 ? w : 300,
            Height = h > 0 ? h : 220,
            Resizable = ResizableBox.IsChecked ?? true,
            AllowNetwork = NetworkBox.IsChecked ?? false,
            CanSwitchTheme = ThemeSwitchBox.IsChecked
        };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var html = HtmlBox.Text ?? "";
        var css = CssBox.Text ?? "";
        var js = JsBox.Text ?? "";
        var manifest = BuildManifestFromFields();

        string slug = string.IsNullOrWhiteSpace(NameBox.Text) ? "widget" : NameBox.Text.Trim();
        slug = slug.Trim();

        if (_isNew)
        {
            if (_svc.UserWidgetExists(slug))
            {
                await _dialogs.ShowMessageAsync($"A widget named '{slug}' already exists.", "Error");
                return;
            }
            var newSlug = _svc.CreateUserWidget(slug, html, css, js, manifest);
            // Generate thumbnail for the newly created widget (fire-and-forget, show in gallery as "Generating...")
            try
            {
                var info = _svc.TryGetWidget(newSlug, WebWidgetSource.User);
                if (info != null) await _svc.GenerateThumbnailAsync(info);
            }
            catch { }
        }
        else
        {
            var targetSlug = _originalSlug ?? slug;
            _svc.UpdateUserWidget(targetSlug, html, css, js, manifest);
            // Force thumbnail regeneration (delete old first so Generate creates fresh)
            try
            {
                var thumbPath = System.IO.Path.Combine(_svc.UserWidgetsRoot, targetSlug, "thumbnail.png");
                if (System.IO.File.Exists(thumbPath)) System.IO.File.Delete(thumbPath);
                var updInfo = _svc.TryGetWidget(targetSlug, WebWidgetSource.User);
                if (updInfo != null) await _svc.GenerateThumbnailAsync(updInfo);
            }
            catch { }
            // Broadcast reload to all placed widgets sharing this slug (update all on save)
            foreach (var win in System.Windows.Application.Current.Windows.OfType<WebWidgetWindow>())
            {
                win.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var field = typeof(WebWidgetWindow).GetField("_container", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (field?.GetValue(win) is DesktopBoxesUI.Core.Models.DesktopItemContainer c)
                        {
                            if (string.Equals(c.WebWidgetName, targetSlug, StringComparison.OrdinalIgnoreCase)
                                && c.WebWidgetSource == WebWidgetSource.User)
                            {
                                var wcField = typeof(WebWidgetWindow).GetField("_widgetControl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                if (wcField?.GetValue(win) is WebWidgetControl ctrl)
                                    ctrl.LoadWidget(targetSlug, WebWidgetSource.User);
                            }
                        }
                    }
                    catch { }
                });
            }
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        try { ApplicationThemeManager.Changed -= OnAppThemeChanged; } catch { }
        try { _debounce.Stop(); } catch { }
        try { _previewControl?.CleanupForShutdown(); } catch { }
        try { PreviewHost.Content = null; } catch { }
        base.OnClosed(e);
    }

    #region Editor highlighting themes

    private static bool IsDarkTheme()
    {
        try
        {
            var app = ApplicationThemeManager.GetAppTheme();
            if (app == ApplicationTheme.Dark) return true;
            if (app == ApplicationTheme.Light) return false;
            return ApplicationThemeManager.GetSystemTheme() != SystemTheme.Light;
        }
        catch { return true; }
    }

    /// <summary>Swaps the three editors between the stock (light) definitions and dark
    /// variants built from AvalonEdit's own embedded grammars. Editor chrome follows the
    /// app theme via DynamicResource; only token colors need swapping.</summary>
    private void ApplyEditorTheme()
    {
        try
        {
            var mgr = HighlightingManager.Instance;
            bool dark = IsDarkTheme();
            HtmlBox.SyntaxHighlighting = dark
                ? GetDarkHighlighting("HTML", "HTML-Mode.xshd", HtmlDarkColors) ?? mgr.GetDefinition("HTML")
                : mgr.GetDefinition("HTML");
            CssBox.SyntaxHighlighting = dark
                ? GetDarkHighlighting("CSS", "CSS-Mode.xshd", CssDarkColors) ?? mgr.GetDefinition("CSS")
                : mgr.GetDefinition("CSS");
            JsBox.SyntaxHighlighting = dark
                ? GetDarkHighlighting("JavaScript", "JavaScript-Mode.xshd", JsDarkColors) ?? mgr.GetDefinition("JavaScript")
                : mgr.GetDefinition("JavaScript");
        }
        catch { }
    }

    private static readonly Dictionary<string, IHighlightingDefinition?> _darkHighlightings = new();

    /// <summary>Builds (once, then cached) a dark variant of a stock definition by recoloring
    /// its embedded XSHD grammar text before parsing. Falls back to null (caller uses stock).</summary>
    private static IHighlightingDefinition? GetDarkHighlighting(
        string key, string resource, Dictionary<string, string> colors)
    {
        try
        {
            lock (_darkHighlightings)
            {
                if (_darkHighlightings.TryGetValue(key, out var cached)) return cached;
                using var stream = typeof(ICSharpCode.AvalonEdit.TextEditor).Assembly
                    .GetManifestResourceStream("ICSharpCode.AvalonEdit.Highlighting.Resources." + resource);
                if (stream is null) return null;
                using var reader = new StreamReader(stream);
                string xml = reader.ReadToEnd();
                foreach (var kv in colors)
                {
                    xml = Regex.Replace(xml,
                        $@"(<Color name=""{Regex.Escape(kv.Key)}""[^>]*?foreground="")[^""]*("")",
                        $"$1{kv.Value}$2");
                }
                using var xr = XmlReader.Create(new StringReader(xml));
                var xshd = HighlightingLoader.LoadXshd(xr);
                var def = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
                HighlightingManager.Instance.RegisterHighlighting(key + "-Dark", Array.Empty<string>(), def);
                _darkHighlightings[key] = def;
                return def;
            }
        }
        catch { return null; }
    }

    // VS Code dark+-inspired remaps of the exact stock foregrounds (dumped from the
    // embedded grammars so names/values are exact, not guessed).
    private static readonly Dictionary<string, string> HtmlDarkColors = new()
    {
        ["Assignment"] = "#569CD6",
        ["Attributes"] = "#9CDCFE",
        ["Comment"] = "#6A9955",
        ["Digits"] = "#B5CEA8",
        ["Entities"] = "#4EC9B0",
        ["EntityReference"] = "#569CD6",
        ["HtmlTag"] = "#569CD6",
        ["JavaScriptTag"] = "#569CD6",
        ["JScriptTag"] = "#569CD6",
        ["ScriptTag"] = "#569CD6",
        ["Slash"] = "#808080",
        ["String"] = "#CE9178",
        ["Tags"] = "#569CD6",
        ["UnknownAttribute"] = "#9CDCFE",
        ["UnknownScriptTag"] = "#569CD6",
        ["VBScriptTag"] = "#569CD6",
    };

    private static readonly Dictionary<string, string> CssDarkColors = new()
    {
        ["Class"] = "#D7BA7D",
        ["Colon"] = "#D4D4D4",
        ["Comment"] = "#6A9955",
        ["CurlyBraces"] = "#D4D4D4",
        ["Property"] = "#9CDCFE",
        ["Selector"] = "#D7BA7D",
        ["String"] = "#CE9178",
        ["Value"] = "#CE9178",
    };

    private static readonly Dictionary<string, string> JsDarkColors = new()
    {
        ["Character"] = "#CE9178",
        ["Comment"] = "#6A9955",
        ["Digits"] = "#B5CEA8",
        ["JavaScriptGlobalFunctions"] = "#DCDCAA",
        ["JavaScriptIntrinsics"] = "#DCDCAA",
        ["JavaScriptKeyWords"] = "#569CD6",
        ["JavaScriptLiterals"] = "#569CD6",
        ["Regex"] = "#CE9178",
        ["String"] = "#CE9178",
    };

    #endregion
}
