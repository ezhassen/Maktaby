using Maktaby.Core.Models;
using Maktaby.WPFServices;
using System.Windows.Media;

namespace Maktaby.ViewModels;

/// <summary>
/// View-model for a single <see cref="BoxItem"/> (one desktop icon/file inside a Box). Loads its
/// icon asynchronously through <see cref="IconImageService"/>.
/// </summary>
public sealed class BoxItemViewModel : ViewModelBase
{
    private readonly BoxItem _model;
    private readonly IconImageService _icons;
    private string _displayName;
    private ImageSource? _icon;

    public BoxItemViewModel(BoxItem model, IconImageService icons, int iconSize)
    {
        _model = model;
        _icons = icons;
        _displayName = model.DisplayName;
        _iconSize = iconSize < 16 ? 16 : (iconSize > 256 ? 256 : iconSize);
    }

    public BoxItem Model => _model;

    public string Path => _model.Path;

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    /// <summary>True while the user is renaming this item inline.</summary>
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    /// <summary>True when this item is part of the current selection (highlighted).</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>The in-progress name typed during an inline rename.</summary>
    private string _renameText = string.Empty;
    public string RenameText
    {
        get => _renameText;
        set => SetField(ref _renameText, value);
    }

    public ImageSource? Icon
    {
        // Lazily start the icon load on first access — i.e. when the binding realises the item in the
        // visual tree — rather than eagerly in the constructor. Keeps startup/VM construction cheap.
        get
        {
            if (!_iconWanted)
            {
                // Gated: the tile is outside the box viewport, so its icon is deferred (see
                // IsIconWanted). The already-resolved bitmap, if any, is kept for the way back.
                return null;
            }

            EnsureIconLoaded();
            return _icon;
        }
        private set => SetField(ref _icon, value);
    }

    private bool _iconLoadRequested;

    private bool _iconWanted = true;

    /// <summary>
    /// Whether this item's icon is wanted right now. A box layers its items in a plain
    /// <c>WrapPanel</c>, so every tile realizes together and binding asks for every icon at once —
    /// a cold 135-icon launch measured ~2 s of shell extraction. <c>BoxControl</c> narrows this to
    /// the rows in (and one row around) its viewport instead.
    /// </summary>
    /// <remarks>
    /// Defaults to true so a consumer with no viewport (drag ghost, settings preview, tests) still
    /// resolves its icon. Turning it off keeps a resolved icon for re-entry — scrolling back to a
    /// seen item is instant, only never-seen items pay the shell.
    /// </remarks>
    public bool IsIconWanted
    {
        get => _iconWanted;
        set
        {
            if (_iconWanted == value)
            {
                return;
            }

            _iconWanted = value;

            if (value)
            {
                EnsureIconLoaded();
            }

            // The tile bound while this was false (or is still bound now): re-read the getter.
            OnPropertyChanged(nameof(Icon));
        }
    }

    private int _iconSize = 32;

    /// <summary>
    /// The display size (in device-independent pixels) this item's icon is drawn at. Setting it
    /// re-resolves the icon.
    /// </summary>
    /// <remarks>
    /// The shell only returns 32 or 48 px icons, so a tile drawn at 96 px was an upscale - the blur
    /// the large icon size showed. The service renders the icon at the physical size the display
    /// size needs, and changing the size has to re-resolve it (the cached one is the wrong
    /// resolution).
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

    /// <summary>Drops the current icon and resolves it again at the current size.</summary>
    private void ReloadIconAtCurrentSize()
    {
        _iconLoadRequested = false;
        _icon = null;
        Icon = null;

        // A gated item re-resolves when it scrolls back in (IsIconWanted), not now: a box-wide icon
        // size change must not drag every off-screen tile back through the shell.
        if (_iconWanted)
        {
            EnsureIconLoaded();
        }
    }

    //fire and forget
    private async void EnsureIconLoaded()
    {
        if (_iconLoadRequested || !_iconWanted)
        {
            return;
        }

        _iconLoadRequested = true;
        await LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        var icon = await ResolveIconAsync();

        // Retry/log only when there was actually something to resolve — an item with neither a PIDL
        // nor a path simply has no icon source.
        bool hasSource = !string.IsNullOrEmpty(_model.Pidl) || !string.IsNullOrEmpty(_model.Path);

        if (icon is null && hasSource)
        {
            // Transient shell failures are common right at startup (Explorer busy, icon cache cold);
            // give it a moment and try once more before surfacing a blank icon.
            await Task.Delay(750);
            icon = await ResolveIconAsync();
        }

        if (icon is null && hasSource)
        {
            // Final failure: un-arm the one-shot guard so a later EnsureIconLoaded (container
            // refresh, re-template) retries the item instead of staying blank forever.
            _iconLoadRequested = false;
            Serilog.Log.Warning("Icon resolution failed for {Path} (pidl-backed: {HasPidl})",
                _model.Path, !string.IsNullOrEmpty(_model.Pidl));
        }

        Icon = icon;
    }

    private Task<ImageSource?> ResolveIconAsync()
    {
        return string.IsNullOrEmpty(_model.Pidl)
            ? (string.IsNullOrEmpty(_model.Path) ? Task.FromResult((ImageSource?)null) : _icons.GetIconAsync(_model.Path, _iconSize))
            : _icons.GetIconFromPidlAsync(_model.Pidl, _model.Path, _iconSize);
    }

    /// <summary>Forces the icon to reload from the (possibly changed) <see cref="BoxItem.Path"/> — used
    /// after a rename where the extension (and thus the icon) may have changed.</summary>
    public void ReloadIcon()
    {
        _icon = null;
        _iconLoadRequested = false;
        OnPropertyChanged(nameof(Icon));
    }

    /// <summary>True when the box is filtered and this item does not match the text.</summary>
    /// <remarks>
    /// Kept as a property rather than by removing items from the collection: the item template binds
    /// its Visibility to this, while <c>Items</c> stays the full list. That matters because selection,
    /// focus and every index in the box are positions in the unfiltered collection.
    /// </remarks>
    private bool _isFilteredOut;
    public bool IsFilteredOut
    {
        get => _isFilteredOut;
        set => SetField(ref _isFilteredOut, value);
    }

    private bool _isFilterExempt;
    /// <summary>
    /// Shown even when it does not match the filter - set when the item arrives (a drop) while a
    /// filter is active, so the thing the user just added cannot vanish on landing.
    /// </summary>
    /// <remarks>
    /// The exemption is dropped by <see cref="BoxViewModel"/> as soon as the filter text changes: the
    /// user is searching again, and the visible set should be matches and nothing else. It survives a
    /// refresh on purpose - the pasted or dropped files keep showing until then.
    /// </remarks>
    public bool IsFilterExempt
    {
        get => _isFilterExempt;
        set => SetField(ref _isFilterExempt, value);
    }

    /// <summary>Drops the cached icon and reloads it - for an item whose icon the shell recomputes over time.
    /// <remarks>
    /// <see cref="ReloadIcon"/> alone would be useless for the Recycle Bin: it re-resolves through
    /// <see cref="IconImageService"/>, whose LRU is keyed on the PIDL, and the bin's PIDL never
    /// changes while its icon does. Invalidating the entry is what forces a fresh shell lookup.
    /// </remarks>
    public void ReloadIconBypassingCache()
    {
        if (!string.IsNullOrEmpty(_model.Pidl)) { _icons.InvalidatePidl(_model.Pidl); }
        if (!string.IsNullOrEmpty(_model.Path)) { _icons.InvalidatePath(_model.Path); }

        ReloadIcon();
    }
}
