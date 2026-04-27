using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SPlayer.Core.Services;

namespace SPlayer.Core.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    public OutputViewModel Outputs { get; } = new();

    public SettingsViewModel Settings { get; }

    public PlayerViewModel Player { get; }

    public AppSettingsService SettingsStore { get; }

    public MainViewModel()
    {
        SettingsStore = new AppSettingsService();
        Settings = new SettingsViewModel(SettingsStore, Outputs);
        Player = new PlayerViewModel(Outputs, Settings, SettingsStore);
    }

    public void Dispose() => Player.Dispose();
}
