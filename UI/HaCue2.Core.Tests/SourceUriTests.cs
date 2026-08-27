using HaCue2.Core.Compile;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Validation;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// Cues whose media is a SOURCE rather than a file.
/// </summary>
/// <remarks>
/// The whole of this feature is one idea - a media path that is a URI - and the risk is everything
/// downstream that assumed a media path is a filename. These tests are mostly about the second half:
/// what must NOT happen to a URI on its way to the engine.
/// </remarks>
public class SourceUriTests
{
    private static HaCueProject WithSource(string uri, Action<HaCueProject>? configure = null)
    {
        var project = new HaCueProject
        {
            CueLists =
            [
                new CueList
                {
                    Name = "Main",
                    Cues = [new MediaCueNode { Number = "1", Label = "Camera", MediaPath = uri }],
                },
            ],
        };

        configure?.Invoke(project);
        return project;
    }

    [Theory]
    [InlineData("ndi://STUDIO (CAM 1)", SourceKind.Ndi)]
    [InlineData("padev://Scarlett 2i2", SourceKind.Capture)]
    [InlineData("youtube://dQw4w9WgXcQ", SourceKind.YouTube)]
    [InlineData("text:eyJUZXh0IjoiaGkifQ==", SourceKind.Text)]
    [InlineData("audio/preshow.wav", SourceKind.File)]
    [InlineData("/mnt/shows/preshow.wav", SourceKind.File)]
    [InlineData("", SourceKind.File)]
    public void ASchemeIsRecognisedExactlyOrNotAtAll(string path, SourceKind expected) =>
        Assert.Equal(expected, SourceUri.KindOf(path));

    /// <summary>
    /// A Windows drive letter is not a scheme.
    /// </summary>
    /// <remarks>
    /// The one way a naive "text before the colon" rule breaks catastrophically: every absolute path
    /// on Windows would become a source URI, and a show authored there would reference nothing.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Shows\preshow.wav")]
    [InlineData(@"D:\ndi\camera.mov")]
    public void ADriveLetterIsAPathAndNotAScheme(string path) =>
        Assert.Equal(SourceKind.File, SourceUri.KindOf(path));

    /// <summary>Live means endless and device-claiming. A prepared YouTube asset is neither.</summary>
    [Fact]
    public void OnlyNdiAndCaptureAreLive()
    {
        Assert.True(SourceUri.IsLive("ndi://CAM 1"));
        Assert.True(SourceUri.IsLive("padev://Mic"));
        Assert.False(SourceUri.IsLive("youtube://dQw4w9WgXcQ"));
        Assert.False(SourceUri.IsLive("audio/bed.wav"));
    }

    [Fact]
    public void AnNdiUriRoundTripsThroughItsOptions()
    {
        var options = new NdiSourceOptions("STUDIO-PC (CAM 1)")
        {
            Audio = true,
            Video = false,
            LowBandwidth = true,
            AudioBufferMs = 120,
            PaceFromIngestClock = true,
            LimitedRange = true,
        };

        var uri = SourceUri.Ndi(options);
        var restored = SourceUri.ParseNdi(uri);

        Assert.Equal(options, restored);
        // Spaces and parentheses are what NDI names are made of; the URI has to survive them.
        Assert.Contains("STUDIO-PC", uri, StringComparison.Ordinal);
        // The framework grammar's escape hatch for studio-range hardware senders, verbatim.
        Assert.Contains("range=limited", uri, StringComparison.Ordinal);
    }

    /// <summary>The common case reads like something a person wrote, because the defaults are absent.</summary>
    [Fact]
    public void AnUnadornedNdiSourceIsJustItsName()
    {
        Assert.Equal("ndi://CAM%201", SourceUri.Ndi(new NdiSourceOptions("CAM 1")));
        Assert.Equal("CAM 1", SourceUri.ParseNdi("ndi://CAM%201").Name);
    }

    [Fact]
    public void ACaptureUriRoundTripsThroughItsOptions()
    {
        var options = new CaptureSourceOptions("Scarlett 2i2 USB")
        {
            HostApi = "ALSA",
            DeviceIndex = 7,
            Channels = 2,
            SampleRate = 48_000,
        };

        Assert.Equal(options, SourceUri.ParseCapture(SourceUri.Capture(options)));
    }

    /// <summary>Both bare and <c>//</c> forms parse: the providers accept either and shows carry both.</summary>
    [Fact]
    public void EitherAuthorityFormParses()
    {
        Assert.Equal("Mic", SourceUri.ParseCapture("padev:Mic").Device);
        Assert.Equal("Mic", SourceUri.ParseCapture("padev://Mic").Device);
    }

