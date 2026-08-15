using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        OnPropertyChanged(nameof(VisualizerShuffleValue));
        OnPropertyChanged(nameof(VisualizerBeatSensitivityValue));
        OnPropertyChanged(nameof(VisualizerRenderSizeValue));
        OnPropertyChanged(nameof(VisualizerRenderRateValue));
        OnPropertyChanged(nameof(CanSkipPreset));
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

    public bool VisualizerShuffleValue
    {
        get => Visualizer is not { ShufflePresets: false };
        set
        {
            if (Visualizer is not { } visualizer || value == visualizer.ShufflePresets)
                return;
            plumbing.EditEach(visualizer, "visualizerShuffle", "cues",
                cue => cue.ShufflePresets, (cue, on) => cue.ShufflePresets = on, value,
                value ? "shuffle preset order" : "rotate presets in order");
        }
    }

    public string VisualizerBeatSensitivityValue
    {
        get => Visualizer is { } visualizer
            ? visualizer.BeatSensitivity.ToString("0.0#", CultureInfo.InvariantCulture)
            : "-";
        set
        {
            if (Visualizer is not { } visualizer
                || !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
                return;
            plumbing.EditEach(visualizer, "visualizerBeat", "cues",
                cue => cue.BeatSensitivity, (cue, level) => cue.BeatSensitivity = level,
                Math.Clamp(parsed, 0, 5), "set beat sensitivity");
        }
    }

    /// <summary>"auto" follows the composition; "1280×720" (or 1280x720) renders projectM's own
    /// FBO at that size and scales the result into the placement - HaPlay's render override.</summary>
    public string VisualizerRenderSizeValue
    {
        get => Visualizer is not { } visualizer
            ? "-"
            : visualizer is { RenderWidth: > 0, RenderHeight: > 0 }
                ? $"{visualizer.RenderWidth}×{visualizer.RenderHeight}"
                : "auto";
        set
        {
            if (Visualizer is not { } visualizer)
                return;

            var cleaned = value.Trim();
            int width = 0, height = 0;
            if (!string.IsNullOrEmpty(cleaned)
                && !cleaned.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cleaned.Split(['x', 'X', '×', '*'], StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height)
                    || width <= 0 || height <= 0)
                    return; // unparseable keeps the old value, like every other field

                width = Math.Clamp(width, 16, 8192);
                height = Math.Clamp(height, 16, 8192);
            }

            var (setWidth, setHeight) = (width, height);
            plumbing.EditEach(visualizer, "visualizerRenderSize", "cues",
                cue => (cue.RenderWidth, cue.RenderHeight),
                (cue, size) => (cue.RenderWidth, cue.RenderHeight) = size,
                (setWidth, setHeight), "set visualizer render size");
        }
    }

    /// <summary>"auto" follows the composition's rate; a number pins projectM's animation FPS.</summary>
    public string VisualizerRenderRateValue
    {
        get => Visualizer is not { } visualizer
            ? "-"
            : visualizer.RenderFps > 0
                ? visualizer.RenderFps.ToString(CultureInfo.InvariantCulture)
                : "auto";
        set
        {
            if (Visualizer is not { } visualizer)
                return;

            var cleaned = value.Trim();
            var fps = 0;
            if (!string.IsNullOrEmpty(cleaned)
                && !cleaned.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && (!int.TryParse(cleaned, out fps) || fps < 0))
                return;

            plumbing.EditEach(visualizer, "visualizerRenderFps", "cues",
                cue => cue.RenderFps, (cue, rate) => cue.RenderFps = rate,
                Math.Clamp(fps, 0, 240), "set visualizer render rate");
        }
    }

    /// <summary>Re-raises only what the 4 Hz engine poll can change - whether the skip button
    /// has a running renderer to talk to.</summary>
    public void RaiseLive() => OnPropertyChanged(nameof(CanSkipPreset));

    /// <summary>Whether THIS cue has a running renderer the skip button can reach.</summary>
    public bool CanSkipPreset =>
        Visualizer is { } visualizer
        && context.Host is { } host
        && host.Visualizers.Running.Contains(visualizer.Id);

    /// <summary>The operator's "this one is ugly" button (HaPlay parity): skips the RUNNING
    /// visualizer to another preset. Unloadable presets are blocklisted and skipped automatically
    /// by the render surface, so this is taste, not repair.</summary>
    [RelayCommand]
    private void NextPreset()
    {
        if (Visualizer is { } visualizer)
            context.Host?.Visualizers.RequestNextPreset(visualizer.Id);
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
