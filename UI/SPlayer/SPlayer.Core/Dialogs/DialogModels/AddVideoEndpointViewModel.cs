using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SPlayer.Core.Models;
using SPlayer.Core.ViewModels;

namespace SPlayer.Core.Dialogs.DialogModels;

public record ScreenItem(string Label, Screen? Screen);

public partial class AddVideoEndpointViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Video Output";

    [ObservableProperty]
    private ScreenItem? _selectedScreen;

    /// <summary>
    /// §sdl3-output — backend picked via the "+ Video" split-button. The
    /// Outputs view passes the selection into this dialog so the dialog
    /// title and explanatory blurb can reflect which backend the user is
    /// configuring. The model itself does not gate behaviour on the
    /// backend; OutputViewModel.AddVideoEndpoint reads <see cref="Backend"/>
    /// after the dialog returns to construct the matching host.
    /// </summary>
    [ObservableProperty]
    private VideoOutputBackend _backend = VideoOutputBackend.Avalonia;

    /// <summary>Short blurb under the dialog title describing the chosen backend.</summary>
    public string BackendDescription => Backend switch
    {
        VideoOutputBackend.Avalonia =>
            "Avalonia OpenGL output — paced by the Avalonia compositor. Good default; uses the same window theming as the rest of the app.",
        VideoOutputBackend.Sdl3 =>
            "SDL3 OpenGL output — runs in its own native window with a dedicated render thread. Recommended for heavy 4K / high-bitrate sources where the Avalonia dispatcher can stall.",
        _ => string.Empty
    };

    /// <summary>Window title shown in the chrome of the dialog.</summary>
    public string DialogHeader => Backend switch
    {
        VideoOutputBackend.Avalonia => "Add Avalonia Video Output",
        VideoOutputBackend.Sdl3     => "Add SDL3 Video Output",
        _ => "Add Video Output"
    };

    partial void OnBackendChanged(VideoOutputBackend value)
    {
        OnPropertyChanged(nameof(BackendDescription));
        OnPropertyChanged(nameof(DialogHeader));
    }

    public ObservableCollection<ScreenItem> AvailableScreens { get; }

    // Design-time constructor
    public AddVideoEndpointViewModel()
    {
        AvailableScreens = new ObservableCollection<ScreenItem>([
            new ScreenItem("Screen 1 (Primary)  —  1920×1080", null)
        ]);
        SelectedScreen = AvailableScreens.FirstOrDefault();
    }

    public AddVideoEndpointViewModel(IReadOnlyList<Screen> screens, VideoOutputBackend backend = VideoOutputBackend.Avalonia)
    {
        Backend = backend;
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
