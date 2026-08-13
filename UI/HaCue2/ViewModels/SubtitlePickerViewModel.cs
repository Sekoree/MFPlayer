using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Machine;

namespace HaCue2.ViewModels;

/// <summary>One row in the subtitle picker: a track in the file, or a sidecar somebody added.</summary>
public sealed partial class SubtitleChoice : ObservableObject
{
    public required string Label { get; init; }

    /// <summary>Empty for an embedded track; a file path for a sidecar.</summary>
    public string Path { get; init; } = "";

    /// <summary>−1 for a sidecar; the container stream index for an embedded track.</summary>
    public int StreamIndex { get; init; } = -1;

    /// <summary>What the track was when it was picked, for the same re-mux reason as audio.</summary>
    public string Signature { get; init; } = "";

    public bool IsSidecar => Path.Length > 0;

    [ObservableProperty]
    private bool _isChosen;
}

/// <summary>
/// Which subtitle tracks a cue shows.
/// </summary>
/// <remarks>
/// A LIST, not a single choice: a show routinely runs an embedded track for one language and a
/// hand-corrected sidecar for another, and both have to be on at once. Nothing is checked by default -
/// subtitles appearing because a file happened to carry them is the surprise this avoids.
/// </remarks>
public sealed partial class SubtitlePickerViewModel : ObservableObject
{
    private readonly ProjectJournal? _journal;
    private readonly MediaCueNode? _cue;

    /// <summary>The preview shape, for a dialog opened with no cue behind it.</summary>
    public SubtitlePickerViewModel()
    {
    }

    public SubtitlePickerViewModel(ProjectJournal journal, MediaCueNode cue, MediaFacts? facts)
    {
        _journal = journal;
        _cue = cue;

        var chosen = cue.Subtitles;

        Choices =
        [
            .. (facts?.SubtitleTracks ?? []).Select(track => new SubtitleChoice
            {
                Label = track.Label,
                StreamIndex = track.Index,
                Signature = track.Signature,
                IsChosen = chosen.Any(selection => selection.Path.Length == 0
                                                   && selection.StreamIndex == track.Index),
            }),

            // Sidecars the cue already carries are listed even though nothing probed them - the choice
            // was made and must not vanish because the file is on another machine.
            .. chosen.Where(selection => selection.Path.Length > 0).Select(selection => new SubtitleChoice
            {
                Label = System.IO.Path.GetFileName(selection.Path),
                Path = selection.Path,
                Signature = selection.Signature,
                IsChosen = true,
            }),
        ];

        Title = $"Subtitles · {cue.Label}";
    }

    public string Title { get; } = "Subtitles";

    public IReadOnlyList<SubtitleChoice> Choices { get; private set; } = [];

    public bool HasChoices => Choices.Count > 0;

    public string Hint => HasChoices
        ? "none are on by default · several can run at once"
        : "this file carries no subtitle tracks - add a sidecar file";

    /// <summary>Adds a sidecar file to the list, already checked.</summary>
    public void AddSidecar(string path)
    {
        if (path.Length == 0 || Choices.Any(choice => choice.Path == path))
            return;

        Choices = [.. Choices, new SubtitleChoice
        {
            Label = System.IO.Path.GetFileName(path),
            Path = path,
            IsChosen = true,
        }];

        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>
    /// Writes the whole selection as one undo step.
    /// </summary>
    /// <remarks>
    /// The list is replaced rather than diffed: it is a handful of entries, and "the subtitles are now
    /// these" is the edit the operator made - an undo that walked back individual check marks would
    /// step through states nobody chose.
    /// </remarks>
    public void Commit()
    {
        if (_journal is null || _cue is null)
            return;

        List<SubtitleSelection> chosen =
        [
            .. Choices.Where(choice => choice.IsChosen).Select(choice => new SubtitleSelection
            {
                Path = choice.Path,
                StreamIndex = choice.StreamIndex,
                Signature = choice.Signature,
            }),
        ];

        var cue = _cue;

        _journal.Do(new SetValueCommand<List<SubtitleSelection>>(
            cue.Id, "subtitles", "cues",
            () => cue.Subtitles, selections => cue.Subtitles = selections, chosen,
            chosen.Count == 0 ? "no subtitles" : $"{chosen.Count} subtitle track(s)"));

        _journal.CloseGroup();
    }
}
