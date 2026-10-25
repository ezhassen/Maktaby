using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.LiveWallpaper;
using Maktaby.Localization;
using Maktaby.Settings;
using Maktaby.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Maktaby.Views.AppWindows;

/// <summary>
/// Lets the user pick a live wallpaper per monitor. Each monitor keeps its own assignment
/// (device name &#8594; file path); monitors without an explicit assignment fall back to the
/// single global ("*") wallpaper path. Assignments are applied automatically on change.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public partial class WallpaperPerMonitorWindow : AppFluentWindow, INotifyPropertyChanged
{
    private readonly IMonitorService _monitor;
    private readonly ISettingsService _settings;
    private readonly LiveWallpaperManager _wallpaper;
    private readonly ObservableCollection<MonitorRow> _rows = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public sealed class MonitorRow : INotifyPropertyChanged
    {
        public string DeviceName { get; set; } = string.Empty;
        public bool IsConnected { get; set; } = true;

        private string _path = string.Empty;
        public string Path
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value;
                OnPropertyChanged(nameof(Path));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(FileExists));
                OnPropertyChanged(nameof(HasWarning));
                OnPropertyChanged(nameof(WarningText));
                OnPropertyChanged(nameof(IsVideo));
                OnPropertyChanged(nameof(IsWeb));
                OnPropertyChanged(nameof(MediaType));
                OnPropertyChanged(nameof(HasMedia));
            }
        }

        public string DisplayPath => string.IsNullOrWhiteSpace(Path) ? "(none)" : System.IO.Path.GetFileName(Path);

        private ImageSource? _preview;
        public ImageSource? Preview
        {
            get => _preview;
            set
            {
                if (ReferenceEquals(_preview, value)) return;
                _preview = value;
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(HasMedia));
            }
        }

        public bool FileExists => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
        public bool HasWarning => !string.IsNullOrWhiteSpace(Path) && !File.Exists(Path);
        public string WarningText => HasWarning ? Loc.Get("WallpaperPerMonitor.FileNotFound") : string.Empty;
        public bool IsVideo => !string.IsNullOrWhiteSpace(Path) && WallpaperPath.IsVideoPath(Path);
        public bool IsWeb => !string.IsNullOrWhiteSpace(Path) && WallpaperPath.IsWebPath(Path);
        public bool IsGif => !string.IsNullOrWhiteSpace(Path) && Path.EndsWith("gif", StringComparison.OrdinalIgnoreCase);
        public string MediaType => IsVideo ? Loc.Get("WallpaperPerMonitor.Video") : IsWeb ? Loc.Get("WallpaperPerMonitor.Web") : IsGif ? Loc.Get("WallpaperPerMonitor.Gif") : Loc.Get("WallpaperPerMonitor.Image");
        public bool HasMedia => !string.IsNullOrWhiteSpace(Path);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // "*" (all monitors) default wallpaper
    private string _defaultPath = string.Empty;
    public string DefaultPath
    {
        get => _defaultPath;
        set
        {
            if (_defaultPath == value) return;
            _defaultPath = value;
            OnPropertyChanged(nameof(DefaultPath));
            OnPropertyChanged(nameof(DefaultDisplayPath));
            OnPropertyChanged(nameof(DefaultFileExists));
            OnPropertyChanged(nameof(DefaultHasWarning));
            OnPropertyChanged(nameof(DefaultWarningText));
            OnPropertyChanged(nameof(DefaultIsVideo));
            OnPropertyChanged(nameof(DefaultIsWeb));
            OnPropertyChanged(nameof(DefaultMediaType));
            OnPropertyChanged(nameof(DefaultHasMedia));
        }
    }

    public string DefaultDisplayPath => string.IsNullOrWhiteSpace(DefaultPath) ? "(none)" : System.IO.Path.GetFileName(DefaultPath);

    private ImageSource? _defaultPreview;
    public ImageSource? DefaultPreview
    {
        get => _defaultPreview;
        set
        {
            if (ReferenceEquals(_defaultPreview, value)) return;
            _defaultPreview = value;
            OnPropertyChanged(nameof(DefaultPreview));
        }
    }

    public bool DefaultFileExists => !string.IsNullOrWhiteSpace(DefaultPath) && File.Exists(DefaultPath);
    public bool DefaultHasWarning => !string.IsNullOrWhiteSpace(DefaultPath) && !File.Exists(DefaultPath);
    public string DefaultWarningText => DefaultHasWarning ? Loc.Get("WallpaperPerMonitor.FileNotFound") : string.Empty;
    public bool DefaultIsVideo => !string.IsNullOrWhiteSpace(DefaultPath) && WallpaperPath.IsVideoPath(DefaultPath);
    public bool DefaultIsWeb => !string.IsNullOrWhiteSpace(DefaultPath) && WallpaperPath.IsWebPath(DefaultPath);
    public string DefaultMediaType => DefaultIsVideo ? Loc.Get("WallpaperPerMonitor.Video") : DefaultIsWeb ? Loc.Get("WallpaperPerMonitor.Web") : Loc.Get("WallpaperPerMonitor.Image");
    public bool DefaultHasMedia => !string.IsNullOrWhiteSpace(DefaultPath);

    public WallpaperPerMonitorWindow()
    {
        InitializeComponent();
        DataContext = this;
        _monitor = App.Services.GetRequiredService<IMonitorService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _wallpaper = App.Services.GetRequiredService<LiveWallpaperManager>();
        _wallpaper.SettingsChanged += OnWallpaperSettingsChanged;
        Loc.LanguageChanged += OnLanguageChanged;

        Loaded += (_, _) => Reload();
        Closing += (_, _) =>
        {
            _wallpaper.SettingsChanged -= OnWallpaperSettingsChanged;
            Loc.LanguageChanged -= OnLanguageChanged;
            foreach (var media in FindVisualChildren<MediaElement>(this))
                StopMedia(media);
            foreach (var wv in FindVisualChildren<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>(this))
            {
                try { wv.CoreWebView2?.Navigate("about:blank"); } catch { }
                try { wv.Dispose(); } catch { }
            }
            SaveAssignments();
        };
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Reload()
    {
        foreach (var media in FindVisualChildren<MediaElement>(this)) StopMedia(media);
        foreach (var wv in FindVisualChildren<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>(this)) wv.Visibility = Visibility.Collapsed;
        foreach (var img in FindVisualChildren<Image>(this)) img.Visibility = Visibility.Visible;
        _rows.Clear();
        try
        {
            var monitors = _monitor.GetAllMonitors();
            var current = CurrentAssignments(_settings);
            var connected = new HashSet<string>(monitors.Select(m => m.DeviceName), StringComparer.OrdinalIgnoreCase);

            foreach (var m in monitors.OrderBy(m => m.IsPrimary ? 0 : 1).ThenBy(m => m.Bounds.X))
            {
                var path = current.TryGetValue(m.DeviceName, out var p) ? p : string.Empty;
                _rows.Add(new MonitorRow
                {
                    DeviceName = m.DeviceName,
                    IsConnected = true,
                    Path = path
                });
            }

            // Presaved (not-connected) monitors.
            foreach (var kv in current)
            {
                if (connected.Contains(kv.Key)) continue;
                _rows.Add(new MonitorRow
                {
                    DeviceName = kv.Key,
                    IsConnected = false,
                    Path = kv.Value ?? string.Empty
                });
            }
        }
        catch
        {
            // Monitors unavailable: show an empty list rather than crashing.
        }

        // Load default ("*") preview
        var globalPath = _settings.UserSettings.LiveWallpaperPath;
        DefaultPath = string.IsNullOrWhiteSpace(globalPath) ? string.Empty : globalPath!;
        DefaultPreview = string.IsNullOrWhiteSpace(globalPath) ? null :
            (File.Exists(globalPath) ? LoadPreviewSync(globalPath!) : null);

        MonitorList.ItemsSource = _rows;
        _ = LoadPreviewsAsync();
    }

    private static ImageSource? LoadPreviewSync(string path)
    {
        if (!File.Exists(path)) return null;
        if (WallpaperPath.IsImagePath(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 168;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
        try
        {
            IntPtr hbitmap = WallpaperPath.IsVideoPath(path)
                ? Win32Apis.GetThumbnailBitmapForPath(path, 128)
                : Win32Apis.GetIconBitmapForPath(path, 128);
            if (hbitmap == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            Win32Apis.DeleteObject(hbitmap);
            return source;
        }
        catch { return null; }
    }

    private static Dictionary<string, string> CurrentAssignments(ISettingsService settings)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var existing = settings.UserSettings.LiveWallpaperAssignments;
            if (existing != null)
            {
                foreach (var a in existing) dict[a.Monitor] = a.Path;
            }
        }
        catch { }
        return dict;
    }

    private async Task LoadPreviewsAsync()
    {
        foreach (var row in _rows)
        {
            row.Preview = await LoadPreviewAsync(row.Path);
        }
    }

    private static async Task<ImageSource?> LoadPreviewAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        if (WallpaperPath.IsImagePath(path))
        {
            return await Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 168;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch { return null; }
            });
        }

        return await Task.Run(() =>
        {
            IntPtr hbitmap = WallpaperPath.IsVideoPath(path)
                ? Win32Apis.GetThumbnailBitmapForPath(path, 128)
                : Win32Apis.GetIconBitmapForPath(path, 128);
            if (hbitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                Win32Apis.DeleteObject(hbitmap);
            }
        });
    }

    private async Task UpdateRowPreviewAsync(MonitorRow row)
    {
        row.Preview = await LoadPreviewAsync(row.Path);
    }

    // Default row handlers
    private void DefaultBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var owner = new WindowInteropHelper(this).Handle;
        var newFile = WallpaperFilePicker.PickMedia(owner);
        if (string.IsNullOrEmpty(newFile) || !File.Exists(newFile)) return;
        _settingsChangedByThis = true;

        // Stop previous playback.
        StopMedia(DefaultVideoPlayer);
        DefaultPlayPauseButton.Tag = false;
