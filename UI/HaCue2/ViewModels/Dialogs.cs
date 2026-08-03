using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Core.Patch;

namespace HaCue2.ViewModels;

/// <summary>
/// Every "…" button in the app, as a prompt plus the journaled edit it performs.
/// </summary>
/// <remarks>
/// <para>
/// Gathered in one file on purpose. These are the app's whole vocabulary of "make a new thing", and
/// keeping them together is what makes it obvious that a new audio line and a new video output ask for
/// the same shape of information — and that every one of them ends in a command rather than a direct
/// mutation.
/// </para>
/// <para>
/// Each returns a <see cref="PromptViewModel"/> or null. Null means the button had nothing to act on
/// (no selection, nothing to add to), and the caller simply does not open a dialog — which is quieter
/// than a modal that says "select something first".
/// </para>
/// </remarks>
public static class Dialogs
{
    // ── audio ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A new audio line: a device this show sends to.</summary>
    public static PromptViewModel AddAudioLine(ProjectJournal journal, AudioLineKind kind)
    {
        var name = new PromptField { Label = "Name", Value = Suggest(kind) };
        var hint = new PromptField
        {
            Label = "Device",
            Value = "",
            // The hint is deliberately not an identity (see AudioLineDefinition): on another machine
            // it may match nothing, and that is a reported absence rather than a silent redirect.
            Hint = "matched by name · leave empty for the default device",
        };
        var channels = new PromptField { Label = "Channels", Kind = PromptFieldKind.Number, Value = "2" };

        return new PromptViewModel(
            $"Add {Describe(kind)} line",
            "an output this show can patch to",
            [name, hint, channels],
            prompt => journal.Do(new AddItemCommand<AudioLineDefinition>(
                journal.Project.AudioLines,
                new AudioLineDefinition
                {
                    Name = prompt["Name"].Value.Trim(),
                    Kind = kind,
                    DeviceHint = prompt["Device"].Value.Trim(),
                    Channels = Math.Clamp(prompt["Channels"].Number(2), 1, 64),
                },
                journal.Project.AudioLines.Count,
                "audio",
                $"add line “{prompt["Name"].Value.Trim()}”")));
    }

    /// <summary>A new logical output — the show's own name for a destination.</summary>
    public static PromptViewModel AddLogicalOutput(ProjectJournal journal)
    {
        var patch = journal.Project.AudioPatch;

        return new PromptViewModel(
            "Add logical output",
            "what the show calls a destination, before any hardware",
            [
                new PromptField { Label = "Name", Value = "" },
                new PromptField
                {
                    Label = "Meter",
                    Kind = PromptFieldKind.Toggle,
                    IsOn = true,
                    Hint = "show this output in the summary meters",
                },
            ],
            prompt => journal.Do(new AddItemCommand<LogicalAudioChannel>(
                patch.LogicalChannels,
                new LogicalAudioChannel
                {
                    Name = prompt["Name"].Value.Trim(),
                    SortOrder = patch.LogicalChannels.Count,
                    MeterInSummary = prompt["Meter"].IsOn,
                },
                patch.LogicalChannels.Count,
                "audio",
                $"add output “{prompt["Name"].Value.Trim()}”")));
    }

