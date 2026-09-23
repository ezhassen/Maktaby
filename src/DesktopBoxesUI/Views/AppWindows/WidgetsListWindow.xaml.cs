using DesktopBoxes.WidgetSdk;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using WPFShared.Interfaces;

namespace DesktopBoxesUI.Views;

/// <summary>Widget taxonomy shown in the gallery. CSS widgets render web content;
/// native widgets run plugin code.</summary>
public enum WidgetGalleryKind
{
    Web,
    Native,
}

public partial class WidgetsListWindow : AppWindows.AppFluentWindow, IContentDialogHostProvider, INotifyPropertyChanged
{
    private readonly IWebWidgetService _svc;
    private readonly INativeWidgetService _native;
    private readonly IDialogService _dialogs;
    public bool IsSelectMode { get; }
    public WidgetGalleryKind? SelectKind { get; }
    public WebWidgetInfo? SelectedInfo { get; private set; }
    public NativeWidgetInfo? SelectedNativeInfo { get; private set; }

    private int _inFlight;
    /// <summary>True while any thumbnail generation/compilation is running.
    /// The window cannot be closed while set (see <see cref="OnClosing"/>).</summary>
    public bool IsWorking
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void BeginWork()
    {
        _inFlight++;
        UpdateIsWorking();
    }

    private void EndWork()
    {
        _inFlight = Math.Max(0, _inFlight - 1);
        UpdateIsWorking();
    }

    private void UpdateIsWorking()
    {
        IsWorking = _inFlight > 0;
        try { CloseButton.IsEnabled = !IsWorking; } catch { }
    }

    // The global dialog service renders WPF-UI content dialogs on this host.
    public ContentDialogHost DialogHost => RootContentDialogHost;
    public WidgetsListWindow(bool selectMode = false, WidgetGalleryKind? selectKind = null)
    {
        InitializeComponent();
        IsSelectMode = selectMode;
        SelectKind = selectKind;
        _svc = App.Services.GetRequiredService<IWebWidgetService>();
        _native = App.Services.GetRequiredService<INativeWidgetService>();
        _dialogs = App.Services.GetRequiredService<IDialogService>();
        DataContext = this;
        Loaded += (_, _) => Refresh();
    }

