using S.Media.Compositor;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Compositor.Tests;

public sealed class TimestampSamplingTests
{
    private static readonly VideoFormat Canvas = new(2, 1, PixelFormat.Bgra32, new Rational(60, 1));

    [Fact]
    public void Master_aligned_slot_samples_irregular_out_of_order_pts_without_collapsing_pending_frames()
    {
        using var compositor = new CpuVideoCompositor(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, compositor);
        var slot = mixer.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        slot.Output.Configure(Canvas);

        slot.Output.Submit(Frame(0, 10));
        slot.Output.Submit(Frame(100, 40));
        slot.Output.Submit(Frame(40, 20));
        slot.Output.Submit(Frame(70, 30));

        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.FromMilliseconds(80),
            outputPresentationTime: TimeSpan.FromSeconds(5),
            defaultSurfaceRenderTime: null,
            out var first));
        Assert.Equal(TimeSpan.FromSeconds(5), first.PresentationTime);
        Assert.Equal(30, first.Planes[0].Span[0]);
        Assert.Equal(2, slot.SamplingSkippedFrames);
        Assert.Equal(0, slot.OverflowFrames);
        first.Dispose();

        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.FromMilliseconds(110),
            outputPresentationTime: TimeSpan.FromSeconds(6),
            defaultSurfaceRenderTime: null,
            out var second));
        Assert.Equal(TimeSpan.FromSeconds(6), second.PresentationTime);
        Assert.Equal(40, second.Planes[0].Span[0]);
        second.Dispose();
    }

    [Fact]
    public void Per_slot_mapper_uses_exact_output_target_and_keeps_output_pts_unchanged()
    {
        using var compositor = new CpuVideoCompositor(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, compositor);
        var slot = mixer.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        slot.AlignmentTimeMapper = outputTime => outputTime + TimeSpan.FromSeconds(2);
        slot.AlignmentTimeOffset = TimeSpan.FromMilliseconds(100);
        slot.Output.Configure(Canvas);
        slot.Output.Submit(Frame(2_150, 15));
        slot.Output.Submit(Frame(2_300, 30));

        var outputPts = TimeSpan.FromMilliseconds(100);
        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.Zero,
            outputPresentationTime: outputPts,
            defaultSurfaceRenderTime: null,
            out var composite));

        Assert.Equal(outputPts, composite.PresentationTime);
        Assert.Equal(15, composite.Planes[0].Span[0]);
        composite.Dispose();
    }

    [Fact]
    public void Bounded_vfr_lookahead_keeps_the_freshest_window_under_a_due_frame_burst()
    {
        using var compositor = new CpuVideoCompositor(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, compositor);
        var slot = mixer.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        slot.Output.Configure(Canvas);

        for (var i = 0; i < 40; i++)
            slot.Output.Submit(Frame(i, (byte)i));

        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.FromMilliseconds(39),
            outputPresentationTime: TimeSpan.FromSeconds(1),
            defaultSurfaceRenderTime: null,
            out var composite));

        Assert.Equal(39, composite.Planes[0].Span[0]);
        Assert.Equal(8, slot.OverflowFrames);
        composite.Dispose();
    }

    [Fact]
    public void Re_sampling_the_same_frame_counts_as_repeated_and_advancing_does_not()
    {
        using var compositor = new CpuVideoCompositor(Canvas);
        using var mixer = new VideoCompositorSource(Canvas, compositor);
        var slot = mixer.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        slot.Output.Configure(Canvas);

        slot.Output.Submit(Frame(0, 10));
        slot.Output.Submit(Frame(50, 20));

        // First sample selects frame 0: an advance, not a repeat.
        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.FromMilliseconds(10),
            outputPresentationTime: TimeSpan.FromSeconds(1),
            defaultSurfaceRenderTime: null,
            out var first));
        first.Dispose();
        Assert.Equal(0, slot.SamplingRepeatedFrames);

        // Second sample at 30 ms: frame 50 is not due yet, so frame 0 is re-shown - one repeat.
        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.FromMilliseconds(30),
            outputPresentationTime: TimeSpan.FromSeconds(2),
            defaultSurfaceRenderTime: null,
            out var second));
        second.Dispose();
        Assert.Equal(1, slot.SamplingRepeatedFrames);

        // Third sample at 60 ms advances to frame 50: repeats stay at one.
        Assert.True(mixer.TryReadNextFrame(
            canvasAlignmentTime: TimeSpan.FromMilliseconds(60),
            outputPresentationTime: TimeSpan.FromSeconds(3),
            defaultSurfaceRenderTime: null,
            out var third));
        third.Dispose();
        Assert.Equal(1, slot.SamplingRepeatedFrames);
        Assert.Equal(0, slot.SamplingSkippedFrames);
    }

    private static VideoFrame Frame(int ptsMs, byte blue) => new(
        TimeSpan.FromMilliseconds(ptsMs),
        Canvas,
        [new byte[] { blue, 0, 0, 255, blue, 0, 0, 255 }],
        [8]);
}
