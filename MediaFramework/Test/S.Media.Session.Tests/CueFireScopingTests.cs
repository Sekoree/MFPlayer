using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// The reach of each stop form over PENDING scheduled fires (batches prepared and parked at an absolute
/// start edge). The regression these pin: <c>CancelActiveFire</c> once cancelled every pending scheduled
/// fire, so a per-cue stop of one UNRELATED cue silently killed a timeline event whose voices were waiting
/// for their edge - a window of several seconds per event, mid-show. Surgical stops must reach exactly the
/// batches that contain what they stop; panic/load/dispose must reach everything.
/// </summary>
public sealed class CueFireScopingTests
{
    private static ShowDocument TwoCueDocument() => new(
        Version: 1,
        Cues:
        [
            new CueDefinition("scheduled", 1, "Scheduled"),
            new CueDefinition("other", 2, "Other"),
        ],
        Clips:
        [
            new ShowClipBinding("scheduled", "fake://scheduled"),
            new ShowClipBinding("other", "fake://other"),
        ],
        Compositions: [], Routes: []);

    /// <summary>Parks a one-cue scheduled batch at its start edge and hands back the release lever.</summary>
    private static async Task<(Task<IReadOnlyList<CueExecutionStatus>> Fire, TaskCompletionSource Release)>
        ParkScheduledFireAsync(ShowSession session, CancellationToken token)
    {
        var edgeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fire = session.FireCuesIndependentScheduledAsync(
            [new ScheduledCueStart("scheduled", "scheduled:run")],
            async cancellationToken =>
            {
                edgeEntered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
            token);
        await edgeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (fire, release);
    }

    [Fact]
    public async Task StopCueAsync_OfAnUnrelatedCue_LeavesThePendingScheduledFireWaiting()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoCueDocument());
        var (fire, release) = await ParkScheduledFireAsync(session, CancellationToken.None);

        // The old blanket cancellation made THIS call kill the parked batch.
        await session.StopCueAsync("other");

        release.TrySetResult();
        var statuses = await fire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([CueExecutionStatus.Fired], statuses);
        Assert.Contains(session.Snapshot(), group => group.GroupId == "scheduled:run" && group.IsActive);
    }

    [Fact]
    public async Task StopCueAsync_OfABatchMember_CancelsTheBatch()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoCueDocument());
        var (fire, _) = await ParkScheduledFireAsync(session, CancellationToken.None);

        await session.StopCueAsync("scheduled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(session.Snapshot(), group => group.IsActive);
    }

    [Fact]
    public async Task StopAsync_OfAnUnrelatedGroup_LeavesThePendingScheduledFireWaiting()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoCueDocument());
        var (fire, release) = await ParkScheduledFireAsync(session, CancellationToken.None);

        await session.StopAsync("someOtherGroup");

        release.TrySetResult();
        var statuses = await fire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([CueExecutionStatus.Fired], statuses);
    }

    [Fact]
    public async Task StopAsync_OfTheBatchesRuntimeGroup_CancelsTheBatch()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoCueDocument());
        var (fire, _) = await ParkScheduledFireAsync(session, CancellationToken.None);

        await session.StopAsync("scheduled:run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fire.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopAllAsync_CancelsPendingScheduledFires()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoCueDocument());
        var (fire, _) = await ParkScheduledFireAsync(session, CancellationToken.None);

        await session.StopAllAsync(TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(session.Snapshot(), group => group.IsActive);
    }

    [Fact]
    public async Task AWedgedOpen_TimesTheBatchOut_AndReleasesTheFireLockForLaterFires()
    {
        // The open below never observes cancellation - the shape of a wedged synchronous native open.
        // Without the preparation bound this held the fire lock forever: every subsequent GO on every
        // list blocked for the rest of the show. The bound must fail THIS batch and free the lock.
        var wedged = new WedgedOpenProvider();
        var document = new ShowDocument(
            Version: 1,
            Cues:
            [
                new CueDefinition("stuck", 1, "Stuck"),
                new CueDefinition("healthy", 2, "Healthy"),
            ],
            Clips:
            [
                new ShowClipBinding("stuck", "wedged://clip"),
                new ShowClipBinding("healthy", "fake://healthy"),
            ],
            Compositions: [], Routes: []);
        await using var session = new ShowSession(MediaRegistry.Build(b =>
        {
            b.AddDecoder(wedged);
            b.AddDecoder(new FakeAudioDecoderProvider(chunks: 100_000));
        }));
        await session.LoadDocumentAsync(document);
        session.FireRunnerForTest.BatchPreparationTimeout = TimeSpan.FromMilliseconds(500);
        session.FireRunnerForTest.AbandonedBatchDrainTimeout = TimeSpan.FromMilliseconds(500);

        var fire = session.FireCuesIndependentScheduledAsync(
            [new ScheduledCueStart("stuck", "scheduled:stuck")],
            _ => Task.CompletedTask,
            CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => fire.WaitAsync(TimeSpan.FromSeconds(10)));

        // The decisive assertion: the fire lock is free again - an ordinary fire goes through promptly.
        Assert.Equal(CueExecutionStatus.Fired,
            await session.FireCueAsync("healthy").WaitAsync(TimeSpan.FromSeconds(10)));

        wedged.Unwedge(); // release the abandoned open so its task does not outlive the test
    }
}