    /// <summary>
    /// A new output group.
    /// </summary>
    /// <remarks>
    /// Grouping is an editing convenience only (register item 9) — the mix math stays per channel — so
    /// this creates a name and nothing else. Membership is set by selecting outputs, which is where
    /// the operator can see what they are grouping.
    /// </remarks>
    /// <summary>
    /// Two linked logical outputs and the group that pairs them, in one step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stereo pair is the single most common thing to add and the fiddliest to add correctly: two
    /// channels with the right suffixes, adjacent in bus order, and a group so a later gain nudge moves
    /// them together. Doing it by hand is three dialogs and a chance to leave them ungrouped.
    /// </para>
    /// <para>
    /// The group is NOT optional here. A pair that is not grouped is two channels that happen to be
    /// called L and R — which is exactly the state an operator discovers when a trim moves one of them.
    /// </para>
    /// </remarks>
    public static PromptViewModel AddStereoPair(ProjectJournal journal)
    {
        var patch = journal.Project.AudioPatch;

        return new PromptViewModel(
            "Add stereo pair",
            "two logical outputs and the group that keeps them together",
            [
                new PromptField { Label = "Name", Value = "", Hint = "“Main” becomes Main L and Main R" },
                new PromptField
                {
                    Label = "Meter",
                    Kind = PromptFieldKind.Toggle,
                    IsOn = true,
                    Hint = "show both outputs in the summary meters",
                },
            ],
            prompt =>
            {
                var name = prompt["Name"].Value.Trim();

                if (name.Length == 0)
                    return;

                var left = new LogicalAudioChannel
                {
                    Name = $"{name} L",
                    SortOrder = patch.LogicalChannels.Count,
                    MeterInSummary = prompt["Meter"].IsOn,
                };

                var right = new LogicalAudioChannel
                {
                    Name = $"{name} R",
                    SortOrder = patch.LogicalChannels.Count + 1,
                    MeterInSummary = prompt["Meter"].IsOn,
                };

                // ONE undo step for all three. Undoing a stereo pair has to leave no half of one
                // behind — a lone "Main R" is worse than never having pressed the button.
                using var scope = journal.Composite($"add stereo pair “{name}”", "audio");

                journal.Do(new AddItemCommand<LogicalAudioChannel>(
                    patch.LogicalChannels, left, patch.LogicalChannels.Count, "audio", $"add “{left.Name}”"));

                journal.Do(new AddItemCommand<LogicalAudioChannel>(
                    patch.LogicalChannels, right, patch.LogicalChannels.Count, "audio", $"add “{right.Name}”"));

                journal.Do(new AddItemCommand<OutputGroup>(
                    patch.Groups,
                    new OutputGroup { Name = name, MemberIds = [left.Id, right.Id] },
                    patch.Groups.Count,
                    "audio",
                    $"group “{name}”"));
            });
    }

