using DesktopBoxesUI.Controls.ContainersControls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Helpers;
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

public partial class WidgetDataWindow : AppWindows.AppFluentWindow
{
    private readonly IWebWidgetService _svc;
    private readonly IDialogService _dialogs;
    private readonly ViewModels.WidgetDataViewModel _vm;
    private readonly string? _originalSlug;
    private readonly bool _isNew;
    private readonly DispatcherTimer _debounce;
    private WebWidgetControl? _previewControl;

    /// <summary>Parameterless for the VS/Blend designer (delegates to new-widget mode).</summary>
    public WidgetDataWindow() : this(null, true)
    {
    }

    public WidgetDataWindow(string? slug, bool isNew)
    {
        InitializeComponent();
        _vm = new ViewModels.WidgetDataViewModel();
        DataContext = _vm;
        // Designer: no services — display content comes from the design factory binding.
        _svc = HelperUI.IsInDesignMode ? null! : App.Services.GetRequiredService<IWebWidgetService>();
        _dialogs = HelperUI.IsInDesignMode ? null! : App.Services.GetRequiredService<IDialogService>();
        _originalSlug = slug;
        _isNew = isNew;
        // Duplicate answers come from the service (new mode only — edit mode disables
        // renaming); null-safe so validation also runs in the designer.
        _vm.SlugExists = s => _isNew && _svc != null && _svc.UserWidgetExists(s);

        if (HelperUI.IsInDesignMode)
        {
            // Designer: VM-level samples only (pure CLR, always safe). Display flows
            // through the XAML bindings + design factory, never control assignments.
            _debounce = null!;
            _vm.LoadSampleData();
            return;
        }
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Designer content is filled in the ctor; never create the preview control there
        // (WebView2 has no design host).
        if (HelperUI.IsInDesignMode) return;
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
            // Clone data into the VM (bindings push it to the editors, so cancel
            // never touches the files).
            _vm.LoadFrom(info.Slug, info.Manifest,
                File.Exists(info.HtmlPath) ? File.ReadAllText(info.HtmlPath) : "",
                File.Exists(info.CssPath) ? File.ReadAllText(info.CssPath) : "",
                File.Exists(info.JsPath) ? File.ReadAllText(info.JsPath) : "");
            NameBox.IsEnabled = false; // don't rename on edit to keep folder stable; could allow rename with move
            Title = $"Edit Widget - {info.Slug}";
        }
        else
        {
            Title = "New Widget";
            _vm.LoadSampleData(blankIdentity: true);
        }
        // Edit mode: surface pre-existing name issues immediately; new mode stays quiet
        // until Save. Validation itself always runs explicitly in Save_Click.
        if (!_isNew) { try { _vm.Validate(); } catch { } }
        RefreshPreview();
    }

    private void ThemeSwitchBox_Changed(object sender, RoutedEventArgs e) => _debounce.Start();

    private void RefreshPreview()
    {
        if (_previewControl is null) return;
        // Editor edits mutate the shared documents in place — always current.
        var manifest = _vm.BuildManifest();
        _previewControl.LoadDirect(_vm.HtmlText, _vm.CssText, _vm.JsText, manifest);
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

    private bool _isSaving;

    /// <summary>Saving overlay: staged status + progress ring. The ring's IsIndeterminate
    /// is always driven together with Visibility (perf rule) — never just hidden.</summary>
    private void SetSaving(bool show, string status)
    {
        try
        {
            SaveStatusText.Text = status;
            SaveProgressRing.IsIndeterminate = show;
            SavingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private void EndSaving()
    {
        _isSaving = false;
        SetSaving(false, "");
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        // Explicit validation gate: errors surface natively via INotifyDataErrorInfo.
        if (!_vm.Validate())
        {
            try { NameBox.Focus(); } catch { }
            return;
        }
        if (_isSaving) return;
        _isSaving = true;
        SetSaving(true, "Verifying preview...");
        try { await Dispatcher.Yield(DispatcherPriority.Background); } catch { }
        // Render gate: refresh the preview from current text, await its load, and block
        // on hard failures (failed load, dead renderer, uncaught JS exceptions). An
        // unverifiable preview (no runtime yet) never blocks saving.
        RefreshPreview();
        if (_previewControl != null)
        {
            bool loaded = false;
            try { loaded = await _previewControl.WaitForCurrentLoadAsync(TimeSpan.FromSeconds(10)); } catch { }
            IReadOnlyList<string> issues = Array.Empty<string>();
            try { issues = loaded ? _previewControl.GetRenderIssues() : new[] { "Preview did not finish loading within 10 seconds." }; } catch { }
            if (issues.Count > 0)
            {
                try { await _dialogs.ShowMessageAsync("Cannot save — the preview has errors:\n• " + string.Join("\n• ", issues), "Preview errors"); } catch { }
                EndSaving();
                return;
            }
        }
        var html = _vm.HtmlText;
        var css = _vm.CssText;
        var js = _vm.JsText;
        var manifest = _vm.BuildManifest();

        string slug = string.IsNullOrWhiteSpace(_vm.Name) ? "widget" : _vm.Name.Trim();
        slug = slug.Trim();

        if (_isNew)
        {
            if (_svc.UserWidgetExists(slug))
            {
                await _dialogs.ShowMessageAsync($"A widget named '{slug}' already exists.", "Error");
                EndSaving();
                return;
            }
            SetSaving(true, "Writing files...");
            var newSlug = _svc.CreateUserWidget(slug, html, css, js, manifest);
            // Generate thumbnail for the newly created widget (fire-and-forget, show in gallery as "Generating...")
            SetSaving(true, "Generating thumbnail...");
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
            SetSaving(true, "Writing files...");
            _svc.UpdateUserWidget(targetSlug, html, css, js, manifest);
            // Force thumbnail regeneration (delete old first so Generate creates fresh)
            SetSaving(true, "Generating thumbnail...");
            try
            {
                var thumbPath = System.IO.Path.Combine(_svc.UserWidgetsRoot, targetSlug, "thumbnail.png");
                if (System.IO.File.Exists(thumbPath)) System.IO.File.Delete(thumbPath);
                var updInfo = _svc.TryGetWidget(targetSlug, WebWidgetSource.User);
                if (updInfo != null) await _svc.GenerateThumbnailAsync(updInfo);
            }
            catch { }
            // Broadcast reload to all placed widgets sharing this slug (update all on save)
            SetSaving(true, "Reloading placed widgets...");
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

        EndSaving();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // Never tear down mid-save: the pipeline owns the window until it finishes.
        if (_isSaving) return;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Same guard as Cancel (X button, Alt+F4, session end): closing mid-save would
        // tear down the preview control and dispatcher work the pipeline still needs.
        if (_isSaving)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

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
