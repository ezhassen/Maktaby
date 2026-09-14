using DesktopBoxesUI.Controls.ContainersControls;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Views.Containers;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using WPFShared.Interfaces;

namespace DesktopBoxesUI.Views;

public partial class WidgetDataWindow : FluentWindow, IContentDialogHostProvider
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
        try { _debounce.Stop(); } catch { }
        try { _previewControl?.CleanupForShutdown(); } catch { }
        try { PreviewHost.Content = null; } catch { }
        base.OnClosed(e);
    }
}
