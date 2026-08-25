using DesktopBoxesUI.Core.Models;
using System.Threading.Tasks;
using System.Windows.Media;

namespace DesktopBoxesUI.ViewModels;

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

    public BoxItemViewModel(BoxItem model, IconImageService icons)
    {
        _model = model;
        _icons = icons;
        _displayName = model.DisplayName;
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
        // visual tree — rather than eagerly in the constructor. Keeps startup/VM construction cheap and
        // naturally limits work to items that are actually displayed.
        get
        {
            EnsureIconLoaded();
            return _icon;
        }
        private set => SetField(ref _icon, value);
    }

    private bool _iconLoadRequested;

    //fire and forget
    private async void EnsureIconLoaded()
    {
        if (_iconLoadRequested)
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
            ? (string.IsNullOrEmpty(_model.Path) ? Task.FromResult((ImageSource?)null) : _icons.GetIconAsync(_model.Path))
            : _icons.GetIconFromPidlAsync(_model.Pidl, _model.Path);
    }

    /// <summary>Forces the icon to reload from the (possibly changed) <see cref="BoxItem.Path"/> — used
    /// after a rename where the extension (and thus the icon) may have changed.</summary>
    public void ReloadIcon()
    {
        _icon = null;
        _iconLoadRequested = false;
        OnPropertyChanged(nameof(Icon));
    }
}
