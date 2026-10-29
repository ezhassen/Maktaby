using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Core.Services;
using Maktaby.WPFServices;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Linq;

namespace Maktaby.ViewModels;

/// <summary>
/// View-model for a single <see cref="Box"/> (one tab inside a <see cref="BoxContainer"/>). Geometry and
/// styling live on the owning <see cref="BoxContainerViewModel"/>; this only models the box's items
/// and behaviour. The <see cref="Items"/> collection mirrors the underlying <see cref="Box.Items"/>
/// model collection (which the file-rule coordinator updates), so a single source of truth is kept.
/// For <see cref="BoxType.FolderPortal"/> the <see cref="FolderItems"/> collection is the live folder view.
/// </summary>
public sealed class BoxViewModel : ViewModelBase, IDisposable
{
    private readonly Box _box;
    private readonly IconImageService _icons;
    private IDisposable? _watcher; // contents of CurrentFolderPath
    private IDisposable? _folderWatcher; // parent of FolderPath to detect root delete/rename/move
    private bool _isWatching;
    private bool _disposed;
    // Lazy folder loading: background tabs never enumerate until first selected. The path the
    // items were last loaded for (stale after navigation — compared per Ensure, never reset);
    // plus coalescing flags so concurrent refreshes re-run once instead of stacking.
    private string? _folderLoadedForPath;
    // Read on UI + pool threads as the coalesce guard (best-effort); all writes go through
    // SetLoadingFlag so the PropertyChanged notification is never skipped.
    private bool _isLoadingFolder;
    private bool _reloadRequested;
    // Watcher gap tracking: while unwatched (background tab, rolled/hidden window) filesystem
    // events are missed, so the next Ensure must refresh. _watchEpoch guards the clear against
    // a stop that lands mid-load (its events belong to the next cycle, not this snapshot).
    private bool _watchMissedChanges;
    private int _watchEpoch;
    private string? _currentFolderPath; // transient navigation, null = root (FolderPath)
    private readonly Stack<string> _navBack = new();
    private readonly Stack<string> _navForward = new();

    public BoxViewModel(Box box, IconImageService icons)
    {
        _box = box;
        _icons = icons;
        var iconsSize = Helpers.BoxIconSize.Resolve(box);
        Items = new ObservableCollection<BoxItemViewModel>(
             box.Items.Where(i => !ShellItemFilter.IsExcluded(i.Path)).Select(i => new BoxItemViewModel(i, icons, iconsSize)));
        _box.Items.CollectionChanged += OnBoxItemsChanged;
        FolderItems = new Helpers.BulkObservableCollection<FolderItemViewModel>();
        // Lazy: no enumeration here — the folder loads on first selection (EnsureFolderLoadedAsync
        // via ManageFolderWatcher / OnDataContextChanged), so background tabs cost nothing.
    }

    public System.Guid Id => _box.Id;