    private async void Refresh()
    {
        _svc.EnsureUserWidgetsRoot();
        var items = new List<WidgetGalleryItem>();
        if (SelectKind is null || SelectKind == WidgetGalleryKind.Web)
        {
            var all = _svc.GetAvailableWidgets().OrderBy(w => w.Source).ThenBy(w => w.Slug).ToList();
            items.AddRange(all.Select(w => new WidgetGalleryItem
            {
                Kind = WidgetGalleryKind.Web,
                Slug = w.Slug,
                Source = w.Source,
                SourceLabel = w.Source.ToString(),
                IsBuiltIn = w.Source == WebWidgetSource.App,
                FolderPath = w.FolderPath,
                Manifest = w.Manifest,
                DisplayName = string.IsNullOrWhiteSpace(w.Manifest.Name) ? w.Slug : w.Manifest.Name,
                DisplayAuthor = w.Manifest.Author ?? "",
                ThumbnailPath = w.ThumbnailPath,
                IsGenerating = false,
                CanPlace = true,
                SourceInfo = w,
            }));
        }
        if (SelectKind is null || SelectKind == WidgetGalleryKind.Native)
        {
            var natives = _native.GetAvailableWidgets().OrderBy(w => w.Source).ThenBy(w => w.Slug).ToList();
            items.AddRange(natives.Select(w => new WidgetGalleryItem
            {
                Kind = WidgetGalleryKind.Native,
                Slug = w.Slug,
                Source = WebWidgetSource.User,
                NativeSource = w.Source,
                SourceLabel = w.Source.ToString(),
                IsBuiltIn = w.Source == NativeWidgetSource.App,
                FolderPath = w.FolderPath,
                DisplayName = string.IsNullOrWhiteSpace(w.Manifest.Name) ? w.Slug : w.Manifest.Name,
                DisplayAuthor = w.Manifest.Author ?? "",
                ThumbnailPath = w.ThumbnailPath,
                IsGenerating = false,
                CanPlace = w.LoadError is null,
                HasError = w.LoadError is not null,
                LoadError = w.LoadError ?? "",
                NativeInfo = w,
            }));
        }
        WidgetsItems.ItemsSource = items;
        CountText.Text = $"{items.Count} widgets";

        // Generate thumbnails for widgets missing them, showing "Generating..." indicator.
        // Native widgets render offscreen via the service (trusted content only).
        foreach (var item in items.Where(v => v.ThumbnailPath == null && (v.SourceInfo is not null || v.NativeInfo is not null)).ToList())
        {
            await RefreshThumbnail(item);
        }
    }
    async Task RefreshThumbnail(WidgetGalleryItem vm, bool force = false)
    {
        if (vm.Kind == WidgetGalleryKind.Native && vm.NativeInfo is null) return;
        if (vm.Kind != WidgetGalleryKind.Native && vm.SourceInfo is null) return;
        if (vm.IsGenerating) return;
        BeginWork();
        try { await RefreshThumbnailCore(vm, force); }
        finally { EndWork(); }
    }
    async Task RefreshThumbnailCore(WidgetGalleryItem vm, bool force = false)
    {
        if (vm.Kind == WidgetGalleryKind.Native)
        {
            if (vm.NativeInfo is null) return;
            vm.IsGenerating = true;
            try
            {
                // First run compiles sources (seconds): off the UI thread so the gallery
                // stays responsive. Rendering must stay on the UI thread (STA visuals).
                vm.LoadingStatus = "Compiling plugin…";
                try { await System.Threading.Tasks.Task.Run(() => _native.GetAssemblyPath(vm.NativeInfo)); }
                catch { return; }
                vm.LoadingStatus = "Rendering preview…";
                int w = (int)Math.Clamp(vm.NativeInfo.Manifest.Width, 16, 1024);
                int h = (int)Math.Clamp(vm.NativeInfo.Manifest.Height, 16, 1024);
                var path = await _native.GenerateThumbnailAsync(vm.NativeInfo, w, h, force);
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    vm.ThumbnailPath = path;
                }
            }
            catch
            {
                // Keep null -> will show "No preview"
            }
            finally
            {
                vm.IsGenerating = false;
            }
            await System.Threading.Tasks.Task.Delay(100);
            return;
        }
        var sourceInfo = vm.SourceInfo;
        if (sourceInfo is null) return;
        vm.IsGenerating = true;
        try
        {
            vm.ThumbnailPath = null;
            var thWidth = vm.Manifest.Width ?? 480;
            var thHeight = vm.Manifest.Height ?? 300;
            var path = await _svc.GenerateThumbnailAsync(sourceInfo, width: thWidth, height: thHeight, force: force);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                vm.ThumbnailPath = path;
            }
            else
            {
                // Keep null -> will show "No preview"
            }
        }
        catch
        {
            // Keep null
        }
        finally
        {
            vm.IsGenerating = false;
        }
        // Small delay to avoid flooding WebView2 with concurrent captures
        await System.Threading.Tasks.Task.Delay(100);
    }
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        try { _native.Refresh(); } catch { } // unload cached plugin assemblies: edited sources recompile on next load
        Refresh();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        var w = new WidgetDataWindow(null, isNew: true);
        if (w.ShowDialog() == true) Refresh();
    }

    private async void RegenerateThumbnail_Click(object sender, RoutedEventArgs e)
    {
        //if (IsWorking) return;
        if (sender is FrameworkElement fe && fe.Tag is WidgetGalleryItem vm)
        {
            await RefreshThumbnail(vm, force: true);
        }
    }
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        if (sender is FrameworkElement fe && fe.Tag is WidgetGalleryItem vm)
        {
            if (vm.Kind == WidgetGalleryKind.Native)
            {
                // Native widgets are code: open the folder instead of the HTML editor.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vm.FolderPath) { UseShellExecute = true }); }
                catch (System.Exception ex)
                {
                    await _dialogs.ShowMessageAsync(ex.Message, "Widgets");
                }
                return;
            }
            if (vm.Source == WebWidgetSource.App)
            {
                await _dialogs.ShowMessageAsync("App widgets are read-only. Duplicate to edit.", "Widgets");
                return;
            }
            var w = new WidgetDataWindow(vm.Slug, isNew: false);
            if (w.ShowDialog() == true) await RefreshThumbnail(vm, force: true);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;

        if (sender is FrameworkElement fe2 && fe2.Tag is WidgetGalleryItem vm2)
        {
            if (vm2.Kind == WidgetGalleryKind.Native)
            {
                if (vm2.IsBuiltIn)
                {
                    await _dialogs.ShowMessageAsync("Cannot delete built-in widgets.", "Widgets");
                    return;
                }
                var confirmedNative = await _dialogs.ShowConfirmDeleteAsync($"Delete native widget '{vm2.Slug}'? This cannot be undone.");
                if (!confirmedNative) return;
                try { _native.DeleteWidget(vm2.Slug); } catch (System.Exception ex) { await _dialogs.ShowMessageAsync(ex.Message, "Widgets"); return; }
                Refresh();
                return;
            }
            if (vm2.Source == WebWidgetSource.App)
            {
                await _dialogs.ShowMessageAsync("Cannot delete built-in widgets.", "Widgets");
                return;
            }
            var confirmed = await _dialogs.ShowConfirmDeleteAsync($"Delete widget '{vm2.Slug}'? This cannot be undone.");
            if (!confirmed) return;

            _svc.DeleteUserWidget(vm2.Slug);
            Refresh();
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        if (sender is FrameworkElement fe && fe.Tag is WidgetGalleryItem vm)
        {
            if (vm.Kind != WidgetGalleryKind.Web) return;
            try
            {
                var info = _svc.TryGetWidget(vm.Slug, vm.Source);
                if (info is null) return;
                var html = System.IO.File.Exists(info.HtmlPath) ? System.IO.File.ReadAllText(info.HtmlPath) : "";
                var css = System.IO.File.Exists(info.CssPath) ? System.IO.File.ReadAllText(info.CssPath) : "";
                var js = System.IO.File.Exists(info.JsPath) ? System.IO.File.ReadAllText(info.JsPath) : "";
                // CreateUserWidget handles unique slug (adds numeric suffix if needed)
                _svc.CreateUserWidget(vm.Slug + "_copy", html, css, js, info.Manifest);
                Refresh();
            }
            catch (System.Exception ex)
            {
                ex.Log_Error();
                System.Diagnostics.Debug.WriteLine($"Duplicate failed: {ex}");
            }
        }
    }

    private async void Place_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        if (sender is FrameworkElement fe3 && fe3.Tag is WidgetGalleryItem vm3)
        {
            if (vm3.Kind == WidgetGalleryKind.Native)
            {
                var native = _native.TryGetWidget(vm3.Slug, vm3.NativeSource);
                if (native is null) return;
                if (native.LoadError is not null)
                {
                    await _dialogs.ShowMessageAsync(native.LoadError, "Widgets");
                    return;
                }
                if (!await ConfirmNativeTrustAsync(native)) return;
                SelectedNativeInfo = native;
                DialogResult = true;
                Close();
                return;
            }
            var info = _svc.TryGetWidget(vm3.Slug, vm3.Source);
            if (info is not null)
            {
                SelectedInfo = info;
                DialogResult = true;
                Close();
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (IsWorking) return;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Never strand an in-flight generation/compilation: its continuations touch this
        // window's controls. Backstop for X/Alt+F4 (the Close button is disabled anyway).
        // App shutdown always wins so exit can never hang here.
        if (IsWorking && Application.Current?.Dispatcher.HasShutdownStarted != true)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Trust-on-first-use consent: plugins are full-trust code, so placing a user
    /// widget asks once per content hash. Built-ins skip the prompt (shipped with the app).
    /// Declining aborts placement; editing the plugin re-arms the prompt.</summary>
    private async Task<bool> ConfirmNativeTrustAsync(NativeWidgetInfo native)
    {
        try
        {
            if (_native.IsTrusted(native)) return true;
        }
        catch { return false; }
        var confirmed = await _dialogs.ShowConfirmAsync(
            $"Native widget '{native.Slug}' runs plugin code with your full privileges.\n\n" +
            $"Only proceed if you trust where it came from.\nFolder: {native.FolderPath}",
            new DialogOptions
            {
                Title = "Trust this widget?",
                PrimaryButtonText = "Trust & Place",
                CloseButtonText = "Cancel",
            });
        if (!confirmed) return false;
        try { _native.Trust(native); }
        catch { return false; }
        return true;
    }

}

public sealed class WidgetGalleryItem : INotifyPropertyChanged
{
    public WidgetGalleryKind Kind { get; set; } = WidgetGalleryKind.Web;
    public string Slug { get; set; } = "";
    public WebWidgetSource Source { get; set; }
    public NativeWidgetSource NativeSource { get; set; } = NativeWidgetSource.User;
    public string SourceLabel { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public string FolderPath { get; set; } = "";
    public WebWidgetManifest Manifest { get; set; } = new();
    public string DisplayName { get; set; } = "";
    public string DisplayAuthor { get; set; } = "";
    public WebWidgetInfo? SourceInfo { get; set; }
    public NativeWidgetInfo? NativeInfo { get; set; }
    public bool CanPlace { get; set; } = true;
    public bool HasError { get; set; }
    public string LoadError { get; set; } = "";

    private string? _thumbnailPath;
    // Decoded bitmap cache: bindings re-evaluate the getter often, and a fresh
    // BitmapImage decode per evaluation is a gallery-wide decode storm. Any (re)set of
    // the path — including same-path regeneration — drops the cache for a single re-decode.
    private BitmapImage? _thumbnailBitmap;
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath != value)
            {
                _thumbnailPath = value;
                OnPropertyChanged();
            }
            _thumbnailBitmap = null;
            OnPropertyChanged(nameof(ThumbnailBitmap));
        }
    }

    public BitmapImage? ThumbnailBitmap
    {
        get
        {
            if (_thumbnailBitmap != null)
            {
                return _thumbnailBitmap;
            }

            if (string.IsNullOrEmpty(_thumbnailPath) || !System.IO.File.Exists(_thumbnailPath))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_thumbnailPath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // Regenerated thumbnails reuse the same file path: without this flag WPF
                // serves the previously decoded bits from its URI cache and the gallery
                // keeps showing the stale image after Refresh/Regenerate.
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 240;
                bitmap.EndInit();
                bitmap.Freeze();
                _thumbnailBitmap = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        set { if (_isGenerating != value) { _isGenerating = value; OnPropertyChanged(); } }
    }

    private string _loadingStatus = "Generating thumbnail...";
    public string LoadingStatus
    {
        get => _loadingStatus;
        set { if (_loadingStatus != value) { _loadingStatus = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
