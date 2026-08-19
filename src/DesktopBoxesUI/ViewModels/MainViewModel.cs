using System.Collections.ObjectModel;
using System.Windows.Input;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.ViewModels;

/// <summary>
/// Application-level view-model. Holds the collection of Boxes and the "New Box" command. The
/// persistence / Explorer-icon orchestration lives in <see cref="DesktopManager"/>; this view-model
/// only models the boxes the user sees.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly IBoxService _boxService;
    private readonly IconImageService _icons;

    public MainViewModel(IBoxService boxService, IconImageService icons)
    {
        _boxService = boxService;
        _icons = icons;
        CreateBoxCommand = new RelayCommand(_ => CreateBox());
    }

    public ObservableCollection<BoxViewModel> Boxes { get; } = new();

    public ICommand CreateBoxCommand { get; }

    public void LoadFromBoxes(System.Collections.Generic.IEnumerable<Box> boxes)
    {
        Boxes.Clear();
        foreach (var box in boxes)
        {
            Boxes.Add(new BoxViewModel(box, _icons));
        }
    }

    public BoxViewModel CreateBox(double left = 60, double top = 60)
    {
        var offset = Boxes.Count * 24;
        var box = _boxService.CreateBox("New Box", left + offset, top + offset, 240, 200);
        var vm = new BoxViewModel(box, _icons);
        Boxes.Add(vm);
        return vm;
    }

    /// <summary>Creates a Box at an exact position (used when dropping onto empty desktop area).</summary>
    public BoxViewModel CreateBoxAt(double left, double top)
    {
        var box = _boxService.CreateBox("New Box", left, top, 240, 200);
        var vm = new BoxViewModel(box, _icons);
        Boxes.Add(vm);
        return vm;
    }
}
