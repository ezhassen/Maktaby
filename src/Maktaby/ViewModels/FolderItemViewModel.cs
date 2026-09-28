using Maktaby.Core.Services;
using Maktaby.WPFServices;
using System.IO;

namespace Maktaby.ViewModels;

/// <summary>View-model for a single filesystem entry inside a FolderPortal box.</summary>
public sealed class FolderItemViewModel : ViewModelBase
{
    private readonly IconImageService _icons;
    private readonly string _path;
    private readonly bool _isDirectory;
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconLoadRequested;
    private bool _isSelected;
    private bool _isEditing;
    private string _renameText = string.Empty;
    private string _displayName;

    public FolderItemViewModel(string path, IconImageService icons)
    {
        _path = path;
        _icons = icons;
        _isDirectory = Directory.Exists(path);
        _displayName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(_displayName)) _displayName = path;
        _renameText = _displayName;
    }

    public string Path => _path;
    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }
    public bool IsDirectory => _isDirectory;
    public string Extension => System.IO.Path.GetExtension(_path);
    public string TypeName => _isDirectory ? "Folder" : (string.IsNullOrEmpty(Extension) ? "File" : Extension.TrimStart('.').ToUpperInvariant() + " File");
    public long FileSize
    {
        get
        {
            if (_isDirectory) return 0;
            try { return new FileInfo(_path).Length; } catch { return 0; }
        }
    }
    public DateTime ModifiedTime
    {
        get
        {
            try { return _isDirectory ? Directory.GetLastWriteTime(_path) : File.GetLastWriteTime(_path); } catch { return DateTime.MinValue; }
        }
    }
    public string ModifiedText => ModifiedTime == DateTime.MinValue ? string.Empty : ModifiedTime.ToString("g");
    public string SizeText => _isDirectory ? string.Empty : FormatSize(FileSize);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.#") + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.#") + " MB";
        double gb = mb / 1024.0;
        return gb.ToString("0.#") + " GB";
    }

    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } } }
    public bool IsEditing { get => _isEditing; set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(); } } }
    public string RenameText { get => _renameText; set { if (_renameText != value) { _renameText = value; OnPropertyChanged(); } } }

    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            EnsureIconLoaded();
            return _icon;
        }
    }

    private void EnsureIconLoaded()
    {
        if (_iconLoadRequested) return;
        _iconLoadRequested = true;
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        try
        {
            var src = await _icons.GetIconAsync(_path).ConfigureAwait(false);
            if (src != null)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() => { _icon = src; OnPropertyChanged(nameof(Icon)); });
            }
        }
        catch { }
    }

    public void ReloadIcon()
    {
        _icon = null;
        _iconLoadRequested = false;
        OnPropertyChanged(nameof(Icon));
    }
}
