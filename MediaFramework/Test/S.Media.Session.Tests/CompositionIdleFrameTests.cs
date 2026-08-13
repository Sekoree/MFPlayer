using S.Media.Core.Video;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Idle frames: what a canvas shows between cues. Before this the concept existed only on local video
/// lines, and only while the line was NOT held by playback - so once a cue list took the line the image
/// stopped appearing and the canvas went black between cues, which is exactly when a logo or holding
/// slate is wanted.
/// </summary>
public sealed class CompositionIdleFrameTests
{
    private static ShowDocument OneComposition() => new(
        Version: 1,
        Cues: [],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", 64, 48, 25, 1)],
        Routes: []);

    /// <summary>Disposal is only observable through the release handle a frame was built with -
    /// <see cref="VideoFrame"/> has no IsDisposed.</summary>
    private sealed class ReleaseProbe : IDisposable
    {
        public bool Released { get; private set; }
        public void Dispose() => Released = true;

        public VideoFrame Frame() =>
            new(TimeSpan.Zero,
                new VideoFormat(64, 48, PixelFormat.Bgra32, new Rational(25, 1)),
                new byte[64 * 48 * 4], 64 * 4,
                new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied),
                release: this);
    }

    private static VideoFrame Frame() => new ReleaseProbe().Frame();

    private static ShowSession Session()
    {
        var session = new ShowSession(MediaRegistry.Build(_ => { }));
        session.LoadDocument(OneComposition());
        return session;
    }

    [Fact]
    public async Task SetsAndClearsACompositionIdleFrame()
    {
        await using var session = Session();

        Assert.True(await session.SetCompositionIdleFrameAsync("screen", Frame()));
        Assert.True(await session.SetCompositionIdleFrameAsync("screen", null));
    }

    [Fact]
    public async Task AnUnknownComposition_DisposesTheFrame_RatherThanLeakingIt()
    {
        await using var session = Session();
        var probe = new ReleaseProbe();

        Assert.False(await session.SetCompositionIdleFrameAsync("nope", probe.Frame()));

        Assert.True(probe.Released, "a rejected idle frame was leaked");
    }

    [Fact]
    public async Task ReplacingTheIdleFrame_DisposesThePreviousOne()
    {
        await using var session = Session();
        var probe = new ReleaseProbe();
        await session.SetCompositionIdleFrameAsync("screen", probe.Frame());

        await session.SetCompositionIdleFrameAsync("screen", Frame());

        Assert.True(probe.Released, "the replaced idle frame was leaked");
    }

    [Fact]
    public async Task DisposingTheSession_ReleasesTheIdleFrame()
    {
        var probe = new ReleaseProbe();
        var session = Session();
        await session.SetCompositionIdleFrameAsync("screen", probe.Frame());

        await session.DisposeAsync();

        // A held frame that outlives its runtime is a permanent leak of a whole canvas buffer.
        Assert.True(probe.Released, "the idle frame outlived the composition");
    }

    [Fact]
    public async Task PerOutputIdle_IsSetAndCleared_AndRejectsUnknownOutputs()
    {
        await using var session = Session();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "lobby-tv");

        Assert.True(await session.SetOutputIdleFrameAsync("screen", "lobby-tv", Frame()));
        Assert.True(await session.SetOutputIdleFrameAsync("screen", "lobby-tv", null));

        var probe = new ReleaseProbe();
        Assert.False(await session.SetOutputIdleFrameAsync("screen", "no-such-output", probe.Frame()));
        Assert.True(probe.Released, "a rejected per-output idle frame was leaked");
    }

    [Fact]
    public async Task RetiringAnOutput_ReleasesItsIdleFrame()
    {
        var probe = new ReleaseProbe();
        var session = Session();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "lobby-tv");
        await session.SetOutputIdleFrameAsync("screen", "lobby-tv", probe.Frame());

        await session.DisposeAsync();

        Assert.True(probe.Released, "a per-output idle frame outlived its output");
    }

    [Fact]
    public async Task AnIdleCompositionKeepsPumping_WithoutThrowing()
    {
        await using var session = Session();
        await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput(), "projector");
        await session.SetCompositionIdleFrameAsync("screen", Frame());

        // The idle path runs every tick while the canvas has no layers, and reuses one stored frame -
        // so an implementation that handed it straight to the sink would dispose it on the first tick.
        await Task.Delay(200);

        var stats = await session.GetCompositionStatsAsync("screen");
        Assert.NotNull(stats);
        Assert.True(await session.SetCompositionIdleFrameAsync("screen", null));
    }

    /// <summary>Counts what a sink was actually told to do - Configure is what opens a real window.</summary>
    private sealed class CountingVideoOutput : IVideoOutput
    {
        private VideoFormat _format;
        private int _submitted;

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public int Configures { get; private set; }
        public int Submitted => Volatile.Read(ref _submitted);

        public void Configure(VideoFormat format)
        {
            _format = format;
            Configures++;
        }

        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _submitted);
            frame.Dispose();
        }
    }

    /// <summary>
    /// An output that declares <c>PresentWhenIdle</c> starts receiving frames before any cue fires.
    /// </summary>
    /// <remarks>
    /// This is what makes a cue player's projector exist. The pump used to start only when a LAYER was
    /// added, so a composition with nothing playing submitted nothing at all - and a sink that is
    /// configured by its first submitted frame (every windowed one) never opened its window. The
    /// operator saw no window, no error, and concluded the output did not work.
    /// </remarks>
    [Fact]
    public async Task PresentWhenIdle_ConfiguresTheSinkBeforeAnythingPlays()
    {
        await using var session = Session();
        var output = new CountingVideoOutput();

        Assert.True(await session.AddCompositionOutputAsync(
            "screen",
            new ClipCompositionOutputLease("projector", "Projector", output, PresentWhenIdle: true)));

        await session.SetOutputIdleFrameAsync("screen", "projector", Frame());

        // 25 fps canvas, so a handful of ticks is ample; polled rather than slept-through so the test
        // does not spend its whole budget waiting on a pump that already delivered.
        for (var attempt = 0; attempt < 40 && output.Submitted == 0; attempt++)
            await Task.Delay(25);

        Assert.True(output.Submitted > 0, "an idle output never received a frame, so its window never opened");
        Assert.Equal(1, output.Configures);
        Assert.Equal(64, output.Format.Width);
    }

    /// <summary>
    /// Without the flag the pump still waits for a layer - a media player must not light a screen.
    /// </summary>
    /// <remarks>
    /// The two answers are both right for somebody, which is why this is opt-in rather than the new
    /// default: a player attaches an output to show a clip, and a pump running over an empty canvas
    /// would put a black window on screen the moment a line was selected.
    /// </remarks>
    [Fact]
    public async Task WithoutTheFlag_AnIdleCompositionStaysQuiet()
    {
        await using var session = Session();
        var output = new CountingVideoOutput();

        Assert.True(await session.AddCompositionOutputAsync(
            "screen", new ClipCompositionOutputLease("preview", "Preview", output)));

        await session.SetOutputIdleFrameAsync("screen", "preview", Frame());
        await Task.Delay(200);

        Assert.Equal(0, output.Submitted);
        Assert.Equal(0, output.Configures);
    }
}