DefaultPlayPauseButton.Content = Loc.Get("WallpaperPerMonitor.Play");
        DefaultPath = newFile;
        _settings.UserSettings.LiveWallpaperPath = newFile;
        _settings.Save();
        DefaultPreview = LoadPreviewSync(newFile);
        // Stop previous.
        StopMedia(DefaultVideoPlayer);
        StopWebView(DefaultWebView);
        DefaultImage.Visibility = Visibility.Visible;

        if (WallpaperPath.IsVideoPath(newFile))
        {
            DefaultVideoPlayer.Source = null;
            DefaultVideoPlayer.Visibility = Visibility.Collapsed;
            DefaultPlayPauseButton.Tag = false;
DefaultPlayPauseButton.Content = Loc.Get("WallpaperPerMonitor.Play");
            DefaultPlayPauseButton.Visibility = Visibility.Visible;
            DefaultStopButton.Visibility = Visibility.Visible;
        }
        else if (WallpaperPath.IsWebPath(newFile))
        {
            DefaultImage.Visibility = Visibility.Collapsed;
            DefaultWebView.Visibility = Visibility.Visible;
            _ = InitWebViewAsync(DefaultWebView, newFile);
        }
        else
        {
            DefaultVideoPlayer.Source = null;
            DefaultVideoPlayer.Visibility = Visibility.Collapsed;
            DefaultImage.Visibility = Visibility.Visible;
        }
        _ = _wallpaper.SetWallpaperAsync(newFile);
        _settingsChangedByThis = false;
    }

    private void DefaultRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsChangedByThis = true;
        StopMedia(DefaultVideoPlayer);
        DefaultPlayPauseButton.Tag = false;
