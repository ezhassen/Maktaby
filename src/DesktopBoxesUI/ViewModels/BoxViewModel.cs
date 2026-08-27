using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using DesktopBoxesUI;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// View-model for a single <see cref="Box"/> (one tab inside a <see cref="BoxContainer"/>). Geometry and
/// styling live on the owning <see cref="BoxContainerViewModel"/>; this only models the box's items
/// and behaviour. The <see cref="Items"/> collection mirrors the underlying <see cref="Box.Items"/>
/// model collection (which the file-rule coordinator updates), so a single source of truth is kept.
/// For <see cref="BoxType.FolderPortal"/> the <see cref="FolderItems"/> collection is the live folder view.
/// </summary>
public sealed class BoxViewModel : ViewModelBase
{
    private readonly Box _box;
    private readonly IconImageService _icons;
    private IDisposable? _watcher; // contents of CurrentFolderPath
    private IDisposable? _folderWatcher; // parent of FolderPath to detect root delete/rename/move
    private bool _isWatching;
    private string? _currentFolderPath; // transient navigation, null = root (FolderPath)
    private readonly Stack<string> _navBack = new();
    private readonly Stack<string> _navForward = new();

    public BoxViewModel(Box box, IconImageService icons)
    {
        _box = box;
        _icons = icons;
        Items = new ObservableCollection<BoxItemViewModel>(
            box.Items.Where(i => !ShellItemFilter.IsExcluded(i.Path)).Select(i => new BoxItemViewModel(i, icons)));
        _box.Items.CollectionChanged += OnBoxItemsChanged;
        FolderItems = new ObservableCollection<FolderItemViewModel>();
        if (_box.BoxType == BoxType.FolderPortal && !string.IsNullOrWhiteSpace(_box.FolderPath))
        {
            _ = RefreshFolderAsync();
        }
    }

    public System.Guid Id => _box.Id;

    /// <summary>The underlying <see cref="Box"/> model (used when moving a box between containers).</summary>
    public Box Model => _box;

    public string Name
    {
        get => _box.Name;
        set
        {
            if (_box.Name != value)
            {
                _box.Name = value;
                OnPropertyChanged();
            }
        }
    }

