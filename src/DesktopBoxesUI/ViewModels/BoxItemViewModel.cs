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
        if (!string.IsNullOrEmpty(_model.Pidl))
        {
            Icon = await _icons.GetIconFromPidlAsync(_model.Pidl);
        }
        else
        {
            Icon = await _icons.GetIconAsync(_model.Path);
        }
    }
}
