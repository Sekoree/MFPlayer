using HaCue2.Core.Model;
using HaCue2.Engine;
using S.Media.Session;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// What firing a cue means, for every kind.
/// </summary>
/// <remarks>
/// The code with the most at stake in the app: it decides what happens when somebody presses GO. Until
/// the execution seam existed it could only be reached through a running session, so none of it was
/// tested — and two defects in it were found by reading rather than by a test.
/// </remarks>
public class CueExecutorTests
{
    private static (CueExecutor Executor, FakeCueHost Host, HaCueProject Project) Show(
        params CueNode[] cues)
    {
        var project = new HaCueProject
        {
            CueLists = [new CueList { Name = "Main", Cues = [.. cues] }],
        };

        var host = new FakeCueHost(project);
        return (new CueExecutor(host), host, project);
    }

    private static MediaCueNode Media(string number) =>
        new() { Number = new CueNumber(number), Label = number, MediaPath = $"{number}.wav" };

    // ── the basics ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMediaCueIsPlayed()
    {
        var cue = Media("1");
        var (executor, host, _) = Show(cue);

        Assert.True(await executor.FireAsync(cue.Id));
        Assert.Equal([cue.Id], host.Played);
    }

    [Fact]
    public async Task ATextCueIsPlayedAndFollowsOnItsNaturalEnd()
    {
        var card = new TextCueNode
        {
            Number = "1", Label = "Title", Text = "ACT ONE", DurationMs = 2_000,
            Trigger = CueTrigger.Follow,
        };
        var next = Media("2");
        var (executor, host, _) = Show(card, next);

        await executor.FireAsync(card.Id);
        Assert.Equal([card.Id], host.Played);

        await executor.OnNaturalEndAsync(card.Id);
        Assert.Equal([card.Id, next.Id], host.Played);
    }

    [Fact]
    public async Task ADisabledCueIsNotFiredFromAnywhere()
    {
        var cue = Media("1");
        cue.Enabled = false;
        var (executor, host, _) = Show(cue);

        // Stepped over wherever it is reached from, not only by GO: an auto-follow chain and a jump
        // have to agree with the cue list about what is in the show tonight.
        Assert.False(await executor.FireAsync(cue.Id));
        Assert.Empty(host.Played);
    }

    [Fact]
    public async Task AnUnknownCueIsRefused()
    {
        var (executor, host, _) = Show(Media("1"));

        Assert.False(await executor.FireAsync(Guid.NewGuid()));
        Assert.Empty(host.Played);
    }

    [Fact]
    public async Task ACommentFiresSuccessfullyWithoutPlayingAnything()
    {
        var comment = new CommentCueNode { Number = new CueNumber("1"), Note = "marker" };
        var (executor, host, _) = Show(comment);

        // Success, so an auto-continue chain runs straight THROUGH a marker rather than stopping on it.
        Assert.True(await executor.FireAsync(comment.Id));
        Assert.Empty(host.Played);
    }

    // ── waits ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APreWaitHappensBeforeTheCueFires()
    {
        var cue = Media("1");
        cue.PreWaitMs = 2_000;
        var (executor, host, _) = Show(cue);

        await executor.FireAsync(cue.Id);

        Assert.Equal([TimeSpan.FromSeconds(2)], host.Waits);
        Assert.Single(host.Played);
    }

    [Fact]
    public async Task APreWaitAppliesToEveryKindNotJustPlayableOnes()
    {
        var action = new ActionCueNode { Number = new CueNumber("1"), PreWaitMs = 500, Address = "/go" };
        var (executor, host, _) = Show(action);

        // "Wait two seconds, then tell the lighting desk" is an ordinary thing to author.
        await executor.FireAsync(action.Id);

        Assert.Equal([TimeSpan.FromMilliseconds(500)], host.Waits);
        Assert.Single(host.Actions);
    }

    [Fact]
    public async Task AShowStoppingDuringAPreWaitCancelsTheCue()
    {
        var cue = Media("1");
        cue.PreWaitMs = 5_000;
        var (executor, host, _) = Show(cue);
        host.Cancelled = true;

        Assert.False(await executor.FireAsync(cue.Id));
        Assert.Empty(host.Played);
    }

