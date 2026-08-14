using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;

namespace HaCue2.ViewModels;

/// <summary>
/// The PATCH pane: the snapshot and inline level changes a patch cue recalls, and its fade. A per-kind editor over the shared <see cref="CueEditPlumbing"/> (review F-11).
/// </summary>
public sealed partial class PatchPaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private PatchCueNode? Patch => context.Cue as PatchCueNode;
    private HaCueProject Project => context.Project;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(PatchSnapshots));
        OnPropertyChanged(nameof(PatchSnapshotIndex));
        OnPropertyChanged(nameof(PatchFadeValue));
        OnPropertyChanged(nameof(PatchLevelChanges));
        OnPropertyChanged(nameof(HasPatchLevelChanges));
        OnPropertyChanged(nameof(PatchHint));
    }

    public IReadOnlyList<string> PatchSnapshots =>
        Patch is null
            ? []
            : ["- none -", .. Project.PatchSnapshots.Select(
                snapshot => $"snapshot “{snapshot.Name}”")];

    public int PatchSnapshotIndex
    {
        get
        {
            if (Patch?.SnapshotId is not { } id)
                return 0;

            var at = Project.PatchSnapshots.FindIndex(snapshot => snapshot.Id == id);
            return at < 0 ? 0 : at + 1;
        }
        set
        {
            if (Patch is not { } patch || value < 0)
                return;

            var chosen = value == 0 || value > Project.PatchSnapshots.Count
                ? (Guid?)null
                : Project.PatchSnapshots[value - 1].Id;

            if (chosen == patch.SnapshotId)
                return;

            var target = patch;
            plumbing.EditEach(target, "patchSnapshot", "cues",
                cue => cue.SnapshotId, (cue, id) => cue.SnapshotId = id, chosen, "set patch snapshot");
        }
    }

    public string PatchFadeValue
    {
        get => Patch is { } patch ? CuePresentation.Seconds(patch.FadeMs) : "-";
        set
        {
            if (Patch is { } patch && CueEditPlumbing.TryParseSeconds(value, out var ms))
            {
                var target = patch;
                plumbing.EditEach(target, "patchFade", "cues",
                cue => cue.FadeMs, (cue, set) => cue.FadeMs = set, ms, "set patch fade");
            }
        }
    }

    /// <summary>The cue's inline level changes, as the pane lists them.</summary>
    /// <remarks>
    /// Read from the document rather than authored: the pane used to show two fixed rows ("Fold L/R",
    /// "Sub") whatever the cue actually carried, so a patch cue with three changes showed two and one
    /// with none still showed two.
    /// </remarks>
    public IReadOnlyList<string> PatchLevelChanges =>
        Patch is null
            ? []
            : [.. Patch.Levels.Select(change =>
                $"{Project.FindChannel(change.LogicalChannelId)?.Name ?? "(deleted output)"} → "
                + (change.Muted ? "mute" : CuePresentation.Db(change.GainDb)))];

    public bool HasPatchLevelChanges => PatchLevelChanges.Count > 0;

    public string PatchHint => Patch is not { } patch
        ? ""
        : patch.SnapshotId is null && patch.Levels.Count == 0
            ? "nothing to recall - this cue will do nothing"
            : "";
}
