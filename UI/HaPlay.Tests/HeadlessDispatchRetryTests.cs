using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Pins the retry policy that keeps the PerTest app-init race from failing a test
/// (<see cref="HeadlessDispatchExtensions.IsHeadlessAppInitRace"/> carries the mechanism). The race itself is
/// a microsecond-wide window between two calls inside Avalonia, so it cannot be provoked on demand - what CAN
/// be pinned deterministically is the discriminator and the loop, which is where the risk actually lives: too
/// loose and a genuine "you touched the wrong thread" defect is retried into an intermittent pass; too tight
/// and the flake comes straight back.
/// <para>The stack-frame half is tested by throwing from methods with the REAL frame names rather than by
/// fabricating a <see cref="System.Exception.StackTrace"/> (which cannot be set), so these assertions exercise
/// the same string the production check reads.</para>
/// </summary>
public sealed class HeadlessDispatchRetryTests
{
    private static InvalidOperationException UiThreadOwnershipFailure() =>
        new("The calling thread cannot access this object because a different thread owns it.");

    /// <summary>Named for the Avalonia frame the discriminator looks for, so throwing here produces a stack
    /// trace containing it - which is precisely the evidence "the harness could not stand the app up".</summary>
    private static void EnsureIsolatedApplication() => throw UiThreadOwnershipFailure();

    /// <summary>The other accepted frame - <c>AppBuilder.SetupUnsafe</c>.</summary>
    private static void SetupUnsafe() => throw UiThreadOwnershipFailure();

    /// <summary>Stands in for a test body that really did touch the dispatcher from the wrong thread: same
    /// exception type, same message, but raised from nowhere near app init.</summary>
    private static void TestBodyTouchedTheWrongThread() => throw UiThreadOwnershipFailure();

    private static Exception Captured(Action throwing)
    {
        try
        {
            throwing();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("the probe did not throw");
    }

    [Fact]
    public void AppInitRace_IsRecognised_FromEitherAvaloniaFrame()
    {
        Assert.True(HeadlessDispatchExtensions.IsHeadlessAppInitRace(Captured(EnsureIsolatedApplication)));
        Assert.True(HeadlessDispatchExtensions.IsHeadlessAppInitRace(Captured(SetupUnsafe)));
    }

    [Fact]
    public void SameFailureFromTheTestBody_IsNotRetried()
    {
        // The message alone must never be enough: retrying a body-thrown ownership error would turn a real
        // defect into an intermittent pass, which is the opposite of what this whole mechanism is for.
        Assert.False(
            HeadlessDispatchExtensions.IsHeadlessAppInitRace(Captured(TestBodyTouchedTheWrongThread)));
    }

    [Fact]
    public void UnrelatedFailures_AreNotRetried()
    {
        Assert.False(HeadlessDispatchExtensions.IsHeadlessAppInitRace(null));
        Assert.False(HeadlessDispatchExtensions.IsHeadlessAppInitRace(
            Captured(() => throw new InvalidOperationException("something else entirely"))));
        // An assertion failure carries no ownership message, and is the single most important thing that
        // must NOT be retried - a retried assertion is a test that passes on the second roll of the dice.
        Assert.False(HeadlessDispatchExtensions.IsHeadlessAppInitRace(
            Captured(() => Assert.Equal(1, 2))));
    }

    [Fact]
    public void WrappedRace_IsRecognised_ThroughInnerExceptions()
    {
        var wrapped = new Exception("dispatch failed", Captured(EnsureIsolatedApplication));
        Assert.True(HeadlessDispatchExtensions.IsHeadlessAppInitRace(wrapped));
    }

    [Fact]
    public async Task Retry_RunsTheBodyAgain_AndReturnsTheSecondResult()
    {
        var attempts = 0;
        var value = await HeadlessDispatchExtensions.RetryAsync(() =>
        {
            attempts++;
            if (attempts == 1)
                EnsureIsolatedApplication();
            return Task.FromResult("ok");
        });

        Assert.Equal(2, attempts);
        Assert.Equal("ok", value);
    }

    [Fact]
    public async Task Retry_GivesUp_RatherThanLoopingForever()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HeadlessDispatchExtensions.RetryAsync(() =>
            {
                attempts++;
                EnsureIsolatedApplication();
                return Task.CompletedTask;
            }));

        // Bounded: a permanently wedged session must surface as a failure, not as a hang.
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task NonRaceFailure_PropagatesOnTheFirstAttempt()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HeadlessDispatchExtensions.RetryAsync(() =>
            {
                attempts++;
                TestBodyTouchedTheWrongThread();
                return Task.CompletedTask;
            }));

        Assert.Equal(1, attempts);
    }
}
