using HaCue2.Core.Compile;
using HaCue2.Core.Model;
using HaCue2.Machine;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Choosing which track of a media file a cue plays.
/// </summary>
/// <remarks>
/// A concert capture routinely carries several audio tracks — a stereo mix, an isolated vocal, a room
/// pair — and files carry several subtitle tracks as a matter of course. The rule underneath all of
/// this: a stored INDEX is positional, and re-muxing a file renumbers its streams, so an index alone
/// can silently start pointing at the German commentary. Every test here is about not doing that.
/// </remarks>
public sealed class MediaTrackTests
{
    private static readonly MediaTrack Mix = Track(1, "Audio:aac:eng:2:48000:0x0", "eng");
    private static readonly MediaTrack Vocal = Track(2, "Audio:aac:deu:1:48000:0x0", "deu");
    private static readonly MediaTrack Room = Track(3, "Audio:aac:fra:2:48000:0x0", "fra");

    private static readonly IReadOnlyList<MediaTrack> Tracks = [Mix, Vocal, Room];

    [Fact]
    public void NoChoiceResolvesToAutomaticElection()
    {
        Assert.Null(MediaFacts.Resolve(Tracks, null, ""));
        Assert.Null(MediaFacts.Resolve(Tracks, -1, ""));
    }

    [Fact]
    public void AnIndexWhoseContentStillMatchesIsTrusted()
    {
        var resolved = MediaFacts.Resolve(Tracks, Vocal.Index, Vocal.Signature);

        Assert.Equal(Vocal.Index, resolved!.Value.Index);
    }

    [Fact]
    public void AReMuxThatMovedTheTrackFindsItByContent()
    {
        // Same file, streams renumbered: the vocal is now #2 where the mix used to be.
        IReadOnlyList<MediaTrack> renumbered =
        [
            Track(1, Vocal.Signature, "deu"),
            Track(2, Mix.Signature, "eng"),
        ];

        var resolved = MediaFacts.Resolve(renumbered, index: 2, Vocal.Signature);

        // Followed the CONTENT, not the number — index 2 is the mix now.
        Assert.Equal(1, resolved!.Value.Index);
    }

    [Fact]
    public void ATrackThatIsGoneFallsBackToAutomaticRatherThanTheWrongOne()
    {
        var resolved = MediaFacts.Resolve(Tracks, index: 2, "Audio:flac:jpn:6:96000:0x0");

        // Null means "let the decoder elect one". Being obviously automatic beats being quietly wrong:
        // the alternative is an operator hearing a language nobody chose with nothing to explain it.
        Assert.Null(resolved);
    }

    [Fact]
    public void AnIndexWithNoRememberedContentIsStillHonoured()
    {
        // A project written before signatures existed, or a choice made against an unprobed file.
        var resolved = MediaFacts.Resolve(Tracks, Room.Index, signature: "");

        Assert.Equal(Room.Index, resolved!.Value.Index);
    }

    [Fact]
    public void AMissingTrackListNeverThrows()
    {
        // FirstOrDefault over a value type hands back a fully default instance whose strings are null.
        Assert.Null(MediaFacts.Resolve([], 1, "anything"));
        Assert.Null(MediaFacts.Resolve(Tracks, 99, ""));
    }

    [Fact]
    public void CoverArtIsPlaceableEvenThoughItIsNotVideo()
    {
        var art = CoverArt();
        var facts = new MediaFacts { AudioTracks = [Mix], VideoTracks = [art] };

        // Not "video" — a placement of it will not move...
        Assert.False(facts.HasVideo);
        // ...but it IS placeable, and that is the whole point: an audio cue can put the album art on
        // a canvas for the length of the track, the same as HaPlay's cue player.
        Assert.True(facts.HasPlaceableVideo);
        Assert.True(facts.IsCoverArtOnly);
        Assert.Equal(art.Index, facts.PlaceableVideoTrack!.Value.Index);
    }

    [Fact]
    public void MovingVideoWinsOverCoverArtForAPlacement()
    {
        var moving = new MediaTrack(0, "#0 h264", "Video:h264::0:0:1920x1080", "eng", 0, 1920, 1080,
            true, IsAttachedPicture: false, true);

        var facts = new MediaFacts { VideoTracks = [CoverArt(), moving] };

        // A file with both: the placement shows the film, not the thumbnail beside it.
        Assert.False(facts.IsCoverArtOnly);
        Assert.Equal(moving.Index, facts.PlaceableVideoTrack!.Value.Index);
    }

