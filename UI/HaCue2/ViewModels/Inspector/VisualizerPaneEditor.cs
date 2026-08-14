using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;

namespace HaCue2.ViewModels;

/// <summary>
/// The VISUALIZER pane: preset pack, hold/blend cadence, lock, and the audio feed selection. A per-kind editor over the shared <see cref="CueEditPlumbing"/> (review F-11).
/// </summary>
public sealed partial class VisualizerPaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private VisualizerCueNode? Visualizer => context.Cue as VisualizerCueNode;
    private HaCueProject Project => context.Project;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(VisualizerPresetPackValue));
        OnPropertyChanged(nameof(VisualizerHoldValue));
        OnPropertyChanged(nameof(VisualizerBlendValue));
        OnPropertyChanged(nameof(VisualizerLocksPresetValue));
        OnPropertyChanged(nameof(VisualizerFeedAllValue));
        OnPropertyChanged(nameof(VisualizerFeedCueNumbers));
        OnPropertyChanged(nameof(VisualizerFeedHint));
        OnPropertyChanged(nameof(VisualizerHint));
    }

    public string VisualizerPresetPackValue
    {
        get => Visualizer?.PresetPack ?? "";
        set
        {
            if (Visualizer is not { } visualizer)
                return;

            var target = visualizer;
            plumbing.EditEach(target, "presetPack", "cues",
                cue => cue.PresetPack, (cue, pack) => cue.PresetPack = pack, value, "set preset pack");
        }
    }

    public string VisualizerHoldValue
    {
        get => Visualizer is { } visualizer ? CuePresentation.Seconds(visualizer.HoldMs) : "-";
        set
        {
            if (Visualizer is { } visualizer && CueEditPlumbing.TryParseSeconds(value, out var ms))
            {
                var target = visualizer;
                plumbing.EditEach(target, "visualizerHold", "cues",
                cue => cue.HoldMs, (cue, set) => cue.HoldMs = set, ms, "set preset hold");
            }
        }
    }

    public string VisualizerBlendValue
    {
        get => Visualizer is { } visualizer ? CuePresentation.Seconds(visualizer.BlendMs) : "-";
        set
        {
            if (Visualizer is { } visualizer && CueEditPlumbing.TryParseSeconds(value, out var ms))
            {
                var target = visualizer;
                plumbing.EditEach(target, "visualizerBlend", "cues",
                cue => cue.BlendMs, (cue, set) => cue.BlendMs = set, ms, "set preset blend");
            }
        }
    }

    public bool VisualizerLocksPresetValue
    {
        get => Visualizer is { LockPreset: true };
        set
        {
            if (Visualizer is not { } visualizer || value == visualizer.LockPreset)
                return;

            var target = visualizer;
            plumbing.EditEach(target, "visualizerLock", "cues",
                cue => cue.LockPreset, (cue, on) => cue.LockPreset = on, value,
                value ? "lock the preset" : "auto-advance presets");
        }
    }

    public bool VisualizerFeedAllValue
    {
        get => Visualizer is not { FeedAll: false };
        set
        {
            if (Visualizer is not { } visualizer || value == visualizer.FeedAll)
                return;
            plumbing.EditEach(visualizer, "visualizerFeedAll", "cues",
                cue => cue.FeedAll, (cue, on) => cue.FeedAll = on, value,
                value ? "feed all sounding media to the visualizer" : "use a selective visualizer feed");
        }
    }

    public string VisualizerFeedCueNumbers
    {
        get => Visualizer is not { } visualizer
            ? ""
            : string.Join(", ", visualizer.FeedCueIds
                .Select(Project.FindCue)
                .OfType<MediaCueNode>()
                .Select(cue => CuePresentation.Number(cue.Number)));
        set
        {
            if (Visualizer is not { } visualizer)
                return;

            var tokens = value.Split([',', ';', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var wanted = Project.AllCues().OfType<MediaCueNode>()
                .Where(cue => tokens.Contains(CuePresentation.Number(cue.Number), StringComparer.OrdinalIgnoreCase))
                .Select(cue => cue.Id)
                .Distinct()
                .ToList();
            if (wanted.SequenceEqual(visualizer.FeedCueIds))
                return;
            plumbing.EditEach(visualizer, "visualizerFeedCues", "cues",
                cue => cue.FeedCueIds, (cue, ids) => cue.FeedCueIds = ids,
                _ => new List<Guid>(wanted), "set visualizer audio feed cues");
        }
    }

    public string VisualizerFeedHint => Visualizer is not { } visualizer
        ? ""
        : visualizer.FeedAll
            ? "program bus · every sounding cue"
            : $"{visualizer.FeedCueIds.Count} explicit cue(s) plus every media cue marked “send to visualizer”";

    /// <summary>
    /// What a visualizer cue will actually do on THIS machine.
    /// </summary>
    /// <remarks>
    /// projectM is a native library a booth box may not have, and the settings above are perfectly
    /// editable without it - so the honest hint is the machine's answer, not a fixed sentence. A cue
    /// authored on a laptop with no library still travels and still runs at the venue.
    /// </remarks>
    public string VisualizerHint =>
        HaCue2.Engine.ProjectVisualizers.IsAvailable
            ? "renders onto every composition this cue is placed on · fires and stops like any other cue"
            : "projectM is not available on this machine - "
              + (HaCue2.Engine.ProjectVisualizers.UnavailableReason ?? "the library was not found")
              + " · the settings still travel with the show";
}
