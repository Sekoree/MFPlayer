using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Compositor.Tests;

/// <summary>
/// Default slot ids survive slot churn.
/// </summary>
/// <remarks>
/// Regression (HaCue2 batch fire): the default id was <c>slot_{count + 1}</c>, so removing a slot and
/// adding another reissued an id a surviving slot still held. On a live composition — layers come and
/// go with every cue — the collision threw out of <c>AddSlot</c>, aborted the whole batch fire
/// ("slot id 'slot_3' is already registered"), and the one-at-a-time fallback then skipped cues.
/// </remarks>
public sealed class CompositorSlotIdTests
{
    private static readonly Rational Fps = new(30, 1);

    [Fact]
    public void Removing_a_slot_never_makes_the_next_default_id_collide()
    {
        var canvas = new VideoFormat(320, 240, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = new VideoCompositorSource(canvas, compositor);

        var first = source.AddSlot();
        var second = source.AddSlot();
        var third = source.AddSlot();

        // The churn every live composition sees: an earlier layer ends while later ones play on.
        Assert.True(source.RemoveSlot(second.Id));

        // With a count-based default this was another "slot_3" and threw.
        var fourth = source.AddSlot();
        var fifth = source.AddSlot();

        var ids = new[] { first.Id, third.Id, fourth.Id, fifth.Id };
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Heavy_add_remove_churn_keeps_every_live_id_unique()
    {
        var canvas = new VideoFormat(320, 240, PixelFormat.Bgra32, Fps);
        using var compositor = new CpuVideoCompositor(canvas);
        using var source = new VideoCompositorSource(canvas, compositor);

        var live = new List<string>();
        for (var round = 0; round < 20; round++)
        {
            live.Add(source.AddSlot().Id);
            live.Add(source.AddSlot().Id);
            Assert.True(source.RemoveSlot(live[0]));
            live.RemoveAt(0);
            Assert.Equal(live.Count, live.Distinct().Count());
        }
    }
}