    [Fact]
    public void APlacementOfCoverArtMustNameTheStream()
    {
        var facts = new MediaFacts { AudioTracks = [Mix], VideoTracks = [CoverArt()] };
        var fixture = new TestProject();

        // The rule this exists for: the decoder's automatic election SKIPS attached pictures. A cue
        // placed with no explicit index would put an empty layer on the canvas and give the operator
        // no reason for it, so whatever creates a placement has to name the track.
        fixture.Track.Placement = new LayerPlacement { CompositionId = fixture.Cyc.Id };
        fixture.Track.VideoTrackIndex = facts.PlaceableVideoTrack!.Value.Index;

        var clip = ShowCompiler.Compile(fixture.Project).Clips
            .Single(candidate => candidate.ClipId == fixture.Track.Id.ToString());

        Assert.Equal(fixture.Cyc.Id.ToString(), clip.CompositionId);
        Assert.Equal(facts.PlaceableVideoTrack!.Value.Index, clip.VideoStreamIndex);
    }

    private static MediaTrack CoverArt() =>
        new(1, "#1 mjpeg 700×700 (cover art)", "Video:mjpeg::0:0:700x700",
            null, 0, 700, 700, false, IsAttachedPicture: true, true);

    [Fact]
    public void CoverArtIsAVideoStreamButNotVideo()
    {
        // Every container that carries album art carries it as a video stream. A FLAC with cover art
        // probes as "1 video track", and treating that as video would put a still image on the wall.
        var art = new MediaTrack(1, "#1 mjpeg 700×700 (cover art)", "Video:mjpeg::0:0:700x700",
            null, 0, 700, 700, false, IsAttachedPicture: true, true);

        var facts = new MediaFacts { AudioTracks = [Mix], VideoTracks = [art] };

        Assert.False(facts.HasVideo);
        Assert.Empty(facts.MovingVideoTracks);
        // Still LISTED, because automatic election skips it and choosing it explicitly is the only
        // way to show one.
        Assert.Single(facts.VideoTracks);
    }

    [Fact]
    public async Task AMacResourceForkIsNotMedia()
    {
        var directory = Directory.CreateTempSubdirectory("hacue2-probe");

        try
        {
            // Any drive that has been through a Mac is full of these: a few KB of metadata with a
            // media extension, sitting beside the real file.
            var stub = Path.Combine(directory.FullName, "._Concert.mp4");
            await File.WriteAllBytesAsync(stub, new byte[4096]);

            var facts = await MediaProbe.ProbeAsync(stub);

            Assert.False(facts.IsKnown);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ACuesTrackChoicesReachTheEngine()
    {
        var fixture = new TestProject();
        fixture.Track.AudioTrackIndex = 2;
        fixture.Track.VideoTrackIndex = -1;
        fixture.Track.Subtitles =
        [
            new SubtitleSelection { StreamIndex = 4 },
            new SubtitleSelection { Path = "subs/act1.srt" },
        ];

        var clip = ShowCompiler.Compile(fixture.Project).Clips
            .Single(candidate => candidate.ClipId == fixture.Track.Id.ToString());

        Assert.Equal(2, clip.AudioStreamIndex);
        // −1 is "no video", a real choice and not the same as electing one.
        Assert.Equal(-1, clip.VideoStreamIndex);

        var subtitles = clip.GetSubtitleSelections();
        Assert.Equal(2, subtitles.Count);
        Assert.Null(subtitles[0].Path);
        Assert.Equal(4, subtitles[0].StreamIndex);
        Assert.Equal("subs/act1.srt", subtitles[1].Path);
    }

    [Fact]
    public void AnUnmadeChoiceStaysUnmadeAllTheWayDown()
    {
        var clip = ShowCompiler.Compile(new TestProject().Project).Clips[0];

        // Null, not a frozen index: the decoder elects, which is what the document asked for.
        Assert.Null(clip.AudioStreamIndex);
        Assert.Null(clip.VideoStreamIndex);
        Assert.Empty(clip.GetSubtitleSelections());
    }

    [Fact]
    public void TrackChoicesSurviveASaveAndLoad()
    {
        var fixture = new TestProject();
        fixture.Track.AudioTrackIndex = 2;
        fixture.Track.AudioTrackSignature = Vocal.Signature;
        fixture.Track.Subtitles = [new SubtitleSelection { StreamIndex = 5, Signature = "sub:eng" }];

        var reloaded = Serialization.HaCueProjectFile.Deserialize(
            Serialization.HaCueProjectFile.Serialize(fixture.Project));

        var cue = reloaded.AllCues().OfType<MediaCueNode>()
            .Single(candidate => candidate.Id == fixture.Track.Id);

        Assert.Equal(2, cue.AudioTrackIndex);
        Assert.Equal(Vocal.Signature, cue.AudioTrackSignature);
        Assert.Equal(5, cue.Subtitles[0].StreamIndex);
    }

    private static MediaTrack Track(int index, string signature, string language) =>
        new(index, $"#{index} aac [{language}]", signature, language, 2, 0, 0, index == 1, false, true);
}
