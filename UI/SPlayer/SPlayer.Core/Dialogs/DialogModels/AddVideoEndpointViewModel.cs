using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Dialogs.DialogModels;

public record ScreenItem(string Label, Screen? Screen);

public partial class AddVideoEndpointViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Video Output";

    [ObservableProperty]
    private ScreenItem? _selectedScreen;

    public ObservableCollection<ScreenItem> AvailableScreens { get; }

    // Design-time constructor
    public AddVideoEndpointViewModel()
    {
        AvailableScreens = new ObservableCollection<ScreenItem>([
            new ScreenItem("Screen 1 (Primary)  —  1920×1080", null)
        ]);
        SelectedScreen = AvailableScreens.FirstOrDefault();
    }

    public AddVideoEndpointViewModel(IReadOnlyList<Screen> screens)
    {
        AvailableScreens = new ObservableCollection<ScreenItem>(
            screens.Select((s, i) => new ScreenItem(
                $"Screen {i + 1}{(s.IsPrimary ? " (Primary)" : "")}  —  {s.Bounds.Width}×{s.Bounds.Height}",
                s)));
        SelectedScreen = AvailableScreens.FirstOrDefault(x => x.Screen?.IsPrimary == true)
                      ?? AvailableScreens.FirstOrDefault();
    }

    [RelayCommand]
    private void Add(Window dialog) => dialog.Close(true);

    [RelayCommand]
    private void Cancel(Window dialog) => dialog.Close(false);
}
