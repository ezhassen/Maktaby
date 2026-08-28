using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace DesktopBoxesUI.Views;

public partial class WidgetsListWindow : FluentWindow
{
    private readonly ICssWidgetService _svc;
    public bool IsSelectMode { get; }
    public CssWidgetInfo? SelectedInfo { get; private set; }

    public WidgetsListWindow(bool selectMode = false)
    {
        InitializeComponent();
        IsSelectMode = selectMode;
        _svc = App.Services.GetRequiredService<ICssWidgetService>();
        DataContext = this;
        Loaded += (_, _) => Refresh();
        if (IsSelectMode) SelectButton.Visibility = Visibility.Visible;
    }

    private async void Refresh()
    {
        _svc.EnsureUserWidgetsRoot();
        var all = _svc.GetAvailableWidgets().OrderBy(w => w.Source).ThenBy(w => w.Slug).ToList();
        var view = all.Select(w => new WidgetGalleryItem
        {
            Slug = w.Slug,
            Source = w.Source,
            FolderPath = w.FolderPath,
            Manifest = w.Manifest,
            ThumbnailPath = w.ThumbnailPath,
            IsGenerating = false,
            SourceInfo = w
        }).ToList();
        WidgetsItems.ItemsSource = view;
        CountText.Text = $"{all.Count} widgets";

        // Generate thumbnails for widgets missing them, showing "Generating..." indicator
        foreach (var item in view.Where(v => v.ThumbnailPath == null).ToList())
        {
            await RefreshThumbnail(item);
        }
    }
    async Task RefreshThumbnail(WidgetGalleryItem vm, bool force = false)
    {
        vm.IsGenerating = true;
        try
        {
            vm.ThumbnailPath = null;
            var thWidth = vm.Manifest.Width ?? 480;
            var thHeight = vm.Manifest.Height ?? 300;
            var path = await _svc.GenerateThumbnailAsync(vm.SourceInfo, width: thWidth, height: thHeight, force: force);
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
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var w = new WidgetDataWindow(null, isNew: true);
        if (w.ShowDialog() == true) Refresh();
    }

    private async void RegenerateThumbnail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is WidgetGalleryItem vm)
        {
            await RefreshThumbnail(vm, force: true);
        }
    }
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is WidgetGalleryItem vm)
        {
            if (vm.Source == CssWidgetSource.App)
            {
                System.Windows.MessageBox.Show("App widgets are read-only. Duplicate to edit.", "Widgets", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            var w = new WidgetDataWindow(vm.Slug, isNew: false);
            if (w.ShowDialog() == true) await RefreshThumbnail(vm, force: true);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe2 && fe2.Tag is WidgetGalleryItem vm2)
        {
            if (vm2.Source == CssWidgetSource.App)
            {
                System.Windows.MessageBox.Show("Cannot delete built-in widgets.", "Widgets", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (System.Windows.MessageBox.Show($"Delete widget '{vm2.Slug}'? This cannot be undone.", "Delete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes)
            {
                _svc.DeleteUserWidget(vm2.Slug);
                Refresh();
            }
        }
    }

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe3 && fe3.Tag is WidgetGalleryItem vm3)
        {
            var info = _svc.TryGetWidget(vm3.Slug, vm3.Source);
            if (info is not null)
            {
                SelectedInfo = info;
                DialogResult = true;
                Close();
            }
        }
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = SelectedInfo is not null;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

}

public sealed class WidgetGalleryItem : INotifyPropertyChanged
{
    public string Slug { get; set; } = "";
    public CssWidgetSource Source { get; set; }
    public string FolderPath { get; set; } = "";
    public CssWidgetManifest Manifest { get; set; } = new();
    public CssWidgetInfo SourceInfo { get; set; } = null!;

    private string? _thumbnailPath;
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set { if (_thumbnailPath != value) { _thumbnailPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThumbnailBitmap)); } }
    }

    public BitmapImage? ThumbnailBitmap
    {
        get
        {
            if (string.IsNullOrEmpty(_thumbnailPath) || !System.IO.File.Exists(_thumbnailPath))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_thumbnailPath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 240;
                bitmap.EndInit();
                bitmap.Freeze();
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
