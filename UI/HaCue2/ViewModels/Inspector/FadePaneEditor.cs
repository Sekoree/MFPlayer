using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;
using HaCue2.Presentation;

namespace HaCue2.ViewModels;

/// <summary>
/// The FADE pane: targets, level, duration and completion behavior of a fade cue. The first
/// per-kind editor over the shared <see cref="CueEditPlumbing"/> (review F-11) - the plumbing owns
/// the multi-selection/undo rules, this class owns only what a fade means.
/// </summary>
public sealed partial class FadePaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private FadeCueNode? Fade => context.Cue as FadeCueNode;
    private HaCueProject Project => context.Project;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(FadeTargets));
        OnPropertyChanged(nameof(FadeToLevelValue));
        OnPropertyChanged(nameof(FadeDurationValue));
        OnPropertyChanged(nameof(FadeEverythingValue));
        OnPropertyChanged(nameof(FadeStopsTargetsValue));
        OnPropertyChanged(nameof(FadeTargetHint));
    }

    /// <summary>
    /// The logical outputs this fade acts on, each with its own checkbox.
    /// </summary>
    /// <remarks>
    /// A live list rather than a picker dialog: choosing which outputs a fade covers is something an
    /// operator does WHILE looking at the patch, and a modal that hides the rest of the pane to ask
    /// one question is the wrong shape for it.
    /// </remarks>
    public IReadOnlyList<TargetToggle> FadeTargets =>
        Fade is not { } fade
            ? []
            : [.. Project.AudioPatch.LogicalChannels
                .OrderBy(channel => channel.SortOrder)
                .Select(channel => new TargetToggle(
                    channel.Name,
                    fade.TargetChannelIds.Contains(channel.Id),
                    on => ToggleFadeTarget(fade, channel.Id, on)))];

    private void ToggleFadeTarget(FadeCueNode fade, Guid channelId, bool on)
    {
        if (on == fade.TargetChannelIds.Contains(channelId))
            return;

        // Computed PER CUE. Ticking "Main L" on a five-fade selection adds that one channel to each of
        // them; copying the lead's finished list across would replace every other cue's own targets
        // with the lead's, which is a different edit and not the one that was asked for.
        plumbing.EditEach(fade, "fadeTargets", "cues",
            cue => cue.TargetChannelIds,
            (cue, ids) => cue.TargetChannelIds = ids,
            cue =>
            {
                var next = new List<Guid>(cue.TargetChannelIds);

                if (on)
                {
                    if (!next.Contains(channelId))
                        next.Add(channelId);
                }
                else
                {
                    next.Remove(channelId);
                }

                return next;
            },
            on ? "add fade target" : "remove fade target");
    }

    public string FadeToLevelValue
    {
        get => Fade is { } fade
            ? fade.ToLevelDb <= GainRange.SilenceFloorDb ? "−inf" : CuePresentation.Db(fade.ToLevelDb)
            : "-";
        set
        {
            if (Fade is not { } fade)
                return;

            // "−inf", "-inf" and "off" all mean the silence floor. An operator typing the word is the
            // commonest way to author a fade-out, and refusing it would send them to look up a number.
            var level = value.Trim().Replace('−', '-');

            var db = level.Equals("-inf", StringComparison.OrdinalIgnoreCase)
                     || level.Equals("inf", StringComparison.OrdinalIgnoreCase)
                     || level.Equals("off", StringComparison.OrdinalIgnoreCase)
                ? GainRange.SilenceFloorDb
                : CueEditPlumbing.TryParseDb(value, out var parsed) ? parsed : double.NaN;

            if (double.IsNaN(db))
                return;

            var target = fade;
            plumbing.EditEach(target, "fadeLevel", "cues",
                cue => cue.ToLevelDb, (cue, set) => cue.ToLevelDb = set, Math.Clamp(db, GainRange.SilenceFloorDb, 12), "set fade level");
        }
    }

    public string FadeDurationValue
    {
        get => Fade is { } fade ? CuePresentation.Seconds(fade.DurationMs) : "-";
        set
        {
            if (Fade is { } fade && CueEditPlumbing.TryParseSeconds(value, out var ms))
            {
                var target = fade;
                plumbing.EditEach(target, "fadeDuration", "cues",
                cue => cue.DurationMs, (cue, set) => cue.DurationMs = set, ms, "set fade duration");
            }
        }
    }

    public bool FadeEverythingValue
    {
        get => Fade is { FadeEverythingSounding: true };
        set
        {
            if (Fade is not { } fade || value == fade.FadeEverythingSounding)
                return;

            var target = fade;
            plumbing.EditEach(target, "fadeEverything", "cues",
                cue => cue.FadeEverythingSounding, (cue, on) => cue.FadeEverythingSounding = on, value,
                value ? "fade everything sounding" : "fade only the targets");
        }
    }

    public bool FadeStopsTargetsValue
    {
        get => Fade is not { StopTargetsWhenComplete: false };
        set
        {
            if (Fade is not { } fade || value == fade.StopTargetsWhenComplete)
                return;

            var target = fade;
            plumbing.EditEach(target, "fadeStops", "cues",
                cue => cue.StopTargetsWhenComplete, (cue, on) => cue.StopTargetsWhenComplete = on, value,
                value ? "stop targets when complete" : "leave targets running");
        }
    }

    /// <summary>Whether the fade covers anything. A fade with no target is a cue that does nothing.</summary>
    public string FadeTargetHint => Fade is not { } fade
        ? ""
        : fade.FadeEverythingSounding
            ? "everything sounding - the per-output list below is ignored"
            : fade.TargetChannelIds.Count + fade.TargetCueIds.Count == 0
                ? "no target - this cue will do nothing"
                : $"{fade.TargetChannelIds.Count + fade.TargetCueIds.Count} target(s)";
}
