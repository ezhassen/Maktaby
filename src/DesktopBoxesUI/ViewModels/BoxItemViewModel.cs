using System.Threading.Tasks;
using System.Windows.Media;
using DesktopBoxesUI.Core.Models;

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
        _ = LoadIconAsync();
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
        get => _icon;
        private set => SetField(ref _icon, value);
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
