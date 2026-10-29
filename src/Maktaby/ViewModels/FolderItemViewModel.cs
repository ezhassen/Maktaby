using Maktaby.WPFServices;
using System.IO;

namespace Maktaby.ViewModels;

/// <summary>View-model for a single filesystem entry inside a FolderPortal box.</summary>
public sealed class FolderItemViewModel : ViewModelBase
{
    private readonly IconImageService _icons;
    private readonly string _path;
    private bool _isDirectory;
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconLoadRequested;

    private int _iconSize = 32;

    /// <summary>
    /// The pixel size this row's icon has to be sharp at. Setting it re-resolves the icon.
    /// </summary>
    /// <remarks>
    /// The shell only returns 32 or 48 px icons, so a row drawn at 96 px was an upscale - the blur
    /// the large icon size showed. Above that the icon is rendered at the requested size instead.
    /// </remarks>
    public int IconSize
    {
        get => _iconSize;
        set
        {
            int normalized = value < 16 ? 16 : (value > 256 ? 256 : value);
            if (normalized == _iconSize)
            {
                return;
            }

            _iconSize = normalized;
            ReloadIconAtCurrentSize();
        }
    }

    private void ReloadIconAtCurrentSize()
    {
        _iconLoadRequested = false;
        _icon = null;
        OnPropertyChanged(nameof(Icon));
        EnsureIconLoaded();
    }
    private bool _isSelected;
    private bool _isFilteredOut;
    private bool _isEditing;
    private string _renameText = string.Empty;
    private string _displayName;

    //
    // Metadata is DATA, not a live lookup. The details view binds SizeText/ModifiedText on the UI
    // thread, and the Date/Size sort reads both per row, so the old live getters (FileInfo.Length,
    // File.GetLastWriteTime) issued a file-system call per binding evaluation and per sort key -
    // measured at ~35 us each, i.e. ~0.7 s per pass over 20k rows, on top of the same information
    // the folder enumeration had already read for free.
    //
    // The caller that enumerates supplies these values (from one DirectoryInfo pass), and the two
    // paths that learn about a change without re-enumerating refresh them explicitly:
    // ApplyMetadata (a reused row in ApplyFolderItems) and RefreshMetadata (a single Changed event).
    //
    private long _fileSize;
    private DateTime _modifiedTime;

    public FolderItemViewModel(
        string path,
        IconImageService icons,
        int iconSize,
        bool isDirectory,
        long fileSize,
        DateTime modifiedTime)
    {
        _path = path;
        _iconSize = iconSize < 16 ? 16 : (iconSize > 256 ? 256 : iconSize);
        _icons = icons;
        _isDirectory = isDirectory;
        _fileSize = fileSize;
        _modifiedTime = modifiedTime;
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
    /// <summary>File length as of the last enumeration (always 0 for a directory).</summary>
    public long FileSize => _isDirectory ? 0 : _fileSize;

    /// <summary>Last-write time as of the last enumeration.</summary>
    public DateTime ModifiedTime => _modifiedTime;

    public string ModifiedText => ModifiedTime == DateTime.MinValue ? string.Empty : ModifiedTime.ToString("g");
    public string SizeText => _isDirectory ? string.Empty : FormatSize(FileSize);

    /// <summary>
    /// Adopts freshly enumerated metadata for this path, keeping the view-model instance (and its
    /// already-loaded icon) alive.
    /// </summary>
    /// <remarks>
    /// <c>ApplyFolderItems</c> reuses the row of a surviving path; without this the row would keep
    /// the size/date it was first created with, because those values are no longer live getters.
    /// </remarks>
    public void ApplyMetadata(bool isDirectory, long fileSize, DateTime modifiedTime)
    {
        if (_isDirectory == isDirectory && _fileSize == fileSize && _modifiedTime == modifiedTime)
        {
            return;
        }

        _isDirectory = isDirectory;
        _fileSize = fileSize;
        _modifiedTime = modifiedTime;

        OnPropertyChanged(nameof(IsDirectory));
        OnPropertyChanged(nameof(TypeName));
        OnPropertyChanged(nameof(FileSize));
        OnPropertyChanged(nameof(ModifiedTime));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ModifiedText));
    }

    /// <summary>
    /// Re-reads this one row's metadata from disk.
    /// </summary>
    /// <remarks>
    /// For the "this file changed" watcher event, which knows the path but not the new size/date.
    /// Exactly one metadata call, on one row - the whole point of caching it is that this is the
    /// only place a row pays for a stat outside a full enumeration.
    /// </remarks>
    public void RefreshMetadata()
    {
        try
        {
            FileSystemInfo info = _isDirectory ? new DirectoryInfo(_path) : new FileInfo(_path);
            info.Refresh();
            var attrs = info.Attributes;
            bool isDirectory = (attrs & FileAttributes.Directory) != 0;
            long length = isDirectory ? 0 : info is FileInfo file ? file.Length : 0;
            ApplyMetadata(isDirectory, length, info.LastWriteTime);
        }
        catch { }
    }

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

    /// <summary>True when the portal is filtered and this row does not match the text.</summary>
    /// <remarks>A property rather than a filtered collection, so every index in
    /// <c>FolderItems</c> - which selection, focus and scrolling all address - stays valid.</remarks>
    public bool IsFilteredOut { get => _isFilteredOut; set { if (_isFilteredOut != value) { _isFilteredOut = value; OnPropertyChanged(); } } }

    private bool _isFilterExempt;
    /// <summary>
    /// Shown even when it does not match the filter - set when the row arrives (a paste) while a
    /// filter is active, so the files the user just pasted cannot vanish on landing. Dropped by
    /// <see cref="BoxViewModel"/> as soon as the filter text changes, kept across a refresh.
    /// </summary>
    public bool IsFilterExempt { get => _isFilterExempt; set { if (_isFilterExempt != value) { _isFilterExempt = value; OnPropertyChanged(); } } }
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
            var src = await _icons.GetIconAsync(_path, _iconSize).ConfigureAwait(false);
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
