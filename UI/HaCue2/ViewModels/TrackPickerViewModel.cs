using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Journal;
using HaCue2.Core.Model;
using HaCue2.Machine;

namespace HaCue2.ViewModels;

/// <summary>Which kind of track a picker chooses.</summary>
public enum TrackKind
{
    Audio,
    Video,
}

/// <summary>
/// One media-track picker: the tracks a file turned out to have, and which one this cue plays.
/// </summary>
/// <remarks>
/// <para>
/// The list is a MACHINE fact and the choice is a DOCUMENT one, which is the whole reason this class
/// exists: the options come from the probe and the selection is journaled onto the cue. A file that
/// has not been probed yet offers nothing but still shows what the cue already chose, so opening a
/// project on a machine without the media does not look like the choice was lost.
/// </para>
/// <para>
/// Index 0 is always "automatic" — the decoder elects a track. It is a real option, not a blank: most
/// files have one track and picking it explicitly would freeze an index that a re-mux can move.
/// </para>
/// </remarks>
public sealed partial class TrackPickerViewModel : ObservableObject
{
    private const string Automatic = "automatic";
    private const string NoVideo = "no video";

    private readonly ProjectJournal? _journal;
    private readonly MediaCueNode? _cue;
    private readonly TrackKind _kind;
    private readonly IReadOnlyList<MediaTrack> _tracks;
    private readonly int _coverArt;
    private readonly Action _reload;

    public TrackPickerViewModel(
        ProjectJournal? journal = null,
        MediaCueNode? cue = null,
        TrackKind kind = TrackKind.Audio,
        MediaFacts? facts = null,
        Action? reload = null)
    {
        _journal = journal;
        _cue = cue;
        _kind = kind;
        _reload = reload ?? (() => { });
        _tracks = facts is null
            ? []
            : kind == TrackKind.Audio ? facts.AudioTracks : facts.VideoTracks;

        // Cover art is a video stream in every container that carries it, so a FLAC with album art
        // probes as one video track. It stays SELECTABLE — automatic election skips it, and choosing
        // it explicitly is the only way to show one — but it is not counted as video the cue has.
        _coverArt = _tracks.Count(track => track.IsAttachedPicture);

        Options =
        [
            Automatic,
            .. kind == TrackKind.Video ? new[] { NoVideo } : [],
            .. _tracks.Select(track => track.Label),
        ];
    }

    public IReadOnlyList<string> Options { get; }

    /// <summary>Whether this cue can have a track at all — false for a cue with no media.</summary>
    public bool HasMedia => _cue is { MediaPath.Length: > 0 };

    /// <summary>Whether anything has looked at the file yet.</summary>
    public bool IsProbed => _tracks.Count > 0;

    /// <summary>How many tracks were found — what makes a multi-track file worth noticing.</summary>
    public int Count => _tracks.Count - _coverArt;

    public string Hint
    {
        get
        {
            if (!HasMedia)
                return "no media";

            if (!IsProbed)
                return "not probed yet";

            // Cover art is a STILL that can be placed, not a missing video track — saying "no tracks"
            // over an MP3 with album art would hide something the operator can legitimately put on a
            // canvas.
            if (Count == 0)
                return _coverArt > 0 ? "cover art · still image" : "no tracks";

            var cover = _coverArt == 0 ? "" : " · cover art";
            return Count == 1 ? $"one track{cover}" : $"{Count} tracks{cover}";
        }
    }

    /// <summary>
    /// True when the cue's stored index no longer names the track it was chosen for.
    /// </summary>
    /// <remarks>
    /// Re-muxing a file keeps its tracks and can renumber them. Saying so is the point: playback falls
    /// back to automatic election, and an operator who is not told will hear the wrong language and
    /// have nothing on screen to explain it.
    /// </remarks>
    public bool HasMoved
    {
        get
        {
            if (!IsProbed || StoredIndex is not { } index || index < 0 || StoredSignature.Length == 0)
                return false;

            return MediaFacts.Resolve(_tracks, index, StoredSignature) is not { } resolved
                   || resolved.Index != index;
        }
    }

    public string MovedNote => HasMoved
        ? "this track moved in the file — playing the elected one instead"
        : "";

    private int? StoredIndex => _kind == TrackKind.Audio ? _cue?.AudioTrackIndex : _cue?.VideoTrackIndex;

    private string StoredSignature =>
        (_kind == TrackKind.Audio ? _cue?.AudioTrackSignature : _cue?.VideoTrackSignature) ?? "";

    public int SelectedIndex
    {
        get
        {
            if (StoredIndex is not { } stored)
                return 0;

            if (_kind == TrackKind.Video && stored < 0)
                return 1;

            var position = _tracks.ToList().FindIndex(track => track.Index == stored);
            // A stored index the probe cannot account for still reads as automatic rather than as a
            // silent blank: the picker must never show an empty box for a choice that was made.
            return position < 0 ? 0 : position + Offset;
        }

        set
        {
            if (_journal is null || _cue is null || value < 0 || value == SelectedIndex)
                return;

            var (index, signature) = Choice(value);

            using (_journal.Composite("choose track", "cues"))
            {
                Write(_kind == TrackKind.Audio ? "audioTrack" : "videoTrack",
                    () => StoredIndex, index);
                WriteSignature(signature);
            }

            _reload();
        }
    }

    /// <summary>Where the real tracks start in <see cref="Options"/>, after automatic (and no-video).</summary>
    private int Offset => _kind == TrackKind.Video ? 2 : 1;

    private (int? Index, string Signature) Choice(int option)
    {
        if (option == 0)
            return (null, "");

        if (_kind == TrackKind.Video && option == 1)
            return (-1, "");

        var track = _tracks[option - Offset];
        return (track.Index, track.Signature);
    }

    private void Write(string property, Func<int?> read, int? value) =>
        _journal!.Do(new SetValueCommand<int?>(
            _cue!.Id, property, "cues", read,
            stored =>
            {
                if (_kind == TrackKind.Audio)
                    _cue.AudioTrackIndex = stored;
                else
                    _cue.VideoTrackIndex = stored;
            },
            value,
            value is null ? "elect the track automatically" : $"choose {_kind} track"));

    private void WriteSignature(string signature) =>
        _journal!.Do(new SetValueCommand<string>(
            _cue!.Id, _kind == TrackKind.Audio ? "audioSignature" : "videoSignature", "cues",
            () => StoredSignature,
            stored =>
            {
                if (_kind == TrackKind.Audio)
                    _cue.AudioTrackSignature = stored;
                else
                    _cue.VideoTrackSignature = stored;
            },
            signature,
            "remember which track"));
}
