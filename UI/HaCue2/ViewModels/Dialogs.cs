using HaCue2.Core.Journal;
using HaCue2.Core.Media;
using HaCue2.Core.Validation;
using HaCue2.Core.Model;
using HaCue2.Machine;
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
    public static PromptViewModel AddAudioLine(ProjectJournal journal, AudioLineKind kind) =>
        AddAudioLine(journal, kind, devices: null);

    /// <summary>
    /// A new output line, with a real device picker when the machine could be asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The device is CHOSEN, not typed.</b> This box enumerates fifteen outputs across three driver
    /// families with names like <c>HD-Audio Generic: HDMI 0 (hw:0,3)</c> — nobody types that from
    /// memory, and a hint that does not match is a silent absence at the venue rather than an error
    /// here. With no backend to ask (a preview, a headless capture) it falls back to a free-text hint,
    /// which is also the honest thing for a show authored on a laptop for a rig it has never seen.
    /// </para>
    /// <para>
    /// <b>The host API narrows the list rather than being stored.</b> The same interface appears as
    /// "Scarlett 2i2 USB: Audio (hw:3,0)" under ALSA and "Scarlett 2i2 3rd Gen Pro" under JACK — two
    /// different names for one box, and picking the wrong one is how a show ends up on the wrong
    /// driver. What travels in the document is the NAME, because that is what the hint matches on.
    /// </para>
    /// </remarks>
    public static PromptViewModel AddAudioLine(
        ProjectJournal journal, AudioLineKind kind, AudioDevices? devices)
    {
        var name = new PromptField { Label = "Name", Value = Suggest(kind) };
        var local = kind == AudioLineKind.LocalAudio;
        var catalog = local && devices is { Enumerated: true } ? devices : null;

        var channels = new PromptField
        {
            Label = "Channels", Kind = PromptFieldKind.Number, Value = "2",
        };

        var mixRate = journal.Project.AudioPatch.MixSampleRate;

        // Left EMPTY unless there is something to say. Empty means "open at the show's mix rate",
        // which is what every line did before this field existed, so an operator who ignores it gets
        // exactly the old behaviour. A device that does not run at the mix rate is wrapped through the
        // bay's resampler; the CLOCK MASTER is the one line that cannot be, and the project status
        // pass names that rather than letting the show open with a silently re-clocked master.
        var rate = local
            ? new PromptField
            {
                Label = "Sample rate",
                Kind = PromptFieldKind.Suggestion,
                Options = CommonSampleRates,
                Value = "",
                Hint = $"empty follows the show's {mixRate:N0} Hz · set it when the device cannot",
            }
            : null;

        if (catalog is null)
        {
            var typed = new PromptField
            {
                Label = "Device",
                Value = "",
                // The hint is deliberately not an identity (see AudioLineDefinition): on another
                // machine it may match nothing, and that is a reported absence, never a silent redirect.
                Hint = "matched by name · leave empty for the default device",
            };

            return Build(
                journal, kind, name, () => typed.Value.Trim(), channels, rate,
                rate is null ? [name, typed, channels] : [name, typed, channels, rate]);
        }

        var hosts = catalog.HostApis;

        // "Any" first, so a rig with one driver family needs no decision — and so a show authored
        // against a name that exists under several of them can still be pointed at any of them.
        var hostOptions = new List<string> { "any" };
        hostOptions.AddRange(hosts);

        var host = new PromptField
        {
            Label = "Driver",
            Kind = PromptFieldKind.Choice,
            Options = hostOptions,
            Hint = "narrows the list below · not stored in the show",
        };

        var device = new PromptField
        {
            Label = "Device",
            Kind = PromptFieldKind.Choice,
            Hint = "the name the show will match on at the venue",
        };

        void Fill()
        {
            var chosen = host.Choice == "any" ? "" : host.Choice;
            var found = catalog.OutputsFor(chosen);

            // The channel count follows the device, because it is the number an operator would
            // otherwise have to look up and get wrong — the patch is built against it.
            device.Options = [.. found.Select(Label)];
            device.SelectedIndex = Math.Max(0, found.ToList().FindIndex(item => item.IsDefault));
        }

        // Everything that FOLLOWS the chosen device. Called explicitly as well as on Picked, because
        // Picked fires on a CHANGE of index and the list opens already pointing at the default device:
        // an operator who accepts that default — the common case — changed nothing, so nothing fired,
        // and the line was created with the two channels the field was born with rather than the
        // eight the device has.
        void FollowDevice()
        {
            var chosen = host.Choice == "any" ? "" : host.Choice;
            var found = catalog.OutputsFor(chosen);

            if (device.SelectedIndex < 0 || device.SelectedIndex >= found.Count)
                return;

            var picked = found[device.SelectedIndex];

            channels.Value = Math.Clamp(picked.MaxChannels, 1, 64)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (rate is null)
                return;

            // Filled in ONLY when the device's own rate differs from the show's. Prefilling every line
            // with its device rate would write an explicit rate into projects that never needed one and
            // turn a clock master into a validation error the moment its driver reported 44.1 — and
            // leaving it blank when the device genuinely cannot do the mix rate is the case the field
            // exists for. So the dialog takes a position exactly when there is a disagreement to see.
            var native = (int)Math.Round(picked.DefaultSampleRate);

            rate.Value = native > 0 && native != mixRate
                ? native.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";

            rate.Hint = native > 0 && native != mixRate
                ? $"“{picked.Name}” reports {native:N0} Hz · the show mixes at {mixRate:N0} Hz"
                : $"empty follows the show's {mixRate:N0} Hz · set it when the device cannot";
        }

        Fill();
        FollowDevice();

        host.Picked += _ =>
        {
            Fill();
            FollowDevice();
        };

        device.Picked += _ => FollowDevice();

        return Build(
            journal,
            kind,
            name,
            () =>
            {
                var chosen = host.Choice == "any" ? "" : host.Choice;
                var found = catalog.OutputsFor(chosen);

                return device.SelectedIndex >= 0 && device.SelectedIndex < found.Count
                    ? found[device.SelectedIndex].Name
                    : "";
            },
            channels,
            rate,
            rate is null ? [name, host, device, channels] : [name, host, device, channels, rate]);
    }

    /// <summary>
    /// The rates a device picker offers. Typed values are still accepted — it is a suggestion list.
    /// </summary>
    /// <remarks>
    /// The two families and their multiples, because a device that will not do 48 k is almost always a
    /// 44.1 k one, and a rig running at 96 k has usually chosen it deliberately.
    /// </remarks>
    private static IReadOnlyList<string> CommonSampleRates { get; } =
        ["44100", "48000", "88200", "96000", "176400", "192000"];

    /// <summary>One device row: its name, its width, and whether the machine calls it the default.</summary>
    private static string Label(S.Media.Core.Audio.AudioDeviceInfo device) =>
        $"{device.Name} · {device.MaxChannels}ch{(device.IsDefault ? " · default" : "")}";

    private static PromptViewModel Build(
        ProjectJournal journal,
        AudioLineKind kind,
        PromptField name,
        Func<string> hint,
        PromptField channels,
        PromptField? rate,
        IReadOnlyList<PromptField> fields) =>
        new(
            $"Add {Describe(kind)} line",
            "an output this show can patch to",
            fields,
            _ => journal.Do(new AddItemCommand<AudioLineDefinition>(
                journal.Project.AudioLines,
                new AudioLineDefinition
                {
                    Name = name.Value.Trim(),
                    Kind = kind,
                    DeviceHint = hint(),
                    Channels = Math.Clamp(channels.Number(2), 1, 64),
                    SampleRate = NativeRate(rate),
                },
                journal.Project.AudioLines.Count,
                "audio",
                $"add line “{name.Value.Trim()}”")));

    /// <summary>
    /// A line's own sample rate, or null to follow the show's mix rate.
    /// </summary>
    /// <remarks>
    /// Null rather than the mix rate for an empty field: the two behave identically when the show
    /// opens, and null is the one that keeps FOLLOWING the mix rate if it is ever changed. Writing the
    /// number would silently pin the line to whatever the rate happened to be on the day it was added.
    /// Anything unparseable or outside the range the validator accepts is treated as empty rather than
    /// stored — a typo must not become a line that fails the status pass with no way to see why.
    /// </remarks>
    private static int? NativeRate(PromptField? rate) =>
        rate is not null
        && int.TryParse(
            rate.Value.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
        && parsed is >= 8_000 and <= 384_000
            ? parsed
            : null;

    /// <summary>
    /// Confirms deleting an audio line, naming everything that goes with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirmed because it CASCADES: the patch cells on it, the snapshot cells that recall them, the
    /// patch cues that change its levels, and the clock-master or audition-rig role it may hold all go
    /// too. An operator who only meant to tidy a stale line would otherwise find out at the next
    /// recall, or when the rig came up on the wrong speakers.
    /// </para>
    /// <para>
    /// The consequences are COUNTED from the document, not described in general terms — "removes 4
    /// patch cells" is something an operator can weigh, and "may affect the patch" is not.
    /// </para>
    /// </remarks>
    public static PromptViewModel? RemoveAudioLine(ProjectJournal journal, Guid? lineId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (lineId is not { } id || journal.Project.FindLine(id) is not { } line)
            return null;

        var references = ProjectReferences.To(journal.Project, ProjectReferences.AudioLine, id);

        return new PromptViewModel(
            $"Remove “{line.Name}”?",
            references.Count == 0
                ? "nothing in this show points at it"
                : string.Join(" · ", references.Select(reference => reference.Description)),
            // No fields: this is a question, not a form. The prompt shell renders title, consequences
            // and the two buttons, which is the whole of what a confirmation is.
            [],
            _ => ProjectEdits.DeleteAudioLine(journal, id),
            confirm: "REMOVE");
    }

    /// <summary>
    /// Confirms deleting a logical output, naming everything that goes with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register item 11's cascade, finally reachable. <c>ProjectEdits.DeleteLogicalChannel</c> has
    /// existed — cleaning up patch cells, cue sends, snapshot cells, group membership, patch-cue levels
    /// and fade targets as ONE undoable edit — with nothing in the app calling it, while the pane's own
    /// footer advertised the behaviour. An operator could add a logical output and never remove one.
    /// </para>
    /// <para>
    /// The consequences are COUNTED from the document rather than described in general terms: "removes
    /// 4 patch cells and 2 cue sends" is something an operator can weigh, and "may affect your show" is
    /// not.
    /// </para>
    /// </remarks>
    public static PromptViewModel? RemoveLogicalOutput(ProjectJournal journal, Guid? channelId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (channelId is not { } id || journal.Project.FindChannel(id) is not { } channel)
            return null;

        var references = ProjectReferences.To(journal.Project, ProjectReferences.LogicalOutput, id);

        return new PromptViewModel(
            $"Remove “{channel.Name}”?",
            references.Count == 0
                ? "nothing in this show points at it"
                : string.Join(" · ", references.Select(reference => reference.Description)),
            [],
            _ =>
            {
                ProjectEdits.DeleteLogicalChannel(journal, id);
                journal.CloseGroup();
            },
            confirm: "REMOVE");
    }

    /// <summary>
    /// Confirms deleting an output group.
    /// </summary>
    /// <remarks>
    /// Not a cascade, and the dialog says so: grouping is an editing convenience (register item 9) and
    /// the mix math is strictly per channel, so removing a group leaves every output and every cell
    /// exactly where it was. Only the linked-nudge behaviour goes.
    /// </remarks>
    public static PromptViewModel? RemoveOutputGroup(ProjectJournal journal, Guid? groupId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var patch = journal.Project.AudioPatch;

        if (groupId is not { } id
            || patch.Groups.FirstOrDefault(group => group.Id == id) is not { } group)
            return null;

        return new PromptViewModel(
            $"Remove group “{group.Name}”?",
            $"{group.MemberIds.Count} output(s) stay exactly as they are — only the link between them goes",
            [],
            _ =>
            {
                journal.Do(new RemoveItemCommand<OutputGroup>(
                    patch.Groups, group, "outputs", $"delete group “{group.Name}”"));
                journal.CloseGroup();
            },
            confirm: "REMOVE");
    }

    /// <summary>
    /// Confirms deleting a patch snapshot, counting the cues that recall it.
    /// </summary>
    /// <remarks>
    /// The count is the whole point: a snapshot with a patch cue pointing at it is a cue that will fire
    /// during the show and do nothing, which is the failure that only shows up in front of an audience.
    /// </remarks>
    public static PromptViewModel? RemoveSnapshot(ProjectJournal journal, Guid? snapshotId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;

        if (snapshotId is not { } id
            || project.PatchSnapshots.FirstOrDefault(snapshot => snapshot.Id == id) is not { } snapshot)
            return null;

        var references = ProjectReferences.To(project, ProjectReferences.Snapshot, id);

        return new PromptViewModel(
            $"Remove snapshot “{snapshot.Name}”?",
            references.Count == 0
                ? $"{snapshot.Cells.Count} stored cell(s) · no cue recalls it"
                : string.Join(" · ", references.Select(reference => reference.Description)),
            [],
            _ =>
            {
                journal.Do(new RemoveItemCommand<PatchSnapshot>(
                    project.PatchSnapshots, snapshot, "patch", $"delete snapshot “{snapshot.Name}”"));
                journal.CloseGroup();
            },
            confirm: "REMOVE");
    }

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
                // TYPED, not picked. The composition pane edits size and rate as free text, so a
                // dropdown here taught the operator that the common rates were the only ones — and a
                // projector at 23.976 or a LED wall at 47.95 is an ordinary thing to have to match.
                // 60 — the same default CompositionDefinition itself carries. This field said 30, so
                // every composition made through the dialog contradicted the model's own default and
                // ran a 60p source at half rate unless the operator noticed and retyped it.
                new PromptField
                {
                    Label = "Rate",
                    Kind = PromptFieldKind.Text,
                    Value = "60",
                    Hint = "frames per second · 23.976 · 25 · 29.97 · 30 · 50 · 59.94 · 60",
                },
            ],
            prompt => journal.Do(new AddItemCommand<CompositionDefinition>(
                project.Compositions,
                new CompositionDefinition
                {
                    Name = prompt["Name"].Value.Trim(),
                    Width = Math.Clamp(prompt["Width"].Number(1920), 16, 16384),
                    Height = Math.Clamp(prompt["Height"].Number(1080), 16, 16384),
                    // Comma or point, because a keyboard set to German types one and the invariant
                    // parse wants the other — and a rate silently falling back to 30 is a canvas that
                    // does not match the screen it was authored for.
                    FramesPerSecond = double.TryParse(
                        prompt["Rate"].Value.Trim().Replace(',', '.'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var rate) && rate is > 0 and <= 480 ? rate : 60,
                },
                project.Compositions.Count,
                "video",
                $"add composition “{prompt["Name"].Value.Trim()}”")));
    }

    /// <summary>
    /// A new video output: something this machine can put a picture on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It asks nothing about compositions.</b> An output is a piece of the RIG — a screen, a sender,
    /// a recorder — and it exists before any canvas is authored against it. This dialog used to open
    /// with a "Shows" picker, so the first thing an operator did on a new project was answer a question
    /// about a composition that did not exist yet; the picker was empty, the output was created showing
    /// nothing, and there was no hint that the two were meant to be joined later. Assignment now lives
    /// on the COMPOSITIONS pane, where an operator can see both ends of it.
    /// </para>
    /// <para>
    /// <b>The screen is stored as a number.</b> The picker's labels read "2 · 1920×1080" and the whole
    /// label used to be written into the hint, which every reader of it then failed to parse — so the
    /// chosen screen was silently discarded and the window opened wherever SDL felt like.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The window sizes worth offering, smallest-monitor-first.
    /// </summary>
    /// <remarks>
    /// A monitoring window, not a canvas: an operator windowing an output is nearly always putting a
    /// confidence view on the booth screen beside everything else, so the small sizes come first. The
    /// full list belongs to the output pane, which is where a raster is chosen.
    /// </remarks>
    public static IReadOnlyList<string> CommonWindowSizes { get; } =
    [
        "960×540",
        "1280×720",
        "1920×1080",
        "1024×768",
    ];

    public static PromptViewModel AddVideoOutput(
        ProjectJournal journal, VideoOutputKind kind, IReadOnlyList<string> screens)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(screens);

        var project = journal.Project;

        var target = new PromptField
        {
            Label = "Target",
            Kind = kind == VideoOutputKind.LocalScreen ? PromptFieldKind.Choice : PromptFieldKind.Text,
            Options = kind == VideoOutputKind.LocalScreen ? screens : [],
            Hint = Target(kind),
        };

        List<PromptField> fields =
        [
            new() { Label = "Name", Value = Suggest(kind) },
            target,
            new()
            {
                Label = "Required",
                Kind = PromptFieldKind.Toggle,
                // Register item 25: a REQUIRED output that is absent is an error, not a warning.
                Hint = "absent on the night = an error, not a warning",
            },
        ];

        if (kind == VideoOutputKind.LocalScreen)
        {
            // Fullscreen ALREADY existed on the model and defaulted to true, with no way to reach it —
            // so every local output was created fullscreen and there was no windowed option at all.
            var windowed = new PromptField
            {
                Label = "Presentation",
                Kind = PromptFieldKind.Choice,
                Options = ["fullscreen", "windowed"],
                // Both open the moment they are added to a running show. The hint used to name only
                // the windowed one, which read as a promise that fullscreen behaved differently.
                Hint = "either opens as soon as it is added, if the show is running",
            };

            var size = new PromptField
            {
                Label = "Window size",
                Kind = PromptFieldKind.Suggestion,
                Options = CommonWindowSizes,
                Value = "",
                Hint = "empty takes the composition's own size",
                IsEnabled = false,
            };

            // The two are mutually exclusive and each dialog used to offer both at once: a fullscreen
            // output takes the SCREEN's size, so a window size typed beside it did nothing, and a
            // windowed one opens where the desktop puts it, so the screen picker did nothing either.
            // Whichever the presentation makes meaningless is greyed rather than hidden — the value
            // survives a change of mind, and the dialog keeps its shape while it is being filled in.
            void Follow(PromptField _)
            {
                var isFullscreen = windowed.Choice != "windowed";
                size.IsEnabled = !isFullscreen;
                target.IsEnabled = isFullscreen;
            }

            windowed.Picked += Follow;
            Follow(windowed);

            fields.Insert(2, windowed);
            fields.Insert(3, size);
        }

        return new PromptViewModel(
            $"Add {Describe(kind)} output",
            project.Compositions.Count == 0
                ? "a screen, a sender or a recorder · send a composition to it once you have one"
                : "a screen, a sender or a recorder · assign it to a composition under COMPOSITIONS",
            fields,
            prompt =>
            {
                var local = kind == VideoOutputKind.LocalScreen;
                var windowed = local && prompt["Presentation"].Choice == "windowed";
                var windowSize = local ? WindowSize(prompt["Window size"].Value) : (0, 0);
                var screenSize = local ? SizeInLabel(target.Choice) : (0, 0);
                var raster = windowed ? windowSize : screenSize;

                journal.Do(new AddItemCommand<VideoOutputDefinition>(
                    project.VideoOutputs,
                    new VideoOutputDefinition
                    {
                        Name = prompt["Name"].Value.Trim(),
                        Kind = kind,
                        // The chosen screen's NUMBER, which is what every reader of the hint expects,
                        // and what the picker's label happens to start with.
                        TargetHint = target.IsChoice
                            ? ScreenHint(target)
                            : target.Value.Trim(),
                        Required = prompt["Required"].IsOn,
                        Fullscreen = !local || !windowed,
                        WindowWidth = local ? windowSize.Item1 : 0,
                        WindowHeight = local ? windowSize.Item2 : 0,
                        // The output layout can now size a feed from the selected display/window
                        // immediately. Zero remains the honest fallback when a label has no size.
                        MappingWidth = local ? raster.Item1 : 0,
                        MappingHeight = local ? raster.Item2 : 0,
                    },
                    project.VideoOutputs.Count,
                    "video",
                    $"add output “{prompt["Name"].Value.Trim()}”"));
            });
    }

    /// <summary>
    /// The screen picker's answer as a hint: a one-based display number, or empty for "anywhere".
    /// </summary>
    /// <remarks>
    /// Derived from the SELECTED INDEX rather than parsed back out of the label, because index 0 is
    /// "anywhere" and every entry after it is display N — so the number is known without reading prose
    /// that a future relabelling could change.
    /// </remarks>
    private static string ScreenHint(PromptField screens) =>
        screens.SelectedIndex <= 0
            ? ""
            : screens.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Confirms deleting a video output, naming what goes with it.
    /// </summary>
    /// <remarks>
    /// Its mapping goes too, which is the part worth stating: an operator tidying a stale projector row
    /// would otherwise lose an evening of warp work with no warning and no way to tell from the row
    /// that there was any to lose.
    /// </remarks>
    public static PromptViewModel? RemoveVideoOutput(ProjectJournal journal, Guid? outputId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        if (outputId is not { } id
            || journal.Project.VideoOutputs.FirstOrDefault(output => output.Id == id) is not { } output)
            return null;

        var composition = output.CompositionId is { } on
            ? journal.Project.Compositions.FirstOrDefault(item => item.Id == on)?.Name
            : null;

        var consequences = new List<string>();

        if (composition is not null)
            consequences.Add($"stops showing {composition}");

        if (output.Mapping.Count > 0)
            consequences.Add($"{output.Mapping.Count} mapping section(s) go with it");

        if (output.Record is not null)
            consequences.Add("its recording settings go with it");

        return new PromptViewModel(
            $"Remove “{output.Name}”?",
            consequences.Count == 0 ? "nothing else in this show points at it" : string.Join(" · ", consequences),
            [],
            _ =>
            {
                journal.Do(new RemoveItemCommand<VideoOutputDefinition>(
                    journal.Project.VideoOutputs, output, "video", $"delete output “{output.Name}”"));
                journal.CloseGroup();
            },
            confirm: "REMOVE");
    }

    /// <summary>
    /// Confirms deleting a composition, and takes the placements and output bindings with it.
    /// </summary>
    /// <remarks>
    /// One undoable edit, like deleting a logical output is. A composition is referenced from two
    /// directions — cues placed ON it and outputs fed BY it — and leaving either behind gives the
    /// validator a dangling reference to a canvas that no longer exists.
    /// </remarks>
    public static PromptViewModel? RemoveComposition(ProjectJournal journal, Guid? compositionId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;

        if (compositionId is not { } id
            || project.Compositions.FirstOrDefault(item => item.Id == id) is not { } composition)
            return null;

        var placed = project.AllCues()
            .Where(cue => CuePlacements.ListOf(cue) is not null)
            .SelectMany(cue => CuePlacements.Of(cue).Select(placement => (cue, placement)))
            .Where(item => item.placement.CompositionId == id)
            .ToList();

        var fed = project.VideoOutputs.Where(output => output.CompositionId == id).ToList();

        var consequences = new List<string>();

        if (placed.Count > 0)
            consequences.Add($"{placed.Count} cue placement(s) removed");

        if (fed.Count > 0)
            consequences.Add($"{fed.Count} output(s) stop showing anything");

        return new PromptViewModel(
            $"Remove “{composition.Name}”?",
            consequences.Count == 0 ? "nothing in this show is on it" : string.Join(" · ", consequences),
            [],
            _ =>
            {
                using (journal.Composite($"delete composition “{composition.Name}”", "video"))
                {
                    foreach (var (cue, placement) in placed)
                    {
                        if (CuePlacements.ListOf(cue) is { } placements)
                            journal.Do(new RemoveItemCommand<LayerPlacement>(
                                placements, placement, "cues",
                                $"remove placement from Q{cue.Number}"));
                    }

                    // The outputs SURVIVE — they are pieces of the rig, not of the canvas. They simply
                    // stop showing anything, which is the state a freshly added output is already in.
                    foreach (var output in fed)
                    {
                        var target = output;
                        journal.Do(new SetValueCommand<Guid?>(
                            target.Id, "composition", "video",
                            () => target.CompositionId, value => target.CompositionId = value, null,
                            $"“{target.Name}” shows nothing"));
                    }

                    journal.Do(new RemoveItemCommand<CompositionDefinition>(
                        project.Compositions, composition, "video",
                        $"delete composition “{composition.Name}”"));
                }

                journal.CloseGroup();
            },
            confirm: "REMOVE");
    }

    /// <summary>Renames anything that has a name and an id, through the journal.</summary>
    public static PromptViewModel? RenameTo(
        ProjectJournal journal,
        string current,
        string domain,
        Func<string> read,
        Action<string> write,
        Guid id)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        return new PromptViewModel(
            "Rename",
            current,
            [new PromptField { Label = "Name", Value = current }],
            prompt =>
            {
                var name = prompt["Name"].Value.Trim();

                if (name.Length == 0 || name == read())
                    return;

                journal.Do(new SetValueCommand<string>(
                    id, "name", domain, read, write, name, $"rename to “{name}”"));
                journal.CloseGroup();
            },
            confirm: "RENAME");
    }

    /// <summary>
    /// A typed "960×540" as a size, or zeros for anything else.
    /// </summary>
    /// <remarks>
    /// Any of ×, x or a space between them, because all three are what somebody types. Zeros mean "the
    /// composition's own size", which is also what an empty box means — so a half-typed value opens the
    /// window at the canvas size rather than at something arbitrary.
    /// </remarks>
    public static (int Width, int Height) WindowSize(string text)
    {
        var parts = (text ?? "").Split(['×', 'x', 'X', ' ', '*'], StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 2
               && int.TryParse(parts[0], out var width)
               && int.TryParse(parts[1], out var height)
               && width is > 0 and <= 16384
               && height is > 0 and <= 16384
            ? (width, height)
            : (0, 0);
    }

    /// <summary>The first valid raster embedded in a label such as "2 · 1920×1080 · primary".</summary>
    public static (int Width, int Height) SizeInLabel(string? label)
    {
        foreach (var part in (label ?? "").Split('·', StringSplitOptions.TrimEntries))
        {
            var size = WindowSize(part);
            if (size is { Width: > 0, Height: > 0 })
                return size;
        }

        return (0, 0);
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

    /// <summary>
    /// Edits an action endpoint's address in place.
    /// </summary>
    /// <remarks>
    /// The add dialog asks four questions and there was no way to revisit any of them: a desk that
    /// moved to a new IP meant deleting the endpoint and re-pointing every cue that used it.
    /// </remarks>
    public static PromptViewModel? EditEndpoint(ProjectJournal journal, Guid? endpointId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;

        if (endpointId is not { } id
            || project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == id) is not { } endpoint)
            return null;

        var osc = endpoint.Kind == EndpointKind.OscOut;

        return new PromptViewModel(
            $"Edit “{endpoint.Name}”",
            osc ? "OSC output" : "MIDI output",
            [
                new PromptField { Label = "Name", Value = endpoint.Name },
                new PromptField
                {
                    Label = "Host",
                    Value = endpoint.Host,
                    Hint = osc ? "" : "MIDI device name — matched as a hint, like an audio line",
                },
                new PromptField
                {
                    Label = "Port",
                    Kind = PromptFieldKind.Number,
                    Value = endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Hint = osc ? "" : "not used by MIDI",
                },
                new PromptField
                {
                    Label = "Test",
                    Value = endpoint.TestMessage,
                    Hint = "the payload the TEST button sends to THIS endpoint",
                },
            ],
            prompt =>
            {
                // ONE undo step for the whole address: a half-applied endpoint — new host, old port —
                // is a destination that exists on no network.
                using var scope = journal.Composite($"edit endpoint “{endpoint.Name}”", "targets");

                Set(journal, endpoint.Id, "name", "targets",
                    () => endpoint.Name, value => endpoint.Name = value, prompt["Name"].Value.Trim());
                Set(journal, endpoint.Id, "host", "targets",
                    () => endpoint.Host, value => endpoint.Host = value, prompt["Host"].Value.Trim());
                Set(journal, endpoint.Id, "test", "targets",
                    () => endpoint.TestMessage, value => endpoint.TestMessage = value,
                    prompt["Test"].Value.Trim());

                var port = Math.Clamp(prompt["Port"].Number(), 0, 65535);
                if (endpoint.Port != port)
                    journal.Do(new SetValueCommand<int>(
                        endpoint.Id, "port", "targets",
                        () => endpoint.Port, value => endpoint.Port = value, port, "set port"));
            },
            confirm: "SAVE");
    }

    /// <summary>Writes one string field, and nothing at all when it did not change.</summary>
    private static void Set(
        ProjectJournal journal,
        Guid id,
        string field,
        string domain,
        Func<string> read,
        Action<string> write,
        string value)
    {
        if (read() == value)
            return;

        journal.Do(new SetValueCommand<string>(
            id, field, domain, read, write, value, $"set {field}"));
    }

    /// <summary>Confirms deleting an action endpoint, counting the cues aimed at it.</summary>
    public static PromptViewModel? RemoveEndpoint(ProjectJournal journal, Guid? endpointId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;

        if (endpointId is not { } id
            || project.ActionEndpoints.FirstOrDefault(endpoint => endpoint.Id == id) is not { } endpoint)
            return null;

        var aimed = project.AllCues()
            .Count(cue => cue is ActionCueNode action && action.EndpointId == id);

        return new PromptViewModel(
            $"Remove “{endpoint.Name}”?",
            aimed == 0
                ? "no cue sends to it"
                : $"{aimed} action cue(s) will have nowhere to send — they are kept, and reported",
            [],
            _ =>
            {
                journal.Do(new RemoveItemCommand<ActionEndpoint>(
                    project.ActionEndpoints, endpoint, "targets", $"delete endpoint “{endpoint.Name}”"));
                journal.CloseGroup();
            },
            confirm: "REMOVE");
    }

    /// <summary>
    /// Edits a trigger input's device and port in place.
    /// </summary>
    /// <remarks>
    /// A MIDI controller swapped for another model is the ordinary case, and re-adding the input would
    /// take every binding on it with it.
    /// </remarks>
    public static PromptViewModel? EditTriggerInput(ProjectJournal journal, Guid? inputId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;

        if (inputId is not { } id
            || project.TriggerInputs.FirstOrDefault(input => input.Id == id) is not { } input)
            return null;

        var wired = input.Kind is TriggerInputKind.MidiIn or TriggerInputKind.OscIn;

        return new PromptViewModel(
            $"Edit “{input.Name}”",
            $"{input.Bindings.Count} binding(s) stay with it",
            wired
                ?
                [
                    new PromptField { Label = "Name", Value = input.Name },
                    new PromptField
                    {
                        Label = "Device",
                        Value = input.DeviceHint,
                        Hint = input.Kind == TriggerInputKind.OscIn ? "any sender" : "matched by name",
                    },
                    new PromptField
                    {
                        Label = "Port",
                        Kind = PromptFieldKind.Number,
                        Value = input.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                ]
                : [new PromptField { Label = "Name", Value = input.Name }],
            prompt =>
            {
                using var scope = journal.Composite($"edit input “{input.Name}”", "targets");

                Set(journal, input.Id, "name", "targets",
                    () => input.Name, value => input.Name = value, prompt["Name"].Value.Trim());

                if (!wired)
                    return;

                Set(journal, input.Id, "deviceHint", "targets",
                    () => input.DeviceHint, value => input.DeviceHint = value,
                    prompt["Device"].Value.Trim());

                var port = Math.Clamp(prompt["Port"].Number(), 0, 65535);
                if (input.Port != port)
                    journal.Do(new SetValueCommand<int>(
                        input.Id, "port", "targets",
                        () => input.Port, value => input.Port = value, port, "set port"));
            },
            confirm: "SAVE");
    }

    /// <summary>Confirms deleting a trigger input, counting the bindings that go with it.</summary>
    public static PromptViewModel? RemoveTriggerInput(ProjectJournal journal, Guid? inputId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var project = journal.Project;

        if (inputId is not { } id
            || project.TriggerInputs.FirstOrDefault(input => input.Id == id) is not { } input)
            return null;

        return new PromptViewModel(
            $"Remove “{input.Name}”?",
            input.Bindings.Count == 0
                ? "nothing is bound to it"
                : $"{input.Bindings.Count} binding(s) go with it — the cues themselves stay",
            [],
            _ =>
            {
                journal.Do(new RemoveItemCommand<TriggerInputDefinition>(
                    project.TriggerInputs, input, "targets", $"delete input “{input.Name}”"));
                journal.CloseGroup();
            },
            confirm: "REMOVE");
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
                TriggerInputKind.Keyboard => "Add keyboard input",
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
                TriggerInputKind.Keyboard =>
                    "local key gestures · learned from the main window",
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

                // Quiet: a renumber rewrites every cue in the list, and each command would otherwise
                // re-run the shell's whole status pass.
                using var scope = journal.Composite($"renumber “{list.Name}”", "cues", quiet: true);
                CueRenumber.Apply(journal, list.Cues, start: start, step: step);
            },
            confirm: "RENUMBER");
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

    // ── source cues ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A cue that plays an NDI sender.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name can be PICKED or TYPED, and both are first-class. Picked, because NDI names are
    /// "STUDIO-PC (CAM 1)" — parentheses, spaces and a machine name nobody types correctly from
    /// memory. Typed, because the camera is very often not on the network yet: a show authored in an
    /// office for a rig that arrives on the day must be able to name a sender that does not exist, and
    /// a dialog that only offered a list would make that impossible.
    /// </para>
    /// <para>
    /// The scan is passed in rather than run here. It blocks for a second or two, and a view-model
    /// factory is the wrong place to decide the operator can wait.
    /// </para>
    /// </remarks>
    public static PromptViewModel NdiSourceCue(
        CuesViewModel cues, NdiSources.Scan scan, MediaCueNode? editing = null)
    {
        var existing = editing is null ? new NdiSourceOptions("") : SourceUri.ParseNdi(editing.MediaPath);

        var name = new PromptField
        {
            Label = "Name",
            Value = editing?.Label ?? (scan.Names.Count > 0 ? scan.Names[0] : "NDI input"),
        };

        var sender = new PromptField
        {
            Label = "Sender",
            Value = existing.Name,
            Hint = scan.Note,
        };

        var fields = new List<PromptField> { name, sender };

        if (scan.Names.Count > 0)
        {
            var found = new PromptField
            {
                Label = "Found",
                Kind = PromptFieldKind.Choice,
                Options = scan.Names,
                Hint = "fills the sender above · the typed name is what the show stores",
            };

            // The list FILLS the field rather than replacing it, so a picked name can then be edited —
            // which is how an operator names next week's camera after this week's.
            found.Picked += _ =>
            {
                sender.Value = found.Choice;
                if (name.Value.Trim().Length == 0)
                    name.Value = found.Choice;
            };

            var at = scan.Names.ToList().IndexOf(existing.Name);
            found.SelectedIndex = at >= 0 ? at : 0;
            if (sender.Value.Length == 0)
                sender.Value = found.Choice;

            fields.Insert(1, found);
        }

        var audio = new PromptField
        {
            Label = "Audio", Kind = PromptFieldKind.Toggle, IsOn = existing.Audio,
        };

        var video = new PromptField
        {
            Label = "Video", Kind = PromptFieldKind.Toggle, IsOn = existing.Video,
            Hint = "a sender carries both · take only the half this cue needs",
        };

        var proxy = new PromptField
        {
            Label = "Low bandwidth", Kind = PromptFieldKind.Toggle, IsOn = existing.LowBandwidth,
            Hint = "the sender's preview stream — a fraction of the bandwidth, at preview resolution",
        };

        var buffer = new PromptField
        {
            Label = "Audio buffer",
            Kind = PromptFieldKind.Number,
            Value = existing.AudioBufferMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            Hint = "ms · empty takes the framework default · raise it if the line crackles",
        };

        fields.AddRange([audio, video, proxy, buffer]);

        return new PromptViewModel(
            editing is null ? "Add NDI input cue" : "Edit NDI input",
            "a live sender on the network",
            fields,
            _ =>
            {
                var options = new NdiSourceOptions(sender.Value.Trim())
                {
                    // Both off is not a source. Refusing would lose everything else typed, so the
                    // half an operator most likely meant — the pictures — comes back on.
                    Audio = audio.IsOn || !video.IsOn,
                    Video = video.IsOn || !audio.IsOn,
                    LowBandwidth = proxy.IsOn,
                    AudioBufferMs = buffer.Value.Trim().Length == 0
                        ? null
                        : Math.Clamp(buffer.Number(0), 0, 2_000),
                };

                Commit(cues, editing, SourceUri.Ndi(options), name.Value);
            },
            confirm: editing is null ? "ADD" : "SAVE");
    }

    /// <summary>
    /// A cue that plays a capture device on this machine — a microphone, a line in, a loopback.
    /// </summary>
    /// <remarks>
    /// The host API narrows the list and IS stored, unlike the output-line picker: the same interface
    /// appears under ALSA and under JACK with different names, and the capture provider resolves the
    /// name against one family. Storing which one is the difference between opening the interface and
    /// opening whatever else happens to share its name.
    /// </remarks>
    public static PromptViewModel CaptureSourceCue(
        CuesViewModel cues, AudioDevices? devices, MediaCueNode? editing = null)
    {
        var existing = editing is null
            ? new CaptureSourceOptions("")
            : SourceUri.ParseCapture(editing.MediaPath);

        var catalog = devices is { Enumerated: true } ? devices : null;

        var name = new PromptField { Label = "Name", Value = editing?.Label ?? "Local input" };

        // Capture is a PortAudio provider, and the device NAMES it resolves against are PortAudio's.
        // On a machine running the other backend the list below is somebody else's names, so the cue
        // would be authored against a device the provider cannot find — said here rather than
        // discovered on GO.
        var backend = App.Backend is null || App.Backend is S.Media.Audio.PortAudio.PortAudioBackend
            ? ""
            : " · this machine runs the miniaudio backend, where capture cues cannot open — switch to"
              + " PortAudio in Settings";

        var channels = new PromptField
        {
            Label = "Channels",
            Kind = PromptFieldKind.Number,
            Value = (existing.Channels ?? 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var rate = new PromptField
        {
            Label = "Sample rate",
            Kind = PromptFieldKind.Number,
            Value = (existing.SampleRate ?? 48_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Hint = "Hz · the device's own rate avoids a resample on the way in",
        };

        if (catalog is null || catalog.Inputs.Count == 0)
        {
            // No backend to ask, or a box with no capture hardware. A name still travels: the show may
            // be authored here and run somewhere that has one.
            var typed = new PromptField
            {
                Label = "Device",
                Value = existing.Device,
                Hint = "matched by name at the venue · leave empty for the default input" + backend,
            };

            return new PromptViewModel(
                editing is null ? "Add local input cue" : "Edit local input",
                "a capture device on the machine running the show",
                [name, typed, channels, rate],
                _ => Commit(cues, editing, SourceUri.Capture(new CaptureSourceOptions(typed.Value.Trim())
                {
                    HostApi = existing.HostApi,
                    Channels = Math.Clamp(channels.Number(2), 1, 64),
                    SampleRate = Math.Clamp(rate.Number(48_000), 8_000, 192_000),
                }), name.Value),
                confirm: editing is null ? "ADD" : "SAVE");
        }

        var hostOptions = new List<string> { "any" };
        hostOptions.AddRange(catalog.InputHostApis);

        var host = new PromptField
        {
            Label = "Driver",
            Kind = PromptFieldKind.Choice,
            Options = hostOptions,
            Hint = "narrows the list below · stored, because the same box appears under several",
        };

        var device = new PromptField
        {
            Label = "Device",
            Kind = PromptFieldKind.Choice,
            Hint = "the input this cue captures from" + backend,
        };

        IReadOnlyList<S.Media.Core.Audio.AudioDeviceInfo> Found() =>
            catalog.InputsFor(host.Choice == "any" ? "" : host.Choice);

        S.Media.Core.Audio.AudioDeviceInfo? Picked()
        {
            var found = Found();

            return device.SelectedIndex >= 0 && device.SelectedIndex < found.Count
                ? found[device.SelectedIndex]
                : null;
        }

        // The width and the rate FOLLOW the device: they are the two numbers an operator would
        // otherwise have to look up, and a wrong one is refused at open time rather than here.
        void Follow()
        {
            if (Picked() is not { } picked)
                return;

            channels.Value = Math.Clamp(picked.MaxChannels, 1, 64)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (picked.DefaultSampleRate > 0)
                rate.Value = ((int)Math.Round(picked.DefaultSampleRate))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (name.Value.Trim().Length == 0)
                name.Value = picked.Name;
        }

        void Fill()
        {
            var found = Found();
            device.Options = [.. found.Select(InputLabel)];
            device.SelectedIndex = Math.Max(0, found.ToList().FindIndex(item => item.IsDefault));

            // Explicitly, NOT through the Picked event: narrowing to a driver whose first device is
            // already at the selected index changes the DEVICE without changing the INDEX, and the
            // cue would then carry the previous device's channel count.
            Follow();
        }

        Fill();
        host.Picked += _ => Fill();
        device.Picked += _ => Follow();

        // Re-select what is being edited, if it is still on the machine.
        var current = Found().ToList().FindIndex(item =>
            string.Equals(item.Name, existing.Device, StringComparison.OrdinalIgnoreCase));
        if (current >= 0)
            device.SelectedIndex = current;

        return new PromptViewModel(
            editing is null ? "Add local input cue" : "Edit local input",
            "a capture device on this machine",
            [name, host, device, channels, rate],
            _ =>
            {
                var picked = Picked();

                Commit(cues, editing, SourceUri.Capture(new CaptureSourceOptions(picked?.Name ?? "")
                {
                    HostApi = picked?.HostApi ?? "",
                    DeviceIndex = int.TryParse(picked?.Id, out var index) ? index : null,
                    Channels = Math.Clamp(channels.Number(2), 1, 64),
                    SampleRate = Math.Clamp(rate.Number(48_000), 8_000, 192_000),
                }), name.Value);
            },
            confirm: editing is null ? "ADD" : "SAVE");
    }

    /// <summary>One capture device row: its name, its width, and whether the machine calls it the default.</summary>
    private static string InputLabel(S.Media.Core.Audio.AudioDeviceInfo device) =>
        $"{device.Name} · {device.MaxChannels}ch{(device.IsDefault ? " · default" : "")}";

    /// <summary>Adds the cue, or repoints the one being edited. The one place either path ends.</summary>
    private static void Commit(CuesViewModel cues, MediaCueNode? editing, string uri, string label)
    {
        if (editing is null)
            cues.AddSourceCue(uri, label);
        else
            cues.SetSource(editing.Id, uri, label, editing.SourceDurationMs);
    }

    private static string Describe(AudioLineKind kind) => kind switch
    {
        AudioLineKind.LocalAudio => "local audio",
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
        AudioLineKind.LocalAudio => "Local output",
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
