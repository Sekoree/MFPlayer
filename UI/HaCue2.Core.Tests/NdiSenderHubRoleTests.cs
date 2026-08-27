using HaCue2.Engine;
using Xunit;
using Xunit.Abstractions;

namespace HaCue2.Core.Tests;

/// <summary>
/// The hub's single-writer-per-half rule (review NDI-02): each sender name has at most one live
/// video claim and one live audio claim - a duplicate claim is refused loudly instead of putting
/// two pump threads on one non-thread-safe native sender. Creating a hub sender publishes a real
/// NDI source, so these follow the <c>MFP_RUN_NDI_TESTS=1</c> opt-in gate the loopback soak uses
/// and self-skip in the hermetic suite.
/// </summary>
public sealed class NdiSenderHubRoleTests(ITestOutputHelper output)
{
    private static bool OptedIn => Environment.GetEnvironmentVariable("MFP_RUN_NDI_TESTS") == "1";

    [Fact]
    public void SecondClaimOfTheSameHalf_IsRefused_AndFreedByRelease()
    {
        if (!OptedIn)
        {
            output.WriteLine("skipped: set MFP_RUN_NDI_TESTS=1 to run the native NDI hub role test");
            return;
        }

        using var hub = new NdiSenderHub();
        var name = $"MFP RoleTest {Guid.NewGuid():N}";

        var video = hub.Acquire(name, NdiSenderRole.Video);
        var audio = hub.Acquire(name, NdiSenderRole.Audio);
        Assert.Same(video.Sender, audio.Sender);

        var refusedVideo = Assert.Throws<InvalidOperationException>(() => hub.Acquire(name, NdiSenderRole.Video));
        Assert.Contains("video", refusedVideo.Message, StringComparison.Ordinal);
        var refusedAudio = Assert.Throws<InvalidOperationException>(() => hub.Acquire(name, NdiSenderRole.Audio));
        Assert.Contains("audio", refusedAudio.Message, StringComparison.Ordinal);

        // Releasing a half frees exactly that half - the bay APPLY cycle - while the sender survives
        // because the other half still holds it.
        audio.Dispose();
        using var reAudio = hub.Acquire(name, NdiSenderRole.Audio);
        Assert.Same(video.Sender, reAudio.Sender);

        video.Dispose();
    }
}
