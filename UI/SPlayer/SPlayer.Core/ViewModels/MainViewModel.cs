using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SPlayer.Core.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    public OutputViewModel Outputs { get; } = new();

    public PlayerViewModel Player { get; }

    public MainViewModel()
    {
        Player = new PlayerViewModel(Outputs);
    }

    public void Dispose() => Player.Dispose();
}