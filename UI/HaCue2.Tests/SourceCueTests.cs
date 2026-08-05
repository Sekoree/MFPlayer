using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaCue2.Core.Validation;
using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Machine;
using HaCue2.Presentation;
using HaCue2.ViewModels;
using HaCue2.Views;
using S.Media.Core.Audio;
using S.Media.Source.YouTube;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// Adding and editing cues that play a SOURCE — a camera, an input, a video.
/// </summary>
/// <remarks>
/// The dialogs are the interesting half: the URI they build is the entire contract with the framework,
/// and a query option written wrongly here is a cue that opens the wrong thing at the venue with
/// nothing on screen to say so.
/// </remarks>
public class SourceCueTests
{
    private sealed class PreparedEnvironment(Func<string, PreparedSourceAvailability> availability)
        : IProjectEnvironment
    {
        public bool MediaExists(string resolvedPath) => true;
        public DeviceAvailability AudioLine(AudioLineDefinition line) => DeviceAvailability.Present;
        public DeviceAvailability VideoOutput(VideoOutputDefinition output) => DeviceAvailability.Present;
        public PreparedSourceAvailability PreparedSource(string sourceUri) => availability(sourceUri);
    }

    private sealed class YouTubeGateway : IYouTubeGateway
    {
        public YouTubeMediaManifest Manifest { get; } = new(
            "dQw4w9WgXcQ", "Background video", "Test", TimeSpan.FromSeconds(30),
            [new YouTubeVideoStreamInfo("1080p|avc1|mp4", "1080p", 1080, 30, "avc1", "mp4", 100, 1000)],
            [new YouTubeAudioStreamInfo("opus|webm|en", "opus", "webm", 10, 128, "en", true)],
            []);

        public Task<YouTubeMediaManifest> GetManifestAsync(string videoId, CancellationToken cancellationToken) =>
            Task.FromResult(Manifest);

        public async Task DownloadStreamAsync(
            string videoId, string descriptor, string filePath,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await Task.Delay(250, cancellationToken);
            await File.WriteAllTextAsync(filePath, descriptor, cancellationToken);
        }