DefaultPlayPauseButton.Content = Loc.Get("WallpaperPerMonitor.Play");
        DefaultPath = string.Empty;
        _settings.UserSettings.LiveWallpaperPath = null;
        _settings.Save();
        DefaultPreview = null;
        _ = _wallpaper.RemoveAsync();
        _settingsChangedByThis = false;
    }

    // Monitor row handlers
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MonitorRow row) return;
        var owner = new WindowInteropHelper(this).Handle;
        var newFile = WallpaperFilePicker.PickMedia(owner);
        if (string.IsNullOrEmpty(newFile) || !File.Exists(newFile)) return;

        // Stop previous playback for this thumbnail.
        var border = FindVisualParent<Border>(button);
        var media = border is null ? null : FindVisualChild<MediaElement>(border);
        var playButton = border is null ? null : FindVisualChild<Wpf.Ui.Controls.Button>(border);
        if (media is not null) StopMedia(media);
        var rowWebViewEarly = border is null ? null : FindVisualChild<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>(border);
        if (rowWebViewEarly is not null) StopWebView(rowWebViewEarly);
        if (playButton is not null) { playButton.Tag = false; playButton.Content = Loc.Get("WallpaperPerMonitor.Play"); }

        row.Path = newFile;
        _ = UpdateRowPreviewAsync(row);
        // Persist + auto-apply
        SaveAssignments();
        _settingsChangedByThis = true;
        if (row.IsConnected)
        {
            _ = _wallpaper.SetWallpaperForMonitorAsync(row.DeviceName, newFile);
        }
        _settingsChangedByThis = false;

        // Auto-play if video.
        if (media is not null && playButton is not null && border is not null)
        {
            var rowImage = FindVisualChild<Image>(border);
            var rowWebView = FindVisualChild<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>(border);
            if (WallpaperPath.IsVideoPath(newFile))
            {
                if (rowWebView is not null) rowWebView.Visibility = Visibility.Collapsed;
                if (rowImage is not null) rowImage.Visibility = Visibility.Visible;
                media.Source = null;
                media.Visibility = Visibility.Collapsed;
                playButton.Tag = false;
                playButton.Content = Loc.Get("WallpaperPerMonitor.Play");
            }
            else if (WallpaperPath.IsWebPath(newFile))
            {
                StopMedia(media);
                if (rowImage is not null) rowImage.Visibility = Visibility.Collapsed;
                media.Visibility = Visibility.Collapsed;
                if (rowWebView is not null)
                {
                    rowWebView.Visibility = Visibility.Visible;
                    _ = InitWebViewAsync(rowWebView, newFile);
                }
            }
            else
            {
                StopMedia(media);
                if (rowWebView is not null) rowWebView.Visibility = Visibility.Collapsed;
                if (rowImage is not null) rowImage.Visibility = Visibility.Visible;
            }
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MonitorRow row) return;
        var border = FindVisualParent<Border>(button);
        var media = border is null ? null : FindVisualChild<MediaElement>(border);
        var webView = border is null ? null : FindVisualChild<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>(border);
        var rowImage = border is null ? null : FindVisualChild<Image>(border);
        var playButton = border is null ? null : FindVisualChild<Wpf.Ui.Controls.Button>(border);
        if (media is not null) StopMedia(media);
        if (webView is not null) webView.Visibility = Visibility.Collapsed;
        if (rowImage is not null) rowImage.Visibility = Visibility.Visible;
        if (playButton is not null) { playButton.Tag = false; playButton.Content = Loc.Get("WallpaperPerMonitor.Play"); }

        row.Path = string.Empty;
        row.Preview = null;
        SaveAssignments();
        _settingsChangedByThis = true;
        if (row.IsConnected)
        {
            _ = _wallpaper.ClearWallpaperForMonitorAsync(row.DeviceName);
        }
        _settingsChangedByThis = false;
    }

    private void SaveAssignments()
    {
        _settingsChangedByThis = true;
        var assignments = CurrentAssignments(_settings);
        foreach (var row in _rows)
        {
            if (string.IsNullOrWhiteSpace(row.Path)) assignments.Remove(row.DeviceName);
            else assignments[row.DeviceName] = row.Path;
        }
        try
        {
            var list = assignments.Select(kv => new Maktaby.Settings.LiveWallpaperMonitorAssignment
            {
                Monitor = kv.Key,
                Path = kv.Value
            }).ToList();
            _settings.UserSettings.LiveWallpaperAssignments = list.Count > 0 ? list : null;
            _settings.Save();
        }
        catch { }
        _settingsChangedByThis = false;
    }

    private void DefaultPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        bool isPlaying = btn.Tag is true;
        if (!isPlaying)
        {
            if (string.IsNullOrWhiteSpace(DefaultPath) || !File.Exists(DefaultPath)) return;
            DefaultImage.Visibility = Visibility.Collapsed;
            if (DefaultVideoPlayer.Source == null)
                DefaultVideoPlayer.Source = new Uri(DefaultPath);
            DefaultVideoPlayer.Visibility = Visibility.Visible;
            try { DefaultVideoPlayer.Play(); } catch { }
            btn.Tag = true;
            btn.Content = Loc.Get("WallpaperPerMonitor.Pause");
        }
        else
        {
            DefaultVideoPlayer.Pause();
            btn.Tag = false;
            btn.Content = Loc.Get("WallpaperPerMonitor.Play");
        }
    }

    private void DefaultStop_Click(object sender, RoutedEventArgs e)
    {
        StopMedia(DefaultVideoPlayer);
        DefaultImage.Visibility = Visibility.Visible;
        DefaultPlayPauseButton.Tag = false;
DefaultPlayPauseButton.Content = Loc.Get("WallpaperPerMonitor.Play");
    }

    private void RowPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var border = FindVisualParent<Border>(btn);
        var media = border is null ? null : FindVisualChild<MediaElement>(border);
        if (media is null) return;
        var row = btn.DataContext as MonitorRow;
        if (row is null || string.IsNullOrWhiteSpace(row.Path) || !File.Exists(row.Path)) return;

        bool isPlaying = btn.Tag is true;
        if (!isPlaying)
        {
            var rowImage = border is null ? null : FindVisualChild<Image>(border);
            if (rowImage is not null) rowImage.Visibility = Visibility.Collapsed;
            if (media.Source == null)
                media.Source = new Uri(row.Path);
            media.Visibility = Visibility.Visible;
            try { media.Play(); } catch { }
            btn.Tag = true;
            btn.Content = Loc.Get("WallpaperPerMonitor.Pause");
        }
        else
        {
            media.Pause();
            btn.Tag = false;
            btn.Content = Loc.Get("WallpaperPerMonitor.Play");
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindVisualChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typed) return typed;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static void StopMedia(MediaElement media)
    {
        try
        {
            media.Stop();
            media.Close();
            media.Source = null;
            media.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private static void StopWebView(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl webView)
    {
        try
        {
            webView.Visibility = Visibility.Collapsed;
            webView.CoreWebView2?.Navigate("about:blank");
        }
        catch { }
    }
    bool _settingsChangedByThis = false;
    private void OnWallpaperSettingsChanged(object? sender, EventArgs e) { if (!_settingsChangedByThis) Dispatcher.BeginInvoke(Reload); }
    private void OnLanguageChanged() => Dispatcher.BeginInvoke(Reload);

    private async Task InitWebViewAsync(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl webView, string path)
    {
        try
        {
            await webView.EnsureCoreWebView2Async(null);
            string target = path;
            if (Directory.Exists(path))
            {
                var index = System.IO.Path.Combine(path, "index.html");
                if (File.Exists(index)) target = index;
            }
            webView.CoreWebView2.Navigate(new Uri(target).AbsoluteUri);
        }
        catch { }
    }

    private void RowStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var border = FindVisualParent<Border>(button);
        var media = border is null ? null : FindVisualChild<MediaElement>(border);
        var webView = border is null ? null : FindVisualChild<Microsoft.Web.WebView2.Wpf.WebView2CompositionControl>(border);
        var rowImage = border is null ? null : FindVisualChild<Image>(border);
        var playButton = border is null ? null : FindVisualChild<Wpf.Ui.Controls.Button>(border);
        if (media is not null) StopMedia(media);
        if (webView is not null) webView.Visibility = Visibility.Collapsed;
        if (rowImage is not null) rowImage.Visibility = Visibility.Visible;
        if (playButton is not null) { playButton.Tag = false; playButton.Content = Loc.Get("WallpaperPerMonitor.Play"); }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}