    /// <summary>
    /// Moves one logical output to another position in bus order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bus order is POSITIONAL: a logical output's index is its channel on the program bus, which is
    /// what the V×R patch multiplies and what the meters are labelled from. So this renumbers every
    /// output rather than swapping two — leaving a gap or a duplicate in SortOrder would put two
    /// outputs on one bus channel.
    /// </para>
    /// <para>
    /// <b>The running bay does not follow.</b> Bus width and order are fixed when it opens, so a
    /// reorder is deferred exactly like a mix-rate change: the document changes now, the rig changes on
    /// "Apply &amp; restart audio". Silently reordering a live bus would move the show to different
    /// speakers mid-cue.
    /// </para>
    /// </remarks>
    public static PromptViewModel? Reorder(ProjectJournal journal, Guid? outputId)
    {
        var patch = journal.Project.AudioPatch;

        var ordered = patch.LogicalChannels.OrderBy(channel => channel.SortOrder).ToList();

        if (outputId is not { } id
            || ordered.FindIndex(channel => channel.Id == id) is var from && from < 0)
            return null;

        return new PromptViewModel(
            $"Move “{ordered[from].Name}”",
            $"bus position, 1–{ordered.Count} · takes effect on the next audio restart",
            [
                new PromptField
                {
                    Label = "Position",
                    Kind = PromptFieldKind.Number,
                    Value = (from + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            ],
            prompt =>
            {
                var to = Math.Clamp(prompt["Position"].Number(), 1, ordered.Count) - 1;

                if (to == from)
                    return;

                var moved = ordered[from];
                ordered.RemoveAt(from);
                ordered.Insert(to, moved);

                // One step for the whole renumbering: an undo that put back half the positions would
                // leave a bus order nobody authored.
                using var scope = journal.Composite(
                    $"move “{moved.Name}” to position {to + 1}", "audio");

                for (var index = 0; index < ordered.Count; index++)
                {
                    var channel = ordered[index];
                    var position = index;

                    if (channel.SortOrder == position)
                        continue;

                    journal.Do(new SetValueCommand<int>(
                        channel.Id, "sortOrder", "audio",
                        () => channel.SortOrder, value => channel.SortOrder = value, position,
                        $"reorder “{channel.Name}”"));
                }
            });
    }

    public static PromptViewModel AddOutputGroup(ProjectJournal journal, IReadOnlyList<Guid> members)
    {
        var patch = journal.Project.AudioPatch;

        return new PromptViewModel(
            "New output group",
            members.Count == 0
                ? "an empty group · select outputs to fill it"
                : $"{members.Count} selected output(s) will join it",
            [new PromptField { Label = "Name", Value = "" }],
            prompt => journal.Do(new AddItemCommand<OutputGroup>(
                patch.Groups,
                new OutputGroup { Name = prompt["Name"].Value.Trim(), MemberIds = [.. members] },
                patch.Groups.Count,
                "audio",
                $"add group “{prompt["Name"].Value.Trim()}”")));
    }

    /// <summary>Renames whatever the operator has selected.</summary>
    public static PromptViewModel? Rename(ProjectJournal journal, Guid id, string current, string domain)
    {
        if (journal.Project.FindChannel(id) is not { } channel)
            return null;

        return new PromptViewModel(
            "Rename",
            current,
            [new PromptField { Label = "Name", Value = current }],
            prompt => journal.Do(new SetValueCommand<string>(
                id, "name", domain,
                () => channel.Name, value => channel.Name = value,
                prompt["Name"].Value.Trim(),
                $"rename to “{prompt["Name"].Value.Trim()}”")),
            confirm: "RENAME");
    }

    /// <summary>
    /// Saves the live patch as a snapshot.
    /// </summary>
    /// <remarks>
    /// The whole patch, not a selection: a snapshot that captured part of the state would recall into
    /// a mixture of two rigs, which is the state nobody can reason about mid-show.
    /// </remarks>
    public static PromptViewModel SaveSnapshot(ProjectJournal journal)
    {
        var project = journal.Project;

        return new PromptViewModel(
            "Save patch snapshot",
            $"captures all {project.AudioPatch.Cells.Count} live cell(s)",
            [new PromptField { Label = "Name", Value = "" }],
            prompt => journal.Do(new AddItemCommand<PatchSnapshot>(
                project.PatchSnapshots,
                new PatchSnapshot
                {
                    Name = prompt["Name"].Value.Trim(),
                    Cells = [.. PatchOperations.Capture(project)],
                },
                project.PatchSnapshots.Count,
                "patch",
                $"save snapshot “{prompt["Name"].Value.Trim()}”")),
            confirm: "SAVE");
    }

    // ── video ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A new composition: a canvas cues can be placed on.</summary>
    public static PromptViewModel AddComposition(ProjectJournal journal)
    {
        var project = journal.Project;

        return new PromptViewModel(
            "Add composition",
            "a canvas · size, rate and idle image, and nothing else (register item 21)",
            [
                new PromptField { Label = "Name", Value = "" },
                new PromptField { Label = "Width", Kind = PromptFieldKind.Number, Value = "1920" },
                new PromptField { Label = "Height", Kind = PromptFieldKind.Number, Value = "1080" },
                new PromptField
                {
                    Label = "Rate",
                    Kind = PromptFieldKind.Choice,
                    Options = ["25", "29.97", "30", "50", "59.94", "60"],
                    SelectedIndex = 2,
                },
            ],
            prompt => journal.Do(new AddItemCommand<CompositionDefinition>(
                project.Compositions,
                new CompositionDefinition
                {
                    Name = prompt["Name"].Value.Trim(),
                    Width = Math.Clamp(prompt["Width"].Number(1920), 16, 16384),
                    Height = Math.Clamp(prompt["Height"].Number(1080), 16, 16384),
                    FramesPerSecond = double.TryParse(
                        prompt["Rate"].Choice,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var rate) ? rate : 30,
                },
                project.Compositions.Count,
                "video",
                $"add composition “{prompt["Name"].Value.Trim()}”")));
    }

    /// <summary>A new video output: something a composition is sent to.</summary>
    public static PromptViewModel AddVideoOutput(
        ProjectJournal journal, VideoOutputKind kind, IReadOnlyList<string> screens)
    {
        var project = journal.Project;
        var compositions = project.Compositions.Select(composition => composition.Name).ToList();

        List<PromptField> fields =
        [
            new() { Label = "Name", Value = Suggest(kind) },
            new()
            {
                Label = "Target",
                Kind = kind == VideoOutputKind.LocalScreen ? PromptFieldKind.Choice : PromptFieldKind.Text,
                Options = kind == VideoOutputKind.LocalScreen ? screens : [],
                Hint = Target(kind),
            },
            new()
            {
                Label = "Shows",
                Kind = PromptFieldKind.Choice,
                Options = compositions,
                Hint = compositions.Count == 0 ? "no compositions yet" : "",
            },
            new()
            {
                Label = "Required",
                Kind = PromptFieldKind.Toggle,
                // Register item 25: a REQUIRED output that is absent is an error, not a warning.
                Hint = "absent on the night = an error, not a warning",
            },
        ];

        return new PromptViewModel(
            $"Add {Describe(kind)} output",
            "where a composition is sent",
            fields,
            prompt => journal.Do(new AddItemCommand<VideoOutputDefinition>(
                project.VideoOutputs,
                new VideoOutputDefinition
                {
                    Name = prompt["Name"].Value.Trim(),
                    Kind = kind,
                    TargetHint = prompt["Target"].IsChoice
                        ? prompt["Target"].Choice
                        : prompt["Target"].Value.Trim(),
                    CompositionId = project.Compositions
                        .FirstOrDefault(composition => composition.Name == prompt["Shows"].Choice)?.Id,
                    Required = prompt["Required"].IsOn,
                },
                project.VideoOutputs.Count,
                "video",
                $"add output “{prompt["Name"].Value.Trim()}”")));
    }

    // ── targets ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A new action endpoint — somewhere action cues send to.</summary>
    public static PromptViewModel AddEndpoint(ProjectJournal journal, EndpointKind kind)
    {
        var project = journal.Project;
        var osc = kind == EndpointKind.OscOut;

        return new PromptViewModel(
            osc ? "Add OSC output" : "Add MIDI output",
            "somewhere action cues send to",
            [
                new PromptField { Label = "Name", Value = osc ? "Lighting desk" : "MIDI out" },
                // A MIDI endpoint has no host and no port: it has a device NAME, matched the way an
                // audio line's is, because ports are not stable across reboots let alone machines.
                new PromptField
                {
                    Label = "Host",
                    Value = osc ? "127.0.0.1" : "",
                    Hint = osc ? "" : "MIDI device name — matched as a hint, like an audio line",
                },
                new PromptField
                {
                    Label = "Port",
                    Kind = PromptFieldKind.Number,
                    Value = osc ? "8000" : "0",
                    Hint = osc ? "" : "not used by MIDI",
                },
                new PromptField
                {
                    Label = "Test",
                    Value = "",
                    // Register item 24: the test payload is stored PER endpoint. A generic ping proves
                    // the socket is open; it does not prove the desk understood you.
                    Hint = "the payload the TEST button sends to THIS endpoint",
                },
            ],
            prompt => journal.Do(new AddItemCommand<ActionEndpoint>(
                project.ActionEndpoints,
                new ActionEndpoint
                {
                    Name = prompt["Name"].Value.Trim(),
                    Kind = kind,
                    Host = prompt["Host"].Value.Trim(),
                    Port = Math.Clamp(prompt["Port"].Number(), 0, 65535),
                    TestMessage = prompt["Test"].Value.Trim(),
                },
                project.ActionEndpoints.Count,
                "targets",
                $"add endpoint “{prompt["Name"].Value.Trim()}”")));
    }

    /// <summary>A new trigger input — something that can fire this show.</summary>
    public static PromptViewModel AddTriggerInput(ProjectJournal journal, TriggerInputKind kind)
    {
        var project = journal.Project;

        // A schedule and a timecode source open no device, so the two device boxes have nothing to
        // ask them. Offering them anyway would suggest there is a port to get wrong.
        var wired = kind is TriggerInputKind.MidiIn or TriggerInputKind.OscIn;

        return new PromptViewModel(
            kind switch
            {
                TriggerInputKind.OscIn => "Add OSC listener",
                TriggerInputKind.Schedule => "Add schedule",
                TriggerInputKind.Timecode => "Add timecode",
                _ => "Add MIDI input",
            },
            kind switch
            {
                TriggerInputKind.Schedule =>
                    "fires on the wall clock · " + TriggerTimes.ScheduleSyntax,
                TriggerInputKind.Timecode =>
                    "fires on incoming MTC · " + TriggerTimes.TimecodeSyntax,
                _ => "external input never gates GO (register item 3)",
            },
            wired
                ?
                [
                    new PromptField { Label = "Name", Value = "" },
                    new PromptField
                    {
                        Label = "Device",
                        Value = "",
                        Hint = kind == TriggerInputKind.OscIn ? "any sender" : "matched by name",
                    },
                    new PromptField
                    {
                        Label = "Port",
                        Kind = PromptFieldKind.Number,
                        Value = kind == TriggerInputKind.OscIn ? "9000" : "0",
                    },
                ]
                : [new PromptField { Label = "Name", Value = "" }],
            prompt => journal.Do(new AddItemCommand<TriggerInputDefinition>(
                project.TriggerInputs,
                new TriggerInputDefinition
                {
                    Name = prompt["Name"].Value.Trim(),
                    Kind = kind,
                    DeviceHint = wired ? prompt["Device"].Value.Trim() : "",
                    Port = wired ? Math.Clamp(prompt["Port"].Number(), 0, 65535) : 0,
                },
                project.TriggerInputs.Count,
                "targets",
                $"add input “{prompt["Name"].Value.Trim()}”")));
    }

    // ── cues ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renumbers a list from a starting number, in steps.
    /// </summary>
    /// <remarks>
    /// One composite for the whole run: renumbering is a single decision, and an undo that walked back
    /// cue by cue would leave the list in numbering nobody chose. Groups keep their children's numbers
    /// relative to them, which is what the dotted scheme is for.
    /// </remarks>
    public static PromptViewModel? Renumber(ProjectJournal journal, CueList? list)
    {
        if (list is null || list.Cues.Count == 0)
            return null;

        return new PromptViewModel(
            $"Renumber “{list.Name}”",
            $"{list.Cues.Count} top-level cue(s) · children renumber under their group",
            [
                new PromptField { Label = "Start at", Kind = PromptFieldKind.Number, Value = "1" },
                new PromptField { Label = "Step", Kind = PromptFieldKind.Number, Value = "1" },
            ],
            prompt =>
            {
                var start = Math.Max(0, prompt["Start at"].Number(1));
                var step = Math.Max(1, prompt["Step"].Number(1));

                using var scope = journal.Composite($"renumber “{list.Name}”", "cues");
                Renumber(journal, list.Cues, prefix: null, start, step);
            },
            confirm: "RENUMBER");
    }

    private static void Renumber(
        ProjectJournal journal, IReadOnlyList<CueNode> cues, CueNumber? prefix, int start, int step)
    {
        var next = start;

        foreach (var cue in cues)
        {
            var number = prefix is { } parent
                ? parent.Child(next)
                : new CueNumber(next.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var target = cue;
            journal.Do(new SetValueCommand<CueNumber>(
                cue.Id, "number", "cues",
                () => target.Number, value => target.Number = value, number,
                $"renumber to {number}"));

            if (cue is GroupCueNode group)
                Renumber(journal, group.Children, number, 1, 1);

            next += step;
        }
    }

    /// <summary>A level change on a patch cue: one logical output moved to a level.</summary>
    public static PromptViewModel? AddLevelChange(ProjectJournal journal, PatchCueNode? cue)
    {
        var channels = journal.Project.AudioPatch.LogicalChannels;

        if (cue is null || channels.Count == 0)
            return null;

        return new PromptViewModel(
            "Add level change",
            "writes patch CELL gains · stopping the cue undoes nothing",
            [
                new PromptField
                {
                    Label = "Output",
                    Kind = PromptFieldKind.Choice,
                    Options = [.. channels.Select(channel => channel.Name)],
                },
                new PromptField
                {
                    Label = "Level",
                    Kind = PromptFieldKind.Number,
                    Value = "0",
                    Hint = $"dB · {GainRange.SilenceFloorDb} is silence",
                },
            ],
            prompt =>
            {
                var channel = channels.FirstOrDefault(item => item.Name == prompt["Output"].Choice);
                if (channel is null)
                    return;

                var level = Math.Clamp(
                    prompt["Level"].Decimal(), GainRange.SilenceFloorDb, GainRange.MaximumDb);

                journal.Do(new AddItemCommand<PatchLevelChange>(
                    cue.Levels,
                    new PatchLevelChange { LogicalChannelId = channel.Id, GainDb = level },
                    cue.Levels.Count,
                    "cues",
                    $"add level change on {channel.Name}"));
            });
    }

    // ── labels ────────────────────────────────────────────────────────────────────────────────

    private static string Describe(AudioLineKind kind) => kind switch
    {
        AudioLineKind.PortAudio => "device",
        AudioLineKind.Ndi => "NDI",
        AudioLineKind.FileRecord => "record",
        _ => "stream",
    };

    private static string Describe(VideoOutputKind kind) => kind switch
    {
        VideoOutputKind.LocalScreen => "local",
        VideoOutputKind.Ndi => "NDI",
        VideoOutputKind.Record => "record",
        _ => "stream",
    };

    private static string Suggest(AudioLineKind kind) => kind switch
    {
        AudioLineKind.PortAudio => "Interface",
        AudioLineKind.Ndi => "NDI audio",
        AudioLineKind.FileRecord => "Record",
        _ => "Stream",
    };

    private static string Suggest(VideoOutputKind kind) => kind switch
    {
        VideoOutputKind.LocalScreen => "Projector",
        VideoOutputKind.Ndi => "NDI program",
        VideoOutputKind.Record => "Record",
        _ => "Stream",
    };

    private static string Target(VideoOutputKind kind) => kind switch
    {
        VideoOutputKind.LocalScreen => "which screen on this machine",
        VideoOutputKind.Ndi => "the NDI source name other machines will see",
        VideoOutputKind.Record => "file pattern",
        _ => "stream URL",
    };
}