    // ── auto-continue ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoContinueFiresTheNextCueAndAdvancesTheCursor()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Continue;
        var second = Media("2");
        var (executor, host, project) = Show(first, second);

        await executor.FireAsync(first.Id);

        Assert.Equal([first.Id, second.Id], host.Played);

        // The cursor is moved PAST the cue that is about to fire, not onto it — so with nothing after
        // the second cue it clears. A cursor left sitting on a cue that has already played would fire
        // it again on the next GO.
        var (list, cursor) = Assert.Single(host.Standby);
        Assert.Equal(project.CueLists[0].Id, list);
        Assert.Null(cursor);
    }

    [Fact]
    public async Task AutoContinueRunsThroughAComment()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Continue;
        var comment = new CommentCueNode { Number = new CueNumber("2"), Trigger = CueTrigger.Continue };
        var last = Media("3");
        var (executor, host, _) = Show(first, comment, last);

        await executor.FireAsync(first.Id);

        Assert.Equal([first.Id, last.Id], host.Played);
    }

    [Fact]
    public async Task AutoContinueStopsWhenTheCueDidNotFire()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Continue;
        var (executor, host, _) = Show(first, Media("2"));
        host.PlayFails = true;

        await executor.FireAsync(first.Id);

        // A chain that carried on past a cue that never started would leave the show ahead of itself.
        Assert.Empty(host.Played);
    }

    [Fact]
    public async Task APostWaitHappensBetweenChainedCues()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Continue;
        first.PostWaitMs = 1_500;
        var (executor, host, _) = Show(first, Media("2"));

        await executor.FireAsync(first.Id);

        Assert.Contains(TimeSpan.FromMilliseconds(1_500), host.Waits);
    }

    // ── prepared follows (ProjectSettings.FollowLeadMs) ───────────────────────────────────────

    [Fact]
    public async Task WithNoFollowLead_TheSuccessorOnlyOpensAtTheOutPoint()
    {
        // The historical shape, and still the default: nothing happens on the pre-end notification, so
        // the successor's whole media open lands AFTER the edge.
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var next = Media("2");
        var (executor, host, _) = Show(first, next);

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);
        Assert.Equal([first.Id], host.Played);

        await executor.OnNaturalEndAsync(first.Id);
        Assert.Equal([first.Id, next.Id], host.Played);
    }

    [Fact]
    public async Task WithAFollowLead_TheSuccessorIsPreparedEarlyAndStartsOnTheOutPoint()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var next = Media("2");
        var (executor, host, project) = Show(first, next);
        project.Settings.FollowLeadMs = 2_000;

        await executor.FireAsync(first.Id);

        // The lead OPENS it - the fake only records a play once its edge is released, so "not yet
        // played" here is exactly "opened but holding at the edge".
        await executor.OnApproachingEndAsync(first.Id);
        Assert.Equal([first.Id], host.Played);

        // ...and the out-point releases it. Same instant it would have started before; the difference
        // is that the open is already behind us.
        await executor.OnNaturalEndAsync(first.Id);
        Assert.Equal([first.Id, next.Id], host.Played);
    }

    [Fact]
    public async Task APreparedSuccessorIsStartedExactlyOnce()
    {
        // The out-point must not ALSO take the ordinary cold path - that would fire the successor twice.
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var next = Media("2");
        var (executor, host, project) = Show(first, next);
        project.Settings.FollowLeadMs = 2_000;

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);
        await executor.OnNaturalEndAsync(first.Id);

        Assert.Equal([first.Id, next.Id], host.Played);
        Assert.Single(host.Played, id => id == next.Id);
    }

    [Fact]
    public async Task StoppingTheOutgoingCueRollsBackItsPreparedSuccessor()
    {
        // A stop is the operator vetoing the chain. The successor was already opened and holding at its
        // edge; the stop must roll it back rather than leave it parked, waiting to be released by
        // whatever reaches an out-point next.
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var next = Media("2");
        var (executor, host, project) = Show(first, next);
        project.Settings.FollowLeadMs = 2_000;

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);

        executor.OnStopped(first.Id);
        Assert.Equal([first.Id], host.Played); // rolled back, not started

        // A stopped cue does not go on to reach a natural end in a real show, but if one arrives it
        // must not find a parked voice to release - it takes the ordinary cold path, exactly as it
        // does with no lead configured, and starts the successor exactly once.
        await executor.OnNaturalEndAsync(first.Id);
        Assert.Equal([first.Id, next.Id], host.Played);
    }

    [Fact]
    public async Task APostWaitOnTheOutgoingCueDisablesTheLead()
    {
        // An authored wait sits between the out-point and the successor, so the out-point is NOT the
        // successor's start and there is no fixed edge to schedule against.
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        first.PostWaitMs = 500;
        var next = Media("2");
        var (executor, host, project) = Show(first, next);
        project.Settings.FollowLeadMs = 2_000;

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);
        Assert.Equal([first.Id], host.Played);

        await executor.OnNaturalEndAsync(first.Id);

        // Still advances - through the ordinary path, honouring the wait.
        Assert.Equal([first.Id, next.Id], host.Played);
        Assert.Contains(TimeSpan.FromMilliseconds(500), host.Waits);
    }

    [Fact]
    public async Task APreWaitOnTheSuccessorDisablesTheLead()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var next = Media("2");
        next.PreWaitMs = 400;
        var (executor, host, project) = Show(first, next);
        project.Settings.FollowLeadMs = 2_000;

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);
        Assert.Equal([first.Id], host.Played);

        await executor.OnNaturalEndAsync(first.Id);
        Assert.Equal([first.Id, next.Id], host.Played);
        Assert.Contains(TimeSpan.FromMilliseconds(400), host.Waits);
    }

    [Fact]
    public async Task ALeadResolvesTheSameSuccessorTheColdPathWould()
    {
        // A lead that prepared a DIFFERENT cue from the one the chain picks would be worse than no
        // lead, so the disabled-cue policy has to be honoured identically on both paths.
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var disabled = Media("2");
        disabled.Enabled = false;
        var (executor, host, project) = Show(first, disabled);
        project.Settings.FollowLeadMs = 2_000;
        project.Settings.DisabledCueFollow = DisabledCueFollow.StopTheChain;
        project.Settings.AtListEnd = AtListEnd.Loop;

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);
        await executor.OnNaturalEndAsync(first.Id);

        Assert.Equal([first.Id], host.Played);
    }

    [Fact]
    public async Task ALeadFollowsAnExplicitEndTarget()
    {
        var first = Media("1");
        var skipped = Media("2");
        var target = Media("3");
        var (executor, host, project) = Show(first, skipped, target);
        first.EndTargetCueId = target.Id;
        project.Settings.FollowLeadMs = 2_000;

        await executor.FireAsync(first.Id);
        await executor.OnApproachingEndAsync(first.Id);
        await executor.OnNaturalEndAsync(first.Id);

        Assert.Equal([first.Id, target.Id], host.Played);
    }

    [Fact]
    public async Task FollowStopsAtADisabledCueEvenWhenTheListWouldLoop()
    {
        var first = Media("1");
        first.Trigger = CueTrigger.Follow;
        var disabled = Media("2");
        disabled.Enabled = false;
        var (executor, host, project) = Show(first, disabled);
        project.Settings.DisabledCueFollow = DisabledCueFollow.StopTheChain;
        project.Settings.AtListEnd = AtListEnd.Loop;

        await executor.FireAsync(first.Id);
        await executor.OnNaturalEndAsync(first.Id);

        // A disabled successor is a stop, not the end of the list. Treating it as the latter fired Q1
        // again because this list loops.
        Assert.Equal([first.Id], host.Played);
    }

    [Fact]
    public async Task AnAutoContinueChainIsBounded()
    {
        // A jump back to its own list plus auto-continue is a legal way to author an infinite loop.
        var loop = Media("1");
        loop.Trigger = CueTrigger.Continue;
        var jump = new JumpCueNode { Number = new CueNumber("2"), Label = "back" };
        var (executor, host, _) = Show(loop, jump);
        jump.TargetCueIds = [loop.Id];

        await executor.FireAsync(loop.Id);

        // Bounded rather than hung: one reported line beats a frozen app.
        Assert.Contains(host.Problems, problem => problem.Contains("jump loop", StringComparison.Ordinal));
        Assert.True(host.Played.Count <= CueExecutor.MaxChainDepth + 2, $"played {host.Played.Count}");
    }

    // ── groups ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAllTogetherGroupFiresEveryChildInOrder()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.AllTogether };
        group.Children.AddRange([Media("1.1"), Media("1.2"), Media("1.3")]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        // In order, because the group's layer order is the order the canvas receives them in.
        Assert.Equal(group.Children.Select(child => child.Id), host.Played);
    }

    [Fact]
    public async Task AnAllTogetherGroupOpensItsChildrenAsOneBatch()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.AllTogether };
        group.Children.AddRange([Media("1.1"), Media("1.2"), Media("1.3")]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        // ONE batch, not three fires. Opened one after another, each child's media is opened before the
        // next is even asked for, so a group of eleven stems starts as a staircase — each late by the
        // sum of every open before it — and the GO costs the sum of all of them.
        var batch = Assert.Single(host.PlayedTogether);
        Assert.Equal(group.Children.Select(child => child.Id), batch);
    }

    [Fact]
    public async Task AChildWithAPreWaitIsNotBatchedWithTheRest()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.AllTogether };
        var late = Media("1.3");
        late.PreWaitMs = 2_000;
        group.Children.AddRange([Media("1.1"), Media("1.2"), late]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        // A pre-wait is a cue asking to start LATER; putting it in a batch whose whole purpose is one
        // start edge would either lose the wait or hold the others back behind it.
        var batch = Assert.Single(host.PlayedTogether);
        Assert.Equal(2, batch.Count);
        Assert.DoesNotContain(late.Id, batch);

        // It still plays, and the group still fires everything.
        Assert.Equal(3, host.Played.Count);
        Assert.Contains(late.Id, host.Played);
    }

    [Fact]
    public async Task AnAllTogetherGroupWithOneChildDoesNotNeedABatch()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.AllTogether };
        var only = Media("1.1");
        group.Children.Add(only);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        Assert.Empty(host.PlayedTogether);
        Assert.Equal([only.Id], host.Played);
    }

    [Fact]
    public async Task ANonClipChildOfAnAllTogetherGroupStillFiresOnItsOwnPath()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.AllTogether };
        var action = new ActionCueNode { Number = new CueNumber("1.3"), Label = "Lights" };
        group.Children.AddRange([Media("1.1"), Media("1.2"), action]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        // An action cue is not a clip and has nothing to open, so it is not part of the batch — but it
        // still has to happen, and the batch must not swallow it.
        var batch = Assert.Single(host.PlayedTogether);
        Assert.Equal(2, batch.Count);
        Assert.Single(host.Actions);
    }

    [Fact]
    public async Task AGroupItselfIsNeverPlayed()
    {
        var group = new GroupCueNode { Number = new CueNumber("1") };
        group.Children.Add(Media("1.1"));
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        // The group holds no voice — its children do. The Active panel shows what is making noise.
        Assert.DoesNotContain(group.Id, host.Played);
    }

    [Fact]
    public async Task APlaylistGroupFiresOnlyItsFirstChild()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.Playlist };
        group.Children.AddRange([Media("1.1"), Media("1.2")]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        // Each child's natural end chains to the next; firing them all would play the set at once.
        Assert.Equal([group.Children[0].Id], host.Played);
    }

    [Fact]
    public async Task APlaylistNaturalEndAdvancesToItsNextChild()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.Playlist };
        group.Children.AddRange([Media("1.1"), Media("1.2")]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);

        Assert.Equal(group.Children.Select(child => child.Id), host.Played);
    }

    [Fact]
    public async Task PlaylistPassCountRunsBeforeHoldAtEnd()
    {
        var group = new GroupCueNode
        {
            Number = "1",
            FireMode = GroupFireMode.Playlist,
            LoopCount = 2,
            AtEnd = AtListEnd.Hold,
            Children = [Media("1.1"), Media("1.2")],
        };
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);
        await executor.OnNaturalEndAsync(group.Children[1].Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);
        await executor.OnNaturalEndAsync(group.Children[1].Id);

        Assert.Equal(
            [group.Children[0].Id, group.Children[1].Id, group.Children[0].Id, group.Children[1].Id],
            host.Played);
    }

    [Fact]
    public async Task PlaylistPlayCountLimitsTheItemsChosenPerPass()
    {
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Playlist, LoopCount = 2, PlayCount = 1,
            Children = [Media("1.1"), Media("1.2"), Media("1.3")],
        };
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);

        Assert.Equal([group.Children[0].Id, group.Children[0].Id], host.Played);
    }

    [Fact]
    public async Task PlaylistPassCountRunsBeforeAdvancingToTheNextList()
    {
        var group = new GroupCueNode
        {
            Number = "1",
            FireMode = GroupFireMode.Playlist,
            LoopCount = 2,
            AtEnd = AtListEnd.NextList,
            Children = [Media("1.1"), Media("1.2")],
        };
        var after = Media("2");
        var project = new HaCueProject
        {
            CueLists =
            [
                new CueList { Name = "Act 1", Cues = [group] },
                new CueList { Name = "Act 2", Cues = [after] },
            ],
        };
        var host = new FakeCueHost(project);
        var executor = new CueExecutor(host);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);
        await executor.OnNaturalEndAsync(group.Children[1].Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);
        await executor.OnNaturalEndAsync(group.Children[1].Id);

        Assert.Equal(
            [group.Children[0].Id, group.Children[1].Id, group.Children[0].Id, group.Children[1].Id, after.Id],
            host.Played);
    }

    [Fact]
    public async Task APlaylistCrossfadeUsesItsCustomCurveAndConsumesTheOutgoingEnd()
    {
        var first = Media("1.1");
        first.Trigger = CueTrigger.Follow;
        var second = Media("1.2");
        var group = new GroupCueNode
        {
            Number = new CueNumber("1"),
            FireMode = GroupFireMode.Playlist,
            CrossfadeMs = 1_250,
            CrossfadeCurve = new CurveSpec
            {
                Points =
                [
                    new FadeCurvePoint(0, 0),
                    new FadeCurvePoint(0.4, 0.15),
                    new FadeCurvePoint(1, 1),
                ],
            },
            Children = [first, second],
        };
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        await executor.OnApproachingEndAsync(first.Id);

        var transition = Assert.Single(host.Transitions, item => item.Cue == second.Id);
        Assert.Equal(TimeSpan.FromMilliseconds(1_250), transition.Duration);
        Assert.True(transition.Curve.IsCustom);

        await executor.OnNaturalEndAsync(first.Id);

        Assert.Equal(1, host.Played.Count(id => id == second.Id));
    }

    [Fact]
    public async Task FirstCueOnlyNeverFiresTheOtherChildren()
    {
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.FirstCueOnly,
            Children = [Media("1.1"), Media("1.2")],
        };
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(group.Children[0].Id);

        Assert.Equal([group.Children[0].Id], host.Played);
    }

    [Fact]
    public async Task ArmedListAdvancesOnlyOnOperatorGoAndHonoursPlayCount()
    {
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.ArmedList, LoopCount = 2,
            Children = [Media("1.1"), Media("1.2")],
        };
        var (executor, host, _) = Show(group);

        for (var index = 0; index < 4; index++)
        {
            await executor.FireAsync(group.Id);
            Assert.Equal(index + 1, host.Played.Count);
            await executor.OnNaturalEndAsync(host.Played[^1]);
            Assert.Equal(index + 1, host.Played.Count); // natural end never advances an armed list
        }

        Assert.Equal(
            [group.Children[0].Id, group.Children[1].Id, group.Children[0].Id, group.Children[1].Id],
            host.Played);
    }

    [Fact]
    public async Task ArmedListOwnsChildFollowAndEndTarget()
    {
        var target = Media("2");
        var child = Media("1.1");
        child.Trigger = CueTrigger.Follow;
        child.EndTargetCueId = target.Id;
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.ArmedList, Children = [child, Media("1.2")],
        };
        var (executor, host, _) = Show(group, target);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(child.Id);

        Assert.Equal([child.Id], host.Played);
    }

    [Fact]
    public async Task StoppingAnArmedListsFinalItemLetsTheNextGoStartAgain()
    {
        var child = Media("1.1");
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.ArmedList, Children = [child],
        };
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        executor.OnStopped(child.Id); // a manual stop has no natural-end callback
        await executor.FireAsync(group.Id);

        Assert.Equal([child.Id, child.Id], host.Played);
    }

    [Fact]
    public async Task StopAllStateResetRestartsAnArmedPassAtItsFirstItem()
    {
        var first = Media("1.1");
        var second = Media("1.2");
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.ArmedList, Children = [first, second],
        };
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        executor.ResetTransientState();
        await executor.FireAsync(group.Id);

        Assert.Equal([first.Id, first.Id], host.Played);
    }

    [Fact]
    public async Task MediaEndTargetFiresInsteadOfOrdinaryFollow()
    {
        var first = Media("1");
        var skipped = Media("2");
        var target = Media("3");
        first.Trigger = CueTrigger.Follow;
        first.EndTargetCueId = target.Id;
        var (executor, host, _) = Show(first, skipped, target);

        await executor.FireAsync(first.Id);
        await executor.OnNaturalEndAsync(first.Id);

        Assert.Equal([first.Id, target.Id], host.Played);
        Assert.DoesNotContain(skipped.Id, host.Played);
    }

    [Fact]
    public async Task PlaylistOwnershipPrecedesAChildEndTarget()
    {
        var target = Media("2");
        var first = Media("1.1");
        first.EndTargetCueId = target.Id;
        var second = Media("1.2");
        var group = new GroupCueNode
        {
            Number = "1", FireMode = GroupFireMode.Playlist, Children = [first, second],
        };
        var (executor, host, _) = Show(group, target);

        await executor.FireAsync(group.Id);
        await executor.OnNaturalEndAsync(first.Id);

        Assert.Equal([first.Id, second.Id], host.Played);
    }

    [Fact]
    public async Task ATimelineGroupSchedulesItsChildrenAtTheirOffsets()
    {
        var group = new GroupCueNode { Number = new CueNumber("1"), FireMode = GroupFireMode.Timeline };
        var first = Media("1.1");
        var second = Media("1.2");
        second.TimelineOffsetMs = 8_000;
        group.Children.AddRange([first, second]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);
        await executor.WaitForTimelineCompletionAsync(group.Id);

        // Independently released from the same master coordinate; the virtual clock makes the wait instant.
        Assert.Equal(
            [(first.Id, TimeSpan.Zero), (second.Id, TimeSpan.FromSeconds(8))],
            host.TimelineStarts.Select(entry => (entry.Cue, entry.MasterTime)));
    }

    [Fact]
    public async Task ADisabledChildIsSkipped()
    {
        var group = new GroupCueNode { Number = new CueNumber("1") };
        var off = Media("1.1");
        off.Enabled = false;
        group.Children.AddRange([off, Media("1.2")]);
        var (executor, host, _) = Show(group);

        await executor.FireAsync(group.Id);

        Assert.Equal([group.Children[1].Id], host.Played);
    }

    [Fact]
    public async Task AnEmptyGroupSucceedsWithoutDoingAnything()
    {
        var group = new GroupCueNode { Number = new CueNumber("1") };
        var (executor, host, _) = Show(group);

        // Succeeds, so an auto-continue chain is not stopped by an empty group somebody left in.
        Assert.True(await executor.FireAsync(group.Id));
        Assert.Empty(host.Played);
    }

    // ── jumps ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AJumpMovesTheCursorAndFiresTheTarget()
    {
        var target = Media("1");
        var jump = new JumpCueNode { Number = new CueNumber("2"), FireOnArrival = true };
        var (executor, host, project) = Show(target, jump);
        jump.TargetCueIds = [target.Id];

        await executor.FireAsync(jump.Id);

        Assert.Contains(target.Id, host.Played);
        Assert.NotEmpty(host.Standby);
    }

    [Fact]
    public async Task AJumpThatDoesNotFireOnArrivalOnlyMovesTheCursor()
    {
        var target = Media("1");
        var jump = new JumpCueNode { Number = new CueNumber("2"), FireOnArrival = false };
        var (executor, host, _) = Show(target, jump);
        jump.TargetCueIds = [target.Id];

        await executor.FireAsync(jump.Id);

        Assert.Empty(host.Played);
        Assert.NotEmpty(host.Standby);
    }

    [Fact]
    public async Task AJumpWithNoLiveTargetReportsRatherThanDoingNothingQuietly()
    {
        var jump = new JumpCueNode { Number = new CueNumber("1"), Label = "nowhere" };
        var (executor, host, _) = Show(jump);

        Assert.False(await executor.FireAsync(jump.Id));
        Assert.Contains(host.Problems, problem => problem.Contains("no live target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJumpSkipsADisabledTarget()
    {
        var off = Media("1");
        off.Enabled = false;
        var jump = new JumpCueNode { Number = new CueNumber("2") };
        var (executor, host, _) = Show(off, jump);
        jump.TargetCueIds = [off.Id];

        Assert.False(await executor.FireAsync(jump.Id));
        Assert.Contains(host.Problems, problem => problem.Contains("no live target", StringComparison.Ordinal));
    }

    // ── fades ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFadeToSilenceStopsItsTargets()
    {
        var bed = Media("1");
        var fade = new FadeCueNode
        {
            Number = new CueNumber("2"),
            TargetCueIds = [bed.Id],
            ToLevelDb = GainRange.SilenceFloorDb,
            StopTargetsWhenComplete = true,
        };

        var (executor, host, _) = Show(bed, fade);

        await executor.FireAsync(fade.Id);

        Assert.Equal([bed.Id], host.Stopped);
        Assert.Contains(bed.Id, host.Faded);
    }

    [Fact]
    public async Task AFadeToALevelChangesTheLevelAndLeavesTheCuePlaying()
    {
        var bed = Media("1");
        var fade = new FadeCueNode
        {
            Number = new CueNumber("2"),
            TargetCueIds = [bed.Id],
            ToLevelDb = -12,
        };

        var (executor, host, _) = Show(bed, fade);

        await executor.FireAsync(fade.Id);

        Assert.Empty(host.Stopped);
        Assert.Equal([(bed.Id, -12d)], host.Levels);
    }

    [Fact]
    public async Task FadeEverythingSoundingActsOnWhatIsActuallySounding()
    {
        var one = Media("1");
        var two = Media("2");
        var fade = new FadeCueNode
        {
            Number = new CueNumber("3"),
            FadeEverythingSounding = true,
            ToLevelDb = GainRange.SilenceFloorDb,
        };

        var (executor, host, _) = Show(one, two, fade);
        await executor.FireAsync(one.Id);
        await executor.FireAsync(two.Id);

        await executor.FireAsync(fade.Id);

        Assert.Equal([one.Id, two.Id], host.Stopped);
    }

    [Fact]
    public async Task AFadeOnLogicalOutputsRampsThePatchAndWritesTheDocument()
    {
        var channel = new LogicalAudioChannel { Name = "Fold L" };
        var line = new AudioLineDefinition { Name = "out" };
        var fade = new FadeCueNode
        {
            Number = new CueNumber("1"),
            TargetChannelIds = [channel.Id],
            ToLevelDb = -20,
            DurationMs = 3_000,
        };

        var (executor, host, project) = Show(fade);
        project.AudioPatch.LogicalChannels.Add(channel);
        project.AudioLines.Add(line);
        project.AudioPatch.Cells.Add(
            new PatchCell { LogicalChannelId = channel.Id, LineId = line.Id, LineChannel = 0 });

        await executor.FireAsync(fade.Id);

        var (destination, duration) = Assert.Single(host.Patches);
        Assert.Equal(TimeSpan.FromSeconds(3), duration);
        Assert.Equal(-20, destination[0].GainDb, 3);
        // The document keeps what the fade landed on, or the next unrelated reload would undo it.
        Assert.Equal(-20, project.AudioPatch.Cells[0].GainDb, 3);
    }

    // ── patch cues ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APatchCueRecallsItsSnapshotAndRampsTowardIt()
    {
        var channel = new LogicalAudioChannel { Name = "Fold L" };
        var line = new AudioLineDefinition { Name = "out" };
        var snapshot = new PatchSnapshot
        {
            Name = "down",
            Cells = [new PatchCell
            {
                LogicalChannelId = channel.Id, LineId = line.Id, LineChannel = 0, GainDb = -12,
            }],
        };

        var patch = new PatchCueNode
        {
            Number = new CueNumber("1"), SnapshotId = snapshot.Id, FadeMs = 1_500,
        };

        var (executor, host, project) = Show(patch);
        project.AudioPatch.LogicalChannels.Add(channel);
        project.AudioLines.Add(line);
        project.PatchSnapshots.Add(snapshot);
        project.AudioPatch.Cells.Add(
            new PatchCell { LogicalChannelId = channel.Id, LineId = line.Id, LineChannel = 0 });

        Assert.True(await executor.FireAsync(patch.Id));

        var (_, duration) = Assert.Single(host.Patches);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), duration);
        Assert.Equal(-12, project.AudioPatch.Cells[0].GainDb, 3);
    }

    [Fact]
    public async Task APatchCueWhoseSnapshotIsGoneReportsAndDoesNotRamp()
    {
        var patch = new PatchCueNode
        {
            Number = new CueNumber("1"), Label = "recall", SnapshotId = Guid.NewGuid(),
        };

        var (executor, host, _) = Show(patch);

        await executor.FireAsync(patch.Id);

        Assert.Empty(host.Patches);
        Assert.NotEmpty(host.Problems);
    }

    // ── action cues ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnActionCueSendsToItsEndpoint()
    {
        var endpoint = new ActionEndpoint { Name = "Eos", Host = "10.0.0.1", Port = 8000 };
        var action = new ActionCueNode
        {
            Number = new CueNumber("1"), EndpointId = endpoint.Id, Address = "/eos/cue/1/fire",
        };

        var (executor, host, project) = Show(action);
        project.ActionEndpoints.Add(endpoint);

        Assert.True(await executor.FireAsync(action.Id));

        var (sent, to) = Assert.Single(host.Actions);
        Assert.Equal("/eos/cue/1/fire", sent.Address);
        Assert.Same(endpoint, to);
    }

    [Fact]
    public async Task AnActionThatCouldNotBeSentReportsAndFails()
    {
        var action = new ActionCueNode { Number = new CueNumber("1"), Address = "/go" };
        var (executor, host, _) = Show(action);
        host.ActionFailure = "the desk did not answer";

        Assert.False(await executor.FireAsync(action.Id));
        Assert.Contains("the desk did not answer", host.Problems);
    }

    [Fact]
    public async Task AFailedActionStopsAnAutoContinueChain()
    {
        var action = new ActionCueNode
        {
            Number = new CueNumber("1"), Address = "/go", Trigger = CueTrigger.Continue,
        };

        var (executor, host, _) = Show(action, Media("2"));
        host.ActionFailure = "unreachable";

        await executor.FireAsync(action.Id);

        // The chain must not run on past a cue that did not do its job.
        Assert.Empty(host.Played);
    }

    [Fact]
    public async Task AnAutomationCueIsDispatchedAsItsOwnCueKind()
    {
        var automation = new AutomationCueNode
        {
            Number = new CueNumber("1"),
            Label = "Bring up projection",
            DurationMs = 2_000,
        };
        var (executor, host, _) = Show(automation);

        Assert.True(await executor.FireAsync(automation.Id));
        Assert.Equal([automation.Id], host.Automations);
        Assert.Empty(host.Played);
    }
}