    public BoxType BoxType
    {
        get => _box.BoxType;
        set
        {
            if (_box.BoxType != value)
            {
                _box.BoxType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsFolderPortal));
                OnPropertyChanged(nameof(IsDesktopItems));
            }
        }
    }

    public bool IsFolderPortal => _box.BoxType == BoxType.FolderPortal;
    public bool IsDesktopItems => _box.BoxType == BoxType.DesktopItems;

    /// <summary>True for the built-in default box (fed by the default rule; cannot be deleted).</summary>
    public bool IsDefault => _box.IsDefault;

    public ObservableCollection<BoxItemViewModel> Items { get; }

    // ----- FolderPortal -----

    public string? FolderPath
    {
        get => _box.FolderPath;
        set
        {
            if (_box.FolderPath != value)
            {
                bool wasWatching = _isWatching;
                if (wasWatching) StopWatching();
                _box.FolderPath = value;
                _currentFolderPath = value; // reset navigation to new root
                _navBack.Clear();
                _navForward.Clear();
                // Keep box name in sync with folder name for FolderPortal (unless user later renames manually)
                if (_box.BoxType == BoxType.FolderPortal && !string.IsNullOrWhiteSpace(value))
                {
                    string fName = Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(fName)) fName = value;
                    if (!string.IsNullOrEmpty(fName) && _box.Name != fName)
                    {
                        _box.Name = fName;
                        OnPropertyChanged(nameof(Name));
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(HasFolderPath));
                OnPropertyChanged(nameof(IsFolderMissing));
                OnPropertyChanged(nameof(FolderDisplayName));
                OnPropertyChanged(nameof(CurrentFolderPath));
                OnPropertyChanged(nameof(IsAtRoot));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
                _ = RefreshFolderAsync();
                if (wasWatching && !string.IsNullOrWhiteSpace(CurrentFolderPath) && Directory.Exists(CurrentFolderPath)) StartWatching();
            }
        }
    }

    public string CurrentFolderPath => _currentFolderPath ?? _box.FolderPath ?? string.Empty;

    public bool IsAtRoot => string.IsNullOrEmpty(_currentFolderPath) || string.Equals(_currentFolderPath, _box.FolderPath, StringComparison.OrdinalIgnoreCase);

    public FolderPortalViewMode FolderPortalViewMode
    {
        get => _box.FolderPortalViewMode;
        set
        {
            if (_box.FolderPortalViewMode != value)
            {
                _box.FolderPortalViewMode = value;
                OnPropertyChanged();
            }
        }
    }

    public FolderSortMode FolderSortBy
    {
        get => _box.FolderSortBy;
        set
        {
            if (_box.FolderSortBy != value)
            {
                _box.FolderSortBy = value;
                OnPropertyChanged();
                ApplyFolderSort();
            }
        }
    }

    public bool FolderSortAscending
    {
        get => _box.FolderSortAscending;
        set
        {
            if (_box.FolderSortAscending != value)
            {
                _box.FolderSortAscending = value;
                OnPropertyChanged();
                ApplyFolderSort();
            }
        }
    }

    public bool HasFolder => !string.IsNullOrWhiteSpace(_box.FolderPath) && Directory.Exists(_box.FolderPath);

    public bool HasFolderPath => !string.IsNullOrWhiteSpace(_box.FolderPath);

    public bool IsFolderMissing => HasFolderPath && !HasFolder;

    public string FolderDisplayName => HasFolder ? System.IO.Path.GetFileName(_box.FolderPath!) ?? _box.FolderPath! : string.Empty;

    public ObservableCollection<FolderItemViewModel> FolderItems { get; }

    public void SetFolderPath(string? path)
    {
        FolderPath = path;
    }

    private static bool ShouldShowEntry(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            bool isHidden = (attrs & FileAttributes.Hidden) != 0;
            bool isSystem = (attrs & FileAttributes.System) != 0;
            if (isSystem)
            {
                // ShowSuperHidden controls system + hidden files
                try
                {
                    var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden", 0);
                    int showSuper = v is int i ? i : 0;
                    if (showSuper != 1) return false;
                }
                catch { return false; }
            }
            else if (isHidden)
            {
                try
                {
                    var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2);
                    int hidden = v is int i ? i : 2;
                    if (hidden != 1) return false;
                }
                catch { return false; }
            }
            return true;
        }
        catch { return true; }
    }

    public async Task RefreshFolderAsync()
    {
        string? effective = CurrentFolderPath;
        if (_box.BoxType != BoxType.FolderPortal || string.IsNullOrWhiteSpace(effective) || !Directory.Exists(effective))
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                FolderItems.Clear();
                // Notify placeholder state may have changed (e.g., current folder deleted, or root missing on load)
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(HasFolderPath));
                OnPropertyChanged(nameof(IsFolderMissing));
                OnPropertyChanged(nameof(IsAtRoot));
            });
            // If current folder deleted but root still exists, stay on empty view (no placeholder); if root missing, placeholder will show via HasFolder
            if (!string.IsNullOrWhiteSpace(effective) && !Directory.Exists(effective) && !string.IsNullOrWhiteSpace(_box.FolderPath) && Directory.Exists(_box.FolderPath) && !string.Equals(effective, _box.FolderPath, StringComparison.OrdinalIgnoreCase))
            {
                // Current subfolder missing but root exists: keep showing empty, watcher will be updated via ManageFolderWatcher
            }
            return;
        }

        string folder = effective;
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        }
        catch
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => FolderItems.Clear());
            return;
        }

        // Exclude desktop.ini etc via filter on path filename and hidden/system per Explorer settings
        entries = entries.Where(p => !ShellItemFilter.IsExcluded(p) && ShouldShowEntry(p)).ToList();

        var newItems = entries.Select(p => new FolderItemViewModel(p, _icons)).ToList();
        // Apply sort before assigning
        newItems = SortEntries(newItems).ToList();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => ApplyFolderItems(newItems));
        }
        else
        {
            ApplyFolderItems(newItems);
        }
    }

    private void ApplyFolderItems(List<FolderItemViewModel> newItems)
    {
        FolderItems.Clear();
        foreach (var it in newItems) FolderItems.Add(it);
    }

    private IEnumerable<FolderItemViewModel> SortEntries(IEnumerable<FolderItemViewModel> items)
    {
        Func<FolderItemViewModel, object> key = _box.FolderSortBy switch
        {
            FolderSortMode.DateModified => x => x.ModifiedTime,
            FolderSortMode.Size => x => x.FileSize,
            FolderSortMode.Type => x => x.Extension,
            _ => x => x.DisplayName
        };
        var sorted = _box.FolderSortAscending
            ? items.OrderBy(key, Comparer<object>.Create(CompareObjects)).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            : items.OrderByDescending(key, Comparer<object>.Create(CompareObjects)).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
        // Always keep folders first in Name sort? Keep simple: pure sort.
        return sorted;
    }

    private static int CompareObjects(object? a, object? b)
    {
        if (a is string sa && b is string sb) return StringComparer.OrdinalIgnoreCase.Compare(sa, sb);
        if (a is DateTime da && b is DateTime db) return da.CompareTo(db);
        if (a is long la && b is long lb) return la.CompareTo(lb);
        return Comparer<object>.Default.Compare(a, b);
    }

    public void ApplyFolderSort()
    {
        if (FolderItems.Count == 0) return;
        var sorted = SortEntries(FolderItems.ToList()).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int old = FolderItems.IndexOf(sorted[i]);
            if (old != i) FolderItems.Move(old, i);
        }
    }

    private void PushNavHistory(string current)
    {
        if (string.IsNullOrWhiteSpace(current)) return;
        // Don't push duplicate
        if (_navBack.Count > 0 && string.Equals(_navBack.Peek(), current, StringComparison.OrdinalIgnoreCase)) return;
        _navBack.Push(current);
        _navForward.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    public bool CanGoBack => _navBack.Count > 0 || !IsAtRoot;
    public bool CanGoForward => _navForward.Count > 0;

    public bool GoBack()
    {
        if (_navBack.Count > 0)
        {
            string cur = CurrentFolderPath;
            string prev = _navBack.Pop();
            _navForward.Push(cur);
            bool wasWatching = _isWatching;
            if (wasWatching) StopWatching();
            _currentFolderPath = prev;
            OnPropertyChanged(nameof(CurrentFolderPath));
            OnPropertyChanged(nameof(IsAtRoot));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            _ = RefreshFolderAsync();
            if (wasWatching) StartWatching();
            return true;
        }
        if (!IsAtRoot)
        {
            // Fallback to parent when no history
            string cur = CurrentFolderPath;
            _navForward.Push(cur);
            OnPropertyChanged(nameof(CanGoForward));
            NavigateUpInternal();
            return true;
        }
        return false;
    }

    public bool GoForward()
    {
        if (_navForward.Count == 0) return false;
        string cur = CurrentFolderPath;
        string next = _navForward.Pop();
        _navBack.Push(cur);
        bool wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        _currentFolderPath = next;
        OnPropertyChanged(nameof(CurrentFolderPath));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        _ = RefreshFolderAsync();
        if (wasWatching) StartWatching();
        return true;
    }

    private void NavigateUpInternal()
    {
        string? cur = CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(cur) || IsAtRoot) return;
        var parent = Directory.GetParent(cur!)?.FullName;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
        if (!string.IsNullOrWhiteSpace(_box.FolderPath) && !IsDescendantOrSelf(parent, _box.FolderPath!))
            return;
        bool wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        _currentFolderPath = parent;
        OnPropertyChanged(nameof(CurrentFolderPath));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        _ = RefreshFolderAsync();
        if (wasWatching) StartWatching();
    }

    public void NavigateTo(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        // NavigateTo respects root boundary: if folder is outside root, clamp to root.
        if (!string.IsNullOrWhiteSpace(_box.FolderPath) && !IsDescendantOrSelf(folder, _box.FolderPath!))
            folder = _box.FolderPath!;
        string cur = CurrentFolderPath;
        if (string.Equals(cur, folder, StringComparison.OrdinalIgnoreCase)) return;
        PushNavHistory(cur);
        bool wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        _currentFolderPath = folder;
        OnPropertyChanged(nameof(CurrentFolderPath));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        _ = RefreshFolderAsync();
        if (wasWatching) StartWatching();
    }

    public void NavigateUp()
    {
        string cur = CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(cur) || IsAtRoot) return;
        // Treat NavigateUp as navigation with history (push current)
        PushNavHistory(cur);
        NavigateUpInternal();
    }

    public bool TryNavigateInto(FolderItemViewModel item)
    {
        if (!item.IsDirectory) return false;
        if (!Directory.Exists(item.Path)) return false;
        PushNavHistory(CurrentFolderPath);
        bool wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        _currentFolderPath = item.Path;
        OnPropertyChanged(nameof(CurrentFolderPath));
        OnPropertyChanged(nameof(IsAtRoot));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        _ = RefreshFolderAsync();
        if (wasWatching) StartWatching();
        return true;
    }

    private static bool IsDescendantOrSelf(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---- Watcher lifecycle: started only when tab is selected and not rolled ----

    public bool IsWatching => _isWatching;

    public void StartWatching()
    {
        if (_isWatching) return;
        if (_box.BoxType != BoxType.FolderPortal) return;
        string? effective = CurrentFolderPath;
        bool hasEffective = !string.IsNullOrWhiteSpace(effective) && Directory.Exists(effective);
        // Contents watcher only if current folder exists; folder watcher always if root path set (to detect delete/rename even when missing)
        try
        {
            var fs = App.Services.GetRequiredService<IFileSystemService>();
            if (hasEffective)
            {
                _watcher = fs.Watch(effective, OnFolderChanged);
            }
            // Watch parent of root to detect delete/rename/move/create of the portal folder itself
            if (!string.IsNullOrWhiteSpace(_box.FolderPath))
            {
                var parent = Path.GetDirectoryName(_box.FolderPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    _folderWatcher = fs.Watch(parent, OnFolderRootChanged);
                }
                else if (!string.IsNullOrEmpty(parent))
                {
                    // Parent may not exist if root is drive root; try watch parent anyway (may still fire)
                    try { _folderWatcher = fs.Watch(parent, OnFolderRootChanged); } catch { }
                }
            }
            _isWatching = true;
        }
        catch { }
    }

    public void StopWatching()
    {
        if (!_isWatching) return;
        _isWatching = false;
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        try { _folderWatcher?.Dispose(); } catch { }
        _folderWatcher = null;
    }

    private static bool IsSamePath(string? a, string? b)
    {
        if (a == null || b == null) return false;
        try
        {
            var fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private void OnFolderRootChanged(FileSystemChange change)
    {
        string? root = _box.FolderPath;
        if (string.IsNullOrWhiteSpace(root)) return;

        // Deleted: root folder deleted
        if (change.Kind == FileSystemChangeKind.Deleted && IsSamePath(change.Path, root))
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Dispose contents watcher (folder gone) but keep parent watcher to detect recreate
                try { _watcher?.Dispose(); } catch { }
                _watcher = null;
                // Keep _isWatching true so parent watcher stays active; contents watcher will be restarted on recreate
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(HasFolderPath));
                OnPropertyChanged(nameof(IsFolderMissing));
                OnPropertyChanged(nameof(FolderDisplayName));
                _ = RefreshFolderAsync();
            });
            return;
        }
        // Created: root folder recreated (was missing)
        if (change.Kind == FileSystemChangeKind.Created && IsSamePath(change.Path, root))
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(IsFolderMissing));
                OnPropertyChanged(nameof(FolderDisplayName));
                await RefreshFolderAsync();
                // Restart watchers to watch new folder contents
                bool was = _isWatching;
                if (was)
                {
                    // _watcher is null after delete, _folderWatcher still active; restart both
                    try { _watcher?.Dispose(); } catch { } _watcher = null;
                    try { _folderWatcher?.Dispose(); } catch { } _folderWatcher = null;
                    _isWatching = false;
                    StartWatching();
                }
            });
            return;
        }
        // Renamed / Moved: root folder renamed
        if (change.Kind == FileSystemChangeKind.Renamed && change.OldPath != null && IsSamePath(change.OldPath, root))
        {
            string newRoot = change.Path;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                string oldRoot = root;
                bool wasWatching = _isWatching;
                if (wasWatching) StopWatching();

                _box.FolderPath = newRoot;
                // Keep name in sync with folder name for FolderPortal
                {
                    string fName2 = Path.GetFileName(newRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(fName2)) fName2 = newRoot;
                    if (!string.IsNullOrEmpty(fName2) && _box.Name != fName2)
                    {
                        _box.Name = fName2;
                        OnPropertyChanged(nameof(Name));
                    }
                }
                // Update current navigation: if was at root or inside root, move it
                if (string.IsNullOrEmpty(_currentFolderPath) || IsSamePath(_currentFolderPath, oldRoot))
                {
                    _currentFolderPath = newRoot;
                }
                else if (!string.IsNullOrEmpty(_currentFolderPath) && IsDescendantOrSelf(_currentFolderPath, oldRoot))
                {
                    try
                    {
                        var relative = _currentFolderPath.Substring(oldRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        _currentFolderPath = Path.Combine(newRoot, relative);
                    }
                    catch { _currentFolderPath = newRoot; }
                }
                // External rename invalidates navigation history (old paths)
                _navBack.Clear();
                _navForward.Clear();

                OnPropertyChanged(nameof(FolderPath));
                OnPropertyChanged(nameof(CurrentFolderPath));
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(HasFolderPath));
                OnPropertyChanged(nameof(IsFolderMissing));
                OnPropertyChanged(nameof(FolderDisplayName));
                OnPropertyChanged(nameof(IsAtRoot));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));

                _ = RefreshFolderAsync();
                // Persist new path
                try { App.Services.GetRequiredService<DesktopManager>().SaveAsyncFireAndForget(); } catch { }

                if (wasWatching) StartWatching();
                // Also restart folder watcher for new parent
                if (wasWatching && _folderWatcher == null)
                {
                    // StartWatching already started it, but ensure
                }
            });
        }
    }

    private void OnFolderChanged(FileSystemChange change)
    {
        // Debounce: file copy can fire many events; coalesce to one refresh.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(120);
            await RefreshFolderAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public async Task<bool> RenameFolderItemAsync(FolderItemViewModel vm, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName.Equals(vm.DisplayName, StringComparison.OrdinalIgnoreCase)) return false;
        string ext = System.IO.Path.GetExtension(vm.Path);
        if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(newName)) newName += ext;
        var dir = System.IO.Path.GetDirectoryName(vm.Path) ?? _box.FolderPath;
        if (string.IsNullOrEmpty(dir)) return false;
        string newPath = System.IO.Path.Combine(dir, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath)) return false;
        var wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        try
        {
            var ops = App.Services.GetRequiredService<IFileOperationService>();
            bool ok = ops.Rename(vm.Path, newName);
            if (ok)
            {
                await RefreshFolderAsync();
            }
            return ok;
        }
        finally
        {
            if (wasWatching) StartWatching();
        }
    }

    public async Task<bool> DeleteFolderItemAsync(FolderItemViewModel vm, bool permanent)
    {
        var wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        try
        {
            var ops = App.Services.GetRequiredService<IFileOperationService>();
            bool ok = ops.Delete(vm.Path, permanent);
            if (ok) await RefreshFolderAsync();
            return ok;
        }
        finally
        {
            if (wasWatching) StartWatching();
        }
    }

    /// <summary>Adds an item to the underlying model; the <see cref="Items"/> view collection mirrors it.</summary>
    public void AddItem(BoxItem item) => _box.Items.Add(item);

    /// <summary>Inserts an item at <paramref name="index"/> in the underlying model (used for drag-reordering).</summary>
    public void InsertItem(BoxItem item, int index)
    {
        int count = _box.Items.Count;
        if (index < 0)
        {
            index = 0;
        }

        if (index > count)
        {
            index = count;
        }

        _box.Items.Insert(index, item);
    }

    /// <summary>Removes an item from the underlying model; the <see cref="Items"/> view collection mirrors it.</summary>
    public void RemoveItem(BoxItem item) => _box.Items.Remove(item);

    private void OnBoxItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            for (int k = 0; k < e.NewItems.Count; k++)
            {
                if (e.NewItems[k] is BoxItem item && !ShellItemFilter.IsExcluded(item.Path))
                {
                    int at = e.NewStartingIndex + k;
                    if (at >= 0 && at <= Items.Count)
                    {
                        Items.Insert(at, new BoxItemViewModel(item, _icons));
                    }
                    else
                    {
                        Items.Add(new BoxItemViewModel(item, _icons));
                    }
                }
            }
        }

        if (e.OldItems != null)
        {
            foreach (BoxItem item in e.OldItems)
            {
                var existing = Items.FirstOrDefault(vm => vm.Model == item);
                if (existing != null)
                {
                    Items.Remove(existing);
                }
            }
        }
    }
}
