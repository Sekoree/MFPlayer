using CommunityToolkit.Mvvm.ComponentModel;

namespace MFPlayer.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _appTitle = "MFPlayer";

    [ObservableProperty]
    private string _status = "Desktop shell for the OwnAudio + FFmpeg/OpenGL video playground.";

    [ObservableProperty]
    private string _activeProjects = "Active validation projects: Test/VideoTest (Avalonia mirrored output) and Test/AudioEx (SDL push playback).";

    [ObservableProperty]
    private string _notes = "The repo is mid-refactor around the video engine/mixer split. Use the test apps to validate decoder, engine, output, and mirroring changes quickly.";
}