        public Task<bool> TryDownloadCaptionsAssAsync(
            string videoId, string languageCode, string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> TryDownloadThumbnailJpegAsync(
            string videoId, string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    /// <summary>A machine with two capture devices across two driver families.</summary>
    private sealed class Backend : IAudioBackend
    {
        public string Name => "fake";

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
            [new("0", "HDMI 0", 8, 48_000, true, "ALSA")];

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() =>
        [
            new("3", "Built-in Microphone", 2, 44_100, true, "ALSA"),
            new("4", "Scarlett 2i2 USB", 4, 48_000, false, "JACK"),
        ];

        public IAudioOutput CreateOutput(string? id, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();

        public IAudioSource CreateInput(string? id, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    private static NdiSources.Scan Found(params string[] names) => new(names);

    /// <summary>The cue that was not there before. New cues land next to the SELECTION, not at the end.</summary>
    private static MediaCueNode Added(ShellViewModel shell, IReadOnlySet<Guid> before) =>
        shell.Project.AllCues().OfType<MediaCueNode>().First(cue => !before.Contains(cue.Id));

    private static HashSet<Guid> Ids(ShellViewModel shell) =>
        [.. shell.Project.AllCues().Select(cue => cue.Id)];

    private static int MediaCount(ShellViewModel shell) =>
        shell.Project.AllCues().OfType<MediaCueNode>().Count();

    [Fact]
    public Task AnNdiCueCarriesItsSenderNameAndItsOptions() => ShellFixture.WithShell(shell =>
    {
        var before = Ids(shell);

        var prompt = Dialogs.NdiSourceCue(shell.Cues, Found("STUDIO-PC (CAM 1)", "STUDIO-PC (CAM 2)"));
        prompt["Name"].Value = "Camera left";
        prompt["Video"].IsOn = true;
        prompt["Audio"].IsOn = false;
        prompt["Low bandwidth"].IsOn = true;
        prompt.Commit();

        var cue = Added(shell, before);
        var options = SourceUri.ParseNdi(cue.MediaPath);

        Assert.Equal("Camera left", cue.Label);
        Assert.Equal("STUDIO-PC (CAM 1)", options.Name);
        Assert.False(options.Audio);
        Assert.True(options.Video);
        Assert.True(options.LowBandwidth);
    });

    /// <summary>
    /// A sender that is not on the network yet can still be named.
    /// </summary>
    /// <remarks>
    /// The show authored in an office for a rig that arrives on the day. A dialog that only offered
    /// what it could see would make this impossible, which is most of how these shows get built.
    /// </remarks>
    [Fact]
    public Task ASenderThatIsNotOnTheNetworkCanStillBeTyped() => ShellFixture.WithShell(shell =>
    {
        var before = Ids(shell);

        var prompt = Dialogs.NdiSourceCue(shell.Cues, NdiSources.Scan.Nothing);
        prompt["Name"].Value = "Camera";
        prompt["Sender"].Value = "TOUR-RACK (PGM)";
        prompt.Commit();

        Assert.Equal("TOUR-RACK (PGM)", SourceUri.ParseNdi(Added(shell, before).MediaPath).Name);
    });

    /// <summary>Picking from the list FILLS the field, so the name stays editable afterwards.</summary>
    [Fact]
    public Task PickingADiscoveredSenderFillsTheTypedName() => ShellFixture.WithShell(shell =>
    {
        var prompt = Dialogs.NdiSourceCue(shell.Cues, Found("CAM 1", "CAM 2"));

        prompt["Found"].SelectedIndex = 1;

        Assert.Equal("CAM 2", prompt["Sender"].Value);
    });

    /// <summary>
    /// A live cue is kept out of pre-roll.
    /// </summary>
    /// <remarks>
    /// Pre-roll opens the next few cues' media early so the next GO is instant. For a camera that means
    /// claiming the network connection minutes before anybody asked; for a capture device it means
    /// claiming the device. A prepared YouTube video is an ordinary local file and stays in pre-roll.
    /// </remarks>
    [Fact]
    public Task ALiveCueIsOutOfPreRollAndAPreparedOneIsNot() => ShellFixture.WithShell(shell =>
    {
        var camera = shell.Cues.AddSourceCue("ndi://CAM 1", "Camera");
        var input = shell.Cues.AddSourceCue("padev://Mic", "Mic");
        var video = shell.Cues.AddSourceCue("youtube://dQw4w9WgXcQ", "Video", 213_000);

        Assert.True(camera!.DisablePreRoll);
        Assert.True(input!.DisablePreRoll);
        Assert.False(video!.DisablePreRoll);
    });

    [Fact]
    public Task ACaptureCueCarriesTheDeviceItsDriverAndItsFormat() => ShellFixture.WithShell(shell =>
    {
        var before = Ids(shell);

        var prompt = Dialogs.CaptureSourceCue(shell.Cues, new AudioDevices(new Backend()));
        prompt["Name"].Value = "Room mic";
        prompt["Driver"].SelectedIndex = prompt["Driver"].Options.ToList().IndexOf("JACK");
        prompt.Commit();

        var options = SourceUri.ParseCapture(Added(shell, before).MediaPath);

        Assert.Equal("Scarlett 2i2 USB", options.Device);
        Assert.Equal("JACK", options.HostApi);
        // The index is the LAST resort behind the name and the driver, but it still travels: it is the
        // only thing that survives a device being renamed by a driver update.
        Assert.Equal(4, options.DeviceIndex);
        // Width and rate follow the device rather than the dialog's defaults.
        Assert.Equal(4, options.Channels);
        Assert.Equal(48_000, options.SampleRate);
    });

    /// <summary>With nothing to enumerate the dialog still opens on a free-text name.</summary>
    [Fact]
    public Task ACaptureCueCanBeAuthoredOnAMachineWithNoInputs() => ShellFixture.WithShell(shell =>
    {
        var before = Ids(shell);

        var prompt = Dialogs.CaptureSourceCue(shell.Cues, devices: null);
        prompt["Name"].Value = "Lectern";
        prompt["Device"].Value = "Podium mic";
        prompt.Commit();

        Assert.Equal("Podium mic", SourceUri.ParseCapture(Added(shell, before).MediaPath).Device);
    });

    /// <summary>
    /// Editing repoints the cue rather than replacing it.
    /// </summary>
    /// <remarks>
    /// Everything else about the cue — its number, its placements, its sends, its position in the list —
    /// is the work. Re-adding it to change which camera it watches would throw all of that away.
    /// </remarks>
    [Fact]
    public Task EditingASourceKeepsTheCueItIsOn() => ShellFixture.WithShell(shell =>
    {
        var cue = shell.Cues.AddSourceCue("ndi://CAM 1", "Camera")!;
        var composition = shell.Project.Compositions.First();
        cue.Placements.Add(new LayerPlacement { CompositionId = composition.Id });

        var count = MediaCount(shell);

        var prompt = Dialogs.NdiSourceCue(shell.Cues, NdiSources.Scan.Nothing, cue);
        prompt["Sender"].Value = "CAM 2";
        prompt.Commit();

        Assert.Equal(count, MediaCount(shell));
        Assert.Equal("CAM 2", SourceUri.ParseNdi(cue.MediaPath).Name);
        Assert.Single(cue.Placements);
    });

    /// <summary>The edit is one undo step, not a repoint the operator has to undo three times.</summary>
    [Fact]
    public Task EditingASourceIsOneUndoStep() => ShellFixture.WithShell(shell =>
    {
        var cue = shell.Cues.AddSourceCue("ndi://CAM 1", "Camera")!;

        var prompt = Dialogs.NdiSourceCue(shell.Cues, NdiSources.Scan.Nothing, cue);
        prompt["Name"].Value = "Camera right";
        prompt["Sender"].Value = "CAM 2";
        prompt.Commit();

        shell.Journal.Undo();

        Assert.Equal("CAM 1", SourceUri.ParseNdi(cue.MediaPath).Name);
        Assert.Equal("Camera", cue.Label);
    });

    [Fact]
    public Task EditingAPreparedSourcesSubtitlesIsPartOfTheSameUndoStep() =>
        ShellFixture.WithShell(shell =>
        {
            var cue = shell.Cues.AddSourceCue(
                "youtube://dQw4w9WgXcQ",
                "Video",
                10_000,
                [new SubtitleSelection { Path = "/cache/old.vtt" }])!;

            shell.Cues.SetSource(cue.Id, "youtube://dQw4w9WgXcQ", "Video edited", 12_000, []);
            Assert.Empty(cue.Subtitles);

            shell.Undo();

            Assert.Equal("Video", cue.Label);
            Assert.Equal(10_000, cue.SourceDurationMs);
            Assert.Equal("/cache/old.vtt", Assert.Single(cue.Subtitles).Path);
        });

    /// <summary>The row names the camera, not the URI — which is unreadable at that width.</summary>
    [Fact]
    public Task TheRowNamesTheSourceAndMarksItLive() => ShellFixture.WithShell(shell =>
    {
        var cue = shell.Cues.AddSourceCue(
            SourceUri.Ndi(new NdiSourceOptions("STUDIO (CAM 1)")), "Camera")!;

        shell.Cues.Refresh();

        var row = shell.Cues.AllRows.First(item => item.Id == cue.Id);

        Assert.Equal("NDI · STUDIO (CAM 1)", row.Source);
        Assert.Contains(row.Badges, badge => badge.Text == "live");
    });

    /// <summary>A prepared video is not live, so it carries no live badge and keeps its length.</summary>
    [Fact]
    public Task APreparedVideoShowsItsLengthAndIsNotLive() => ShellFixture.WithShell(shell =>
    {
        var cue = shell.Cues.AddSourceCue("youtube://dQw4w9WgXcQ", "Never gonna", 213_000)!;

        shell.Cues.Refresh();

        var row = shell.Cues.AllRows.First(item => item.Id == cue.Id);

        Assert.DoesNotContain(row.Badges, badge => badge.Text == "live");
        Assert.Equal("3:33", row.Length);
    });

    [Fact]
    public Task AddingAYouTubeCueClosesOverASavedCueWhileItsDownloadContinues() =>
        ShellFixture.WithShellAsync(async shell =>
        {
            var cache = Directory.CreateTempSubdirectory("hacue-youtube-ui-").FullName;
            var gateway = new YouTubeGateway();
            var preparer = new YouTubePreparer(gateway, cache, (video, audio, thumbnail, output, _) =>
            {
                File.WriteAllText(output, string.Join('+', new[] { video, audio }
                    .Where(path => path is not null)
                    .Select(path => File.ReadAllText(path!))));
            });
            using var downloads = new YouTubePreparationQueue(preparer);
            try
            {
                var before = Ids(shell);
                var editor = new YouTubeCueViewModel(shell.Cues, gateway, preparer, downloads)
                {
                    Url = "https://youtu.be/dQw4w9WgXcQ",
                };

                await editor.ResolveCommand.ExecuteAsync(null);
                editor.PrepareCommand.Execute(null);

                var cue = Added(shell, before);
                var completion = downloads.Enqueue(cue.MediaPath); // joins the dialog's job
                Assert.Equal("Background video", cue.Label);
                Assert.Equal(30_000, cue.SourceDurationMs);
                Assert.False(completion.IsCompleted);
                Assert.True(downloads.StateOf(cue.MediaPath) is
                    YouTubeCacheState.Queued or YouTubeCacheState.Downloading);

                var result = await completion;
                Assert.True(result.IsSuccess, result.Error);
                Assert.Equal(YouTubeCacheState.Ready, downloads.StateOf(cue.MediaPath));
            }
            finally
            {
                try { Directory.Delete(cache, recursive: true); }
                catch { /* best effort */ }
            }
        });

    [Fact]
    public Task ProjectStatusFixRedownloadsAMovedYouTubeCueAndPinsItsResolvedStreams() =>
        ShellFixture.WithShellAsync(async shell =>
        {
            var cache = Directory.CreateTempSubdirectory("hacue-youtube-repair-").FullName;
            var gateway = new YouTubeGateway();
            var preparer = new YouTubePreparer(gateway, cache, (video, audio, thumbnail, output, _) =>
            {
                File.WriteAllText(output, string.Join('+', new[] { video, audio }
                    .Where(path => path is not null)
                    .Select(path => File.ReadAllText(path!))));
            });
            using var downloads = new YouTubePreparationQueue(preparer);
            const string movedSource = "youtube://dQw4w9WgXcQ"; // older projects stored the unresolved policy
            var cue = shell.Cues.AddSourceCue(movedSource, "Moved video", 30_000)!;
            var environment = new PreparedEnvironment(source => downloads.StateOf(source) switch
            {
                YouTubeCacheState.Ready => PreparedSourceAvailability.Ready,
                YouTubeCacheState.Queued or YouTubeCacheState.Downloading => PreparedSourceAvailability.Preparing,
                YouTubeCacheState.Failed => PreparedSourceAvailability.Failed,
                _ => PreparedSourceAvailability.Missing,
            });
            using var status = new ProjectStatusViewModel(
                shell.Project, environment, shell.Journal, youTubeDownloads: downloads);

            Assert.Equal(CheckOutcome.Failed,
                status.Report.Checks.Single(check => check.Name == "YouTube cache").Outcome);

            try
            {
                status.QueueMissingYouTube();
                var result = await downloads.Enqueue(movedSource); // joins the Fix action's job
                Assert.True(result.IsSuccess, result.Error);

                // The repair continuation journals the manifest-resolved selection on the UI thread.
                for (var attempt = 0; attempt < 20 && cue.MediaPath == movedSource; attempt++)
                {
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10);
                }

                Assert.NotEqual(movedSource, cue.MediaPath);
                Assert.Contains("v=1080p", cue.MediaPath);
                Assert.Contains("a=opus", cue.MediaPath);
                status.Rerun();
                Assert.Equal(CheckOutcome.Passed,
                    status.Report.Checks.Single(check => check.Name == "YouTube cache").Outcome);

                shell.Journal.Undo();
                Assert.Equal(movedSource, cue.MediaPath);
            }
            finally
            {
                try { Directory.Delete(cache, recursive: true); }
                catch { /* best effort */ }
            }
        });

    /// <summary>The clip editor is a waveform over a file; a source has neither.</summary>
    [Fact]
    public Task ASourceCueHasNoClipEditor() => ShellFixture.WithShell(shell =>
    {
        var cue = shell.Cues.AddSourceCue("ndi://CAM 1", "Camera")!;
        ShellFixture.Select(shell.Cues, cue.Id);

        Assert.False(shell.Cues.Inspector.CanEditClip);
        Assert.Null(shell.Cues.Inspector.ClipEditor());
    });

    /// <summary>"Edit source…" is offered on a source cue and on nothing else.</summary>
    [Fact]
    public Task EditSourceIsOfferedOnlyOnASourceCue() => ShellFixture.WithShell(shell =>
    {
        // Captured BEFORE the camera exists: a new cue lands next to the selection, so afterwards
        // "the first media cue" could well be the camera itself.
        var bed = ShellFixture.Bed(shell.Project);

        var camera = shell.Cues.AddSourceCue("ndi://CAM 1", "Camera")!;
        ShellFixture.Select(shell.Cues, camera.Id);

        Assert.True(shell.Cues.CanEditSource);
        Assert.Equal(SourceKind.Ndi, shell.Cues.SelectedSourceKind);

        ShellFixture.Select(shell.Cues, bed.Id);

        Assert.False(shell.Cues.CanEditSource);
    });

    /// <summary>
    /// The three source kinds are reachable from the ADD menu.
    /// </summary>
    /// <remarks>
    /// A view-model that can build a cue nobody can ask for is the failure mode this catches: every
    /// one of these providers was already in the tree and reachable by nothing at all, which is how
    /// this whole feature came to be missing in the first place.
    /// </remarks>
    [Fact]
    public Task TheAddMenuOffersEverySourceKind() => ShellFixture.WithShell(shell =>
    {
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var add = window.GetVisualDescendants().OfType<Button>()
            .First(button => (button.Content as string) == "+ ADD ▾");

        var headers = Assert.IsType<MenuFlyout>(add.Flyout).Items
            .OfType<MenuItem>()
            .Select(item => item.Header as string)
            .ToList();

        Assert.Contains("NDI input…", headers);
        Assert.Contains("Local input…", headers);
        Assert.Contains("YouTube…", headers);

        window.Close();
    });
}