    /// <summary>Detaches from the underlying <see cref="Box"/> model and stops folder watching so a
    /// removed tab's view-model is collectable. Disposed VMs are never reused (moves create a new VM
    /// via <c>InsertBox</c>).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _box.Items.CollectionChanged -= OnBoxItemsChanged;
        StopWatching();
    }

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

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }
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

    public string? FolderPortalColumns
    {
        get => _box.FolderPortalColumns;
        set
        {
            if (_box.FolderPortalColumns != value)
            {
                _box.FolderPortalColumns = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasFolder => !string.IsNullOrWhiteSpace(_box.FolderPath) && Directory.Exists(_box.FolderPath);

    public bool HasFolderPath => !string.IsNullOrWhiteSpace(_box.FolderPath);

    public bool IsFolderMissing => HasFolderPath && !HasFolder;

    public string FolderDisplayName => HasFolder ? System.IO.Path.GetFileName(_box.FolderPath!) ?? _box.FolderPath! : string.Empty;

    public Helpers.BulkObservableCollection<FolderItemViewModel> FolderItems { get; }

    // ----- Search / filter -----

    private string _filterText = string.Empty;

    /// <summary>
    /// The text items are filtered by. Empty means no filtering; assigning re-filters both
    /// collections at once, so one box's filter is shared by its two body types.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (string.Equals(_filterText, value, StringComparison.Ordinal)) { return; }

            _filterText = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFilterActive));
            // New search, new answer: items that were only being kept visible because they had just
            // arrived must go back to being filtered like everything else.
            ClearFilterExemptions();
            ApplyFilter();
        }
    }

    /// <summary>True when <see cref="FilterText"/> is non-empty.</summary>
    public bool IsFilterActive => _filterText.Length > 0;

    /// <summary>Clears the filter and reveals every item again.</summary>
    public void ClearFilter() => FilterText = string.Empty;

    /// <summary>
    /// Keeps the given items visible even though they do not match the active filter, so something
    /// the user just dropped or pasted cannot disappear on landing.
    /// </summary>
    /// <remarks>
    /// The alternative - clearing the filter - throws away the search the user was in, which is
    /// worse than showing a few non-matching rows they recognise as the ones they just added. The
    /// exemption is dropped the moment the filter text changes, so the next keystroke returns to a
    /// pure result set.
    /// </remarks>
    public void RevealItems(IEnumerable<BoxItem> models)
    {
        bool revealed = false;
        var wanted = models as IReadOnlyCollection<BoxItem> ?? models.ToList();

        foreach (var item in Items)
        {
            if (item.IsFilterExempt || !wanted.Contains(item.Model))
            {
                continue;
            }

            item.IsFilterExempt = true;
            revealed = true;
        }

        if (revealed)
        {
            ApplyFilter();
        }
    }

    /// <summary>Folder-row counterpart of <see cref="RevealItems"/>, keyed on the row's path.</summary>
    public void RevealFolderRows(IEnumerable<string> paths)
    {
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        bool revealed = false;

        foreach (var row in FolderItems)
        {
            if (!row.IsFilterExempt && wanted.Contains(row.Path))
            {
                row.IsFilterExempt = true;
                revealed = true;
            }
        }

        if (revealed)
        {
            ApplyFilter();
        }
    }

    private void ClearFilterExemptions()
    {
        foreach (var item in Items)
        {
            if (item.IsFilterExempt)
            {
                item.IsFilterExempt = false;
            }
        }

        foreach (var row in FolderItems)
        {
            if (row.IsFilterExempt)
            {
                row.IsFilterExempt = false;
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="text"/> as the filter and re-filters even when the text is unchanged.
    /// </summary>
    /// <remarks>
    /// The <see cref="FilterText"/> setter skips work when the value is equal, which is right in
    /// isolation but wrong after a folder refresh or a dropped tab: both rebuild the item instances,
    /// and the new ones carry a default <c>IsFilteredOut</c> - so an unchanged filter would have to
    /// be re-applied by hand to hide the entries it should hide.
    /// </remarks>
    public void SetFilter(string text)
    {
        if (!string.Equals(FilterText, text, StringComparison.Ordinal))
        {
            FilterText = text;
            return;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Hides the entries that do not match, on both the box items and the folder rows.
    /// </summary>
    /// <remarks>
    /// Matching is on the displayed name, plus the full path so a fragment of a folder can find
    /// things in it - the behaviour Explorer has. Case-insensitive, and deliberately substring rather
    /// than prefix: the filter is a find box, not a jump-to-initial.
    /// <para>
    /// Items are hidden with a per-item flag rather than by removing them from the collections,
    /// because every index in <see cref="Items"/> and <see cref="FolderItems"/> - which selection,
    /// focus, marquee and scrolling all address - is a position in the unfiltered list.
    /// </para>
    /// </remarks>
    public void ApplyFilter()
    {
        var text = _filterText;

        foreach (var item in Items)
        {
            item.IsFilteredOut = text.Length > 0 && !item.IsFilterExempt && !Matches(item.DisplayName, item.Path, text);
        }

        foreach (var row in FolderItems)
        {
            row.IsFilteredOut = text.Length > 0 && !row.IsFilterExempt && !Matches(row.DisplayName, row.Path, text);
        }
    }

    private static bool Matches(string? name, string? path, string text) =>
        (name is not null && name.Contains(text, StringComparison.CurrentCultureIgnoreCase)) ||
        (path is not null && path.Contains(text, StringComparison.CurrentCultureIgnoreCase));

    public void SetFolderPath(string? path)
    {
        FolderPath = path;
    }

    /// <summary>
    /// Explorer's "show hidden / system files" policy, read <b>once per folder load</b>.
    /// </summary>
    /// <remarks>
    /// The values are per-user constants between Explorer preference changes, but the old per-entry
    /// check read them from HKCU inside the visibility test, i.e. once per hidden entry on every
    /// refresh. Reading them once per load is equivalent and takes that cost out of the hot loop.
    /// A failed read keeps the old behaviour: hidden and system entries stay hidden.
    /// </remarks>
    private readonly record struct ExplorerVisibility(int Hidden, int ShowSuperHidden)
    {
        private const string AdvancedKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        public static ExplorerVisibility Read() =>
            new(ReadInt("Hidden", 2), ReadInt("ShowSuperHidden", 0));

        private static int ReadInt(string name, int fallback)
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(AdvancedKey, name, fallback);
                return v is int i ? i : fallback;
            }
            catch { return fallback; }
        }

        /// <summary>False when Explorer would hide the entry.</summary>
        public bool Allows(FileAttributes attrs)
        {
            // ShowSuperHidden controls system + hidden files
            if ((attrs & FileAttributes.System) != 0) return ShowSuperHidden == 1;
            if ((attrs & FileAttributes.Hidden) != 0) return Hidden == 1;
            return true;
        }
    }

    /// <summary>Single-path form of the visibility rule (the drop/paste path, one entry at a time).</summary>
    private static bool ShouldShowEntry(string path)
    {
        try
        {
            return ExplorerVisibility.Read().Allows(File.GetAttributes(path));
        }
        catch { return true; }
    }

    /// <summary>True while a folder load is in flight (binds the loading indicator).</summary>
    public bool IsLoadingFolder
    {
        get => _isLoadingFolder;
        private set => SetField(ref _isLoadingFolder, value);
    }

    /// <summary>Reloads the folder view. Concurrent calls coalesce: a refresh requested while a
    /// load is in flight re-runs once afterwards instead of stacking parallel enumerations.</summary>
    public async Task RefreshFolderAsync()
    {
        if (_disposed) return;
        if (IsLoadingFolder)
        {
            _reloadRequested = true;
            return;
        }

        // The setter owns the write: presetting the field here would make SetField's
        // equality check swallow the notification and stick the loading indicator on.
        SetLoadingFlag(true);
        try
        {
            do
            {
                _reloadRequested = false;
                await LoadFolderCoreAsync().ConfigureAwait(false);
            }
            while (_reloadRequested && !_disposed);
        }
        finally
        {
            SetLoadingFlag(false);
        }
    }

    /// <summary>Loads the folder when its contents are stale for the current path: never loaded,
    /// path changed, or watcher gaps (background tab / rolled / hidden) may have missed events.
    /// Otherwise (loaded, watched, same path) tab switches back are instant.</summary>
    public Task EnsureFolderLoadedAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_box.BoxType != BoxType.FolderPortal || !HasFolder) return Task.CompletedTask;
        if (!IsLoadingFolder && !_watchMissedChanges && string.Equals(_folderLoadedForPath, CurrentFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        return RefreshFolderAsync();
    }

    private void SetLoadingFlag(bool value)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => IsLoadingFolder = value);
        }
        else
        {
            IsLoadingFolder = value;
        }
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private async Task LoadFolderCoreAsync()
    {
        string effective = CurrentFolderPath;
        int epoch = _watchEpoch;
        if (_box.BoxType != BoxType.FolderPortal || string.IsNullOrWhiteSpace(effective) || !Directory.Exists(effective))
        {
            RunOnUi(() =>
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
            MarkFolderFresh(effective, epoch);
            return;
        }

        string folder = effective;
        // Enumeration + filtering + VM construction + sorting are pure IO/CPU: keep them off the
        // UI thread so large or network folders never freeze the desktop while loading.
        // Null = transient enumeration failure: stay retryable instead of marking loaded.
        List<FolderItemViewModel>? newItems;
        try
        {
            newItems = await Task.Run(() =>
            {
                // ONE metadata pass. The old shape enumerated entry *paths* and then statted each
                // one again (GetAttributes for the hidden/system rule, Directory.Exists for the
                // row's IsDirectory, then FileInfo/GetLastWriteTime per bound cell): three to four
                // round-trips per entry. GetFileSystemInfos hands back attributes, size and
                // last-write time from the directory enumeration itself - measured on a 20k-entry
                // folder: ~1.4 s before, ~20-30 ms after.
                FileSystemInfo[] infos;
                try
                {
                    infos = new DirectoryInfo(folder).GetFileSystemInfos();
                }
                catch
                {
                    return (List<FolderItemViewModel>?)null;
                }

                var visibility = ExplorerVisibility.Read();
                int iconSize = Helpers.BoxIconSize.ResolveForView(_box, _box.FolderPortalViewMode);
                var rows = new List<FolderItemViewModel>(infos.Length);

                foreach (var info in infos)
                {
                    try
                    {
                        // Exclude desktop.ini etc, and hidden/system entries per Explorer settings.
                        if (ShellItemFilter.IsExcluded(info.FullName)) continue;
                        var attrs = info.Attributes;
                        if (!visibility.Allows(attrs)) continue;

                        bool isDirectory = (attrs & FileAttributes.Directory) != 0;
                        long length = isDirectory ? 0 : info is FileInfo file ? file.Length : 0;
                        rows.Add(new FolderItemViewModel(info.FullName, _icons, iconSize, isDirectory, length, info.LastWriteTime));
                    }
                    catch
                    {
                        // An entry that vanished / cannot be read must not fail the whole load.
                    }
                }

                return SortEntries(rows);
            }).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (newItems is null)
        {
            return;
        }

        // Navigated away (or disposed) mid-load: skip the stale apply — the newer refresh owns it.
        if (_disposed || !string.Equals(effective, CurrentFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunOnUi(() => ApplyFolderItems(newItems));
        MarkFolderFresh(effective, epoch);
    }

    /// <summary>Records a completed load — unless the watcher was stopped mid-load, in which case
    /// the missed flag (and its newer epoch) survives so the next Ensure refreshes again.</summary>
    private void MarkFolderFresh(string effective, int epoch)
    {
        if (epoch != _watchEpoch)
        {
            return;
        }

        _folderLoadedForPath = effective;
        _watchMissedChanges = false;
    }

    private void ApplyFolderItems(List<FolderItemViewModel> newItems)
    {
        // In-place diff keyed by path: reuses surviving VMs (and their already-loaded icons),
        // emitting only deltas instead of N notifications + a full re-layout per refresh.
        // Reuse cannot go stale: FileSize/ModifiedTime are live getters, and renames change the path.
        var wanted = new HashSet<string>(newItems.Select(v => v.Path), StringComparer.OrdinalIgnoreCase);
        for (int i = FolderItems.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(FolderItems[i].Path))
            {
                FolderItems.RemoveAt(i);
            }
        }

        var live = new Dictionary<string, FolderItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var vm in FolderItems)
        {
            live[vm.Path] = vm;
        }

        for (int i = 0; i < newItems.Count; i++)
        {
            var fresh = newItems[i];
            var vm = live.TryGetValue(fresh.Path, out var existing) ? existing : fresh;

            // Metadata is cached on the row (see FolderItemViewModel), so a reused row has to adopt
            // the values this load just enumerated - otherwise it keeps the size/date it was first
            // created with, which is exactly what the live getters used to hide.
            if (existing is not null && !ReferenceEquals(existing, fresh))
            {
                existing.ApplyMetadata(fresh.IsDirectory, fresh.FileSize, fresh.ModifiedTime);
            }

            if (i < FolderItems.Count)
            {
                if (!ReferenceEquals(FolderItems[i], vm))
                {
                    int at = FolderItems.IndexOf(vm);
                    if (at >= 0)
                    {
                        FolderItems.Move(at, i);
                    }
                    else
                    {
                        FolderItems.Insert(i, vm);
                    }
                }
            }
            else
            {
                FolderItems.Add(vm);
            }
        }

        // Re-apply the filter after every load. Newly added rows arrive with the default
        // IsFilteredOut and reused rows keep whatever they had, so without this a refresh (watcher
        // event, F5, a paste) would let new files bypass the filter outright and leave the view
        // showing a mix of filtered and unfiltered rows.
        ApplyFilter();
    }

    private List<FolderItemViewModel> SortEntries(IEnumerable<FolderItemViewModel> items)
    {
        // Typed comparisons, not a Func<FolderItemViewModel, object> key: a key selector returning
        // object boxes every date/size and then dispatches through Comparer<object> per comparison.
        // Always keep folders first in Name sort? Keep simple: pure sort.
        Comparison<FolderItemViewModel> compare = _box.FolderSortBy switch
        {
            FolderSortMode.DateModified => static (a, b) => a.ModifiedTime.CompareTo(b.ModifiedTime),
            FolderSortMode.Size => static (a, b) => a.FileSize.CompareTo(b.FileSize),
            FolderSortMode.Type => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Extension, b.Extension),
            _ => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName),
        };

        bool ascending = _box.FolderSortAscending;
        var list = items as List<FolderItemViewModel> ?? items.ToList();
        list.Sort((a, b) =>
        {
            int order = ascending ? compare(a, b) : -compare(a, b);
            // Explicit tie-break keeps the order deterministic without OrderBy's stability.
            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        });
        return list;
    }

    /// <summary>
    /// What a re-sort may spend moving rows one at a time before the whole collection is rebuilt in
    /// one reset: the sum of the distances every row has to travel (a
    /// <see cref="ObservableCollection{T}.Move"/> shifts the rows in between) and, separately, how many
    /// rows may be moved at all (each move is a `CollectionChanged` notification for every view bound
    /// to this collection). Either budget exceeded ⇒ bulk rebuild.
    /// </summary>
    private const long InPlaceShiftBudget = 50_000;
    private const int InPlaceMoveBudget = 4_096;

    public void ApplyFolderSort()
    {
        if (FolderItems.Count == 0) return;
        var sorted = SortEntries(FolderItems);

        // Where each row sits right now. The old loop called FolderItems.IndexOf per row to learn
        // this — an O(n) scan per row, which is the quadratic term: 20k rows spent ~0.4 s here even
        // when the sort moved a single row.
        var index = new Dictionary<FolderItemViewModel, int>(FolderItems.Count);
        for (int i = 0; i < FolderItems.Count; i++)
        {
            index[FolderItems[i]] = i;
        }

        long shift = 0;
        int moves = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = index[sorted[i]];
            if (at == i) continue;
            shift += Math.Abs(at - i);
            moves++;
        }

        // A watcher tick that re-sorts by date/size moves a row or two, and moving those keeps
        // scroll, focus and selection untouched. A sort-mode change reorders everything: that is
        // quadratic with Move, so past the budget the contents are replaced in bulk instead.
        if (shift <= InPlaceShiftBudget && moves <= InPlaceMoveBudget)
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                int at = index[sorted[i]];
                if (at == i) continue;

                int lo = Math.Min(at, i), hi = Math.Max(at, i);
                FolderItems.Move(at, i);
                // Move shifts the rows in between; keep the map in step so the next row is still a
                // lookup instead of a scan.
                for (int k = lo; k <= hi; k++) index[FolderItems[k]] = k;
            }
            return;
        }

        // Selection lives on the rows (the grid's IsSelected setter is TwoWay), but a reset throws
        // away the containers and with them the grid's selection, so snapshot the flags and put
        // them back after the rebuild.
        List<FolderItemViewModel>? selected = null;
        foreach (var row in FolderItems)
        {
            if (row.IsSelected) (selected ??= new()).Add(row);
        }

        FolderItems.ReplaceAll(sorted);

        if (selected is null) return;
        foreach (var row in selected) row.IsSelected = true;
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
        // From here on filesystem events are missed: force the next Ensure to refresh.
        _watchMissedChanges = true;
        _watchEpoch++;
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
                    try { _watcher?.Dispose(); } catch { }
                    _watcher = null;
                    try { _folderWatcher?.Dispose(); } catch { }
                    _folderWatcher = null;
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

    // Batched targeted updates: filesystem bursts (copy jobs, AV/indexer catch-up after
    // sleep) arrive as hundreds of events. Enumerating + rebuilding the whole folder per event
    // pins the CPU and churns the LOH; instead events accumulate here and ONE trailing UI pass
    // applies them as single-item deltas. Anything ambiguous falls back to a full refresh, so
    // correctness can never regress — only speed.
    private readonly object _pendingGate = new();
    private readonly List<FileSystemChange> _pendingChanges = new();
    private bool _drainScheduled;
    private const int ChangeOverflowFullRefresh = 1000;
    private const int ChangeHardCap = 5000;

    private void OnFolderChanged(FileSystemChange change)
    {
        lock (_pendingGate)
        {
            if (_pendingChanges.Count >= ChangeHardCap)
            {
                // Pathological storm: drop the backlog, one full refresh covers everything.
                _pendingChanges.Clear();
                _pendingChanges.Add(new FileSystemChange(FileSystemChangeKind.Unknown, ""));
            }
            else
            {
                _pendingChanges.Add(change);
            }

            if (_drainScheduled) return;
            _drainScheduled = true;
        }

        // Debounce: a file copy fires many events; coalesce to one trailing drain.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(120);
            await DrainFolderChangesAsync();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task DrainFolderChangesAsync()
    {
        List<FileSystemChange> batch;
        lock (_pendingGate)
        {
            _drainScheduled = false;
            batch = new List<FileSystemChange>(_pendingChanges);
            _pendingChanges.Clear();
        }

        if (_disposed || batch.Count == 0) return;
        if (_box.BoxType != BoxType.FolderPortal) return;

        // Storm overflow or ambiguous marker: full refresh covers everything at once.
        if (batch.Count >= ChangeOverflowFullRefresh
            || batch.Any(c => c.Kind == FileSystemChangeKind.Unknown))
        {
            await RefreshFolderAsync();
            return;
        }

        string effective = CurrentFolderPath;
        if (string.IsNullOrWhiteSpace(effective) || !Directory.Exists(effective))
        {
            await RefreshFolderAsync();
            return;
        }

        // Any event outside the current folder (subfolder noise, renames across folders):
        // fall back — targeted math only covers direct children.
        foreach (var c in batch)
        {
            if (!IsDirectChild(c.Path, effective)
                || (c.OldPath != null && !IsDirectChild(c.OldPath, effective) && !string.IsNullOrEmpty(c.OldPath)))
            {
                await RefreshFolderAsync();
                return;
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => ApplyFolderDeltas(batch, effective));
        }
        else
        {
            ApplyFolderDeltas(batch, effective);
        }
    }

    private static bool IsDirectChild(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var dir = Path.GetDirectoryName(path);
            return string.Equals(dir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Applies a batch of single-item deltas (UI thread). New VMs load icons lazily on
    /// render — unlike a full refresh, this allocates almost nothing per event.</summary>
    private void ApplyFolderDeltas(List<FileSystemChange> batch, string effective)
    {
        if (_disposed) return;
        // Navigated away while the batch waited: a full load owns the new folder.
        if (!string.Equals(CurrentFolderPath, effective, StringComparison.OrdinalIgnoreCase)) return;

        bool structural = false;
        bool meta = false;
        foreach (var c in batch)
        {
            switch (c.Kind)
            {
                case FileSystemChangeKind.Created:
                    if (TryAddSingleItem(c.Path)) structural = true;
                    break;
                case FileSystemChangeKind.Deleted:
                    structural |= RemoveSingleItem(c.Path);
                    break;
                case FileSystemChangeKind.Changed:
                    if (TouchSingleItem(c.Path)) meta = true;
                    break;
                case FileSystemChangeKind.Renamed:
                    if (!string.IsNullOrEmpty(c.OldPath)) structural |= RemoveSingleItem(c.OldPath);
                    if (TryAddSingleItem(c.Path)) structural = true;
                    break;
                default:
                    _ = RefreshFolderAsync();
                    return;
            }
        }

        // One in-memory re-sort covers date/size ordering after metadata changes.
        if (structural || (meta && (_box.FolderSortBy == FolderSortMode.DateModified || _box.FolderSortBy == FolderSortMode.Size)))
        {
            ApplyFolderSort();
        }

        _folderLoadedForPath = effective;
        _watchMissedChanges = false;
    }

    private bool TryAddSingleItem(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            if (ShellItemFilter.IsExcluded(path) || !ShouldShowEntry(path)) return false;
            foreach (var vm in FolderItems)
            {
                if (string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase)) return false;
            }

            // Same information the folder enumeration supplies, read once for this one entry.
            var info = new FileInfo(path);
            var attrs = info.Attributes;
            bool isDirectory = (attrs & FileAttributes.Directory) != 0;
            long length = isDirectory ? 0 : info.Length;

            FolderItems.Add(new FolderItemViewModel(
                path,
                _icons,
                Helpers.BoxIconSize.ResolveForView(_box, _box.FolderPortalViewMode),
                isDirectory,
                length,
                info.LastWriteTime));
            return true;
        }
        catch { return false; }
    }

    private bool RemoveSingleItem(string path)
    {
        try
        {
            for (int i = FolderItems.Count - 1; i >= 0; i--)
            {
                if (string.Equals(FolderItems[i].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    FolderItems.RemoveAt(i);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private bool TouchSingleItem(string path)
    {
        try
        {
            foreach (var vm in FolderItems)
            {
                if (string.Equals(vm.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    vm.ReloadIcon();
                    // Size/date are cached on the row now: this event is the only notification
                    // that they changed without a full re-enumeration.
                    vm.RefreshMetadata();
                    return true;
                }
            }
            // Changed file we don't show (or arrived before its Created): refresh to be sure.
            _ = RefreshFolderAsync();
        }
        catch { }
        return false;
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

    [Obsolete("Use DeleteFolderItemsAsync instead", error: true)]
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

    /// <summary>Deletes several items in ONE native operation (single shell dialog): cancelling it
    /// aborts the whole batch. The old per-item fan-out showed one modal per file, where cancelling
    /// one dialog could not stop the rest — users read that as "cancel still deletes".</summary>
    public async Task<bool> DeleteFolderItemsAsync(IEnumerable<FolderItemViewModel> vms, bool permanent)
    {
        var paths = vms?.Where(v => v is not null && !string.IsNullOrEmpty(v.Path)).Select(v => v.Path).ToList();
        if (paths is null || paths.Count == 0) return false;
        var wasWatching = _isWatching;
        if (wasWatching) StopWatching();
        try
        {
            var ops = App.Services.GetRequiredService<IFileOperationService>();
            bool ok = await ops.DeleteAsync(paths, permanent);
            if (ok) await RefreshFolderAsync();
            return ok;
        }
        finally
        {
            if (wasWatching) StartWatching();
        }
    }

    /// <summary>Adds an item to the underlying model; the <see cref="Items"/> view collection mirrors it.</summary>
    public void AddItem(BoxItem item)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.Invoke(() => _box.Items.Add(item));
        }
        else
        {
            _box.Items.Add(item);
        }
    }

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

        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.Invoke(() => _box.Items.Insert(index, item));
        }
        else
        {
            _box.Items.Insert(index, item);
        }
    }

    /// <summary>Removes an item from the underlying model; the <see cref="Items"/> view collection mirrors it.</summary>
    public void RemoveItem(BoxItem item)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.Invoke(() => _box.Items.Remove(item));
        }
        else
        {
            _box.Items.Remove(item);
        }
    }

    private void OnBoxItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            int iconSize = Helpers.BoxIconSize.ResolveForView(_box, _box.FolderPortalViewMode);
            for (int k = 0; k < e.NewItems.Count; k++)
            {
                if (e.NewItems[k] is BoxItem item && !ShellItemFilter.IsExcluded(item.Path))
                {
                    int at = e.NewStartingIndex + k;
                    if (at >= 0 && at <= Items.Count)
                    {
                        Items.Insert(at, new BoxItemViewModel(item, _icons, iconSize));
                    }
                    else
                    {
                        Items.Add(new BoxItemViewModel(item, _icons, iconSize));
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