    [Fact]
    public void ASourceIsDescribedByItsNameRatherThanItsUri()
    {
        Assert.Equal("NDI · CAM 1 · video only",
            SourceUri.Describe(SourceUri.Ndi(new NdiSourceOptions("CAM 1") { Audio = false })));

        Assert.Equal("input · Mic · 2ch",
            SourceUri.Describe(SourceUri.Capture(new CaptureSourceOptions("Mic") { Channels = 2 })));

        Assert.Equal("YouTube · dQw4w9WgXcQ", SourceUri.Describe("youtube://dQw4w9WgXcQ?v=best"));

        // An empty capture name is the system default input, not a nameless device.
        Assert.Equal("input · default device", SourceUri.Describe("padev://"));
    }

    [Fact]
    public void AYouTubeCaptionLanguageCanBeIdentifiedWithoutTreatingSidecarsAsProviderData()
    {
        Assert.Equal("de-DE", SourceUri.YouTubeSubtitleLanguage(
            "youtube://dQw4w9WgXcQ?v=1080p&sub=de-DE"));
        Assert.Null(SourceUri.YouTubeSubtitleLanguage("youtube://dQw4w9WgXcQ?v=1080p"));
        Assert.Null(SourceUri.YouTubeSubtitleLanguage("video.mp4?sub=de-DE"));
    }

    // ── what must not happen to a URI ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A URI is never joined onto the media root.
    /// </summary>
    /// <remarks>
    /// The failure this prevents: <c>Path.Combine("/shows", "ndi://CAM 1")</c> normalises to
    /// <c>/shows/ndi:/CAM 1</c>, and the registry is then handed a directory that does not exist
    /// instead of a camera that does.
    /// </remarks>
    [Fact]
    public void AUriIsNotResolvedAgainstTheMediaRoot()
    {
        var project = WithSource("ndi://CAM 1", item => item.Settings.MediaRoot = "/shows/media");

        Assert.Equal("ndi://CAM 1", MediaPaths.Resolve(project, "ndi://CAM 1", "/shows/show.hacue2proj"));
    }

    /// <summary>A camera is not a file to relink, to consolidate, or to report missing.</summary>
    [Fact]
    public void AUriIsNotAMediaFileReference()
    {
        var project = WithSource("ndi://CAM 1");

        Assert.Empty(MediaPaths.ReferencesIn(project));
    }

    [Fact]
    public void FilesAndUrisCoexistAndOnlyTheFilesAreReferences()
    {
        var project = WithSource("ndi://CAM 1");
        project.CueLists[0].Cues.Add(new MediaCueNode
        {
            Number = "2", Label = "Bed", MediaPath = "audio/bed.wav",
        });

        var reference = Assert.Single(MediaPaths.ReferencesIn(project));

        Assert.Equal("audio/bed.wav", reference.Path);
    }

    /// <summary>The status pass must not call a live cue's media missing on a machine off the network.</summary>
    [Fact]
    public void TheStatusPassDoesNotReportASourceAsAMissingFile()
    {
        var status = ProjectStatus.Run(WithSource("ndi://CAM 1"));

        var media = status.Checks.Single(check => check.Name == "Media files");

        Assert.Equal(CheckOutcome.Passed, media.Outcome);
    }

    /// <summary>The URI reaches the engine exactly as authored - that is the whole contract.</summary>
    [Fact]
    public void TheUriReachesTheEngineVerbatim()
    {
        var uri = SourceUri.Ndi(new NdiSourceOptions("CAM 1") { LowBandwidth = true });
        var project = WithSource(uri, item => item.Settings.MediaRoot = "/shows/media");

        var clip = Assert.Single(ShowCompiler
            .Compile(project, new ShowCompileContext { ProjectPath = "/shows/show.hacue2proj" })
            .Clips);

        Assert.Equal(uri, clip.MediaPath);
    }

    /// <summary>
    /// A duration the source told us survives the save.
    /// </summary>
    /// <remarks>
    /// Durations are otherwise machine facts kept out of the document on purpose. This one has nowhere
    /// else to live: a prepared YouTube video knows its length from the manifest, and no probe on this
    /// machine can rediscover it from a <c>youtube://</c> URI.
    /// </remarks>
    [Fact]
    public void ASourceDurationSurvivesASave()
    {
        var project = WithSource("youtube://dQw4w9WgXcQ");
        ((MediaCueNode)project.CueLists[0].Cues[0]).SourceDurationMs = 213_000;

        var restored = Serialization.HaCueProjectFile.Deserialize(
            Serialization.HaCueProjectFile.Serialize(project));

        Assert.Equal(213_000, ((MediaCueNode)restored.CueLists[0].Cues[0]).SourceDurationMs);
    }
}
