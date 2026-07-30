using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public sealed partial class CueListEditorViewModel : ObservableObject
{
    public CueListEditorViewModel(string name)
    {
        Name = name;
    }

    /// <summary>Session-only identity for this loaded list. The cross-list merged session scopes its
    /// runtime transport groups with it (<c>HaPlayShowMapper.RuntimeGroupId</c>) so two lists cannot land
    /// their ungrouped cues on the same session group. Deliberately NOT part of <see cref="CueList"/>:
    /// nothing about it is authored, so persistence, undo and the project dirty-hash stay untouched.</summary>
    public Guid RuntimeId { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            Name = Strings.CueListFileNameFallback;
    }

    [ObservableProperty]
    private string? _path;

    public ObservableCollection<CueCompositionViewModel> Compositions { get; } = new();

    public ObservableCollection<CueVideoOutputBindingViewModel> VideoOutputs { get; } = new();

    public ObservableCollection<CueNodeViewModel> Nodes { get; } = new();

    public CueList ToModel() => new()
    {
        Name = Name,
        DefaultTriggerMode = DefaultTriggerMode,
        AutoRenumberOnInsert = AutoRenumberOnInsert,
        StopFadeMs = StopFadeMs,
        StopFadeCurve = StopFadeCurve,
        Compositions = Compositions.Select(c => c.ToModel()).ToList(),
        VideoOutputs = VideoOutputs.Select(o => o.ToModel()).ToList(),
        Nodes = Nodes.Select(n => n.ToModel()).ToList(),
    };

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode = CueTriggerMode.Manual;

    /// <summary>Mirrors <see cref="CueList.AutoRenumberOnInsert"/>, including its default ON - a list created
    /// in the app must start in the same state as a freshly constructed model, or the two disagree about what
    /// "new" means and the answer depends on whether the list was saved first.</summary>
    [ObservableProperty]
    private bool _autoRenumberOnInsert = true;

    /// <summary>Null = fall back to the app-settings stop fade (<c>AppSettings.StopFadeMs</c>).</summary>
    [ObservableProperty]
    private int? _stopFadeMs;

    [ObservableProperty]
    private CueFadeCurve _stopFadeCurve = CueFadeCurve.Linear;

    public static CueListEditorViewModel FromModel(
        CueList list,
        string? path = null,
        Func<Guid, OutputLineViewModel?>? resolveLine = null)
    {
        var vm = new CueListEditorViewModel(list.Name)
        {
            Path = path,
            DefaultTriggerMode = list.DefaultTriggerMode,
            AutoRenumberOnInsert = list.AutoRenumberOnInsert,
            StopFadeMs = list.StopFadeMs,
            StopFadeCurve = list.StopFadeCurve,
        };
        foreach (var c in list.Compositions)
            vm.Compositions.Add(CueCompositionViewModel.FromModel(c));
        foreach (var o in list.VideoOutputs)
            vm.VideoOutputs.Add(CueVideoOutputBindingViewModel.FromModel(o, resolveLine));
        foreach (var node in list.Nodes)
            vm.Nodes.Add(CueNodeViewModel.FromModel(node, resolveLine));
        return vm;
    }
}

public sealed record PreviewAudioDeviceOption(int? DeviceIndex, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public enum CueNodeDropPlacement
{
    Before,
    Inside,
    After,
}
