using S.Media.Audio.MiniAudio;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Audio.Backends.Tests;

/// <summary>
/// Cached-enumeration-context contract (review finding: a fresh ma_context per enumeration call).
/// The disposal contract needs no native library; the repeated-enumeration case exercises the cached
/// context where one is loadable and skips (logging the reason) on a headless runner, matching the
/// conformance suite's gating convention.
/// </summary>
public sealed class MiniAudioBackendContextCacheTests(ITestOutputHelper output)
{
    [Fact]
    public void Dispose_IsIdempotent_AndEnumerationAfterDisposeThrows()
    {
        var backend = new MiniAudioBackend();
        backend.Dispose();
        backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.EnumerateOutputDevices());
        Assert.Throws<ObjectDisposedException>(() => backend.EnumerateInputDevices());
    }

    [Fact]
    public void RepeatedEnumeration_ReusesTheCachedContext_AndStaysWellFormed()
    {
        using var backend = new MiniAudioBackend();
        try
        {
            // Mixed playback/capture calls all flow through the one cached context.
            var first = backend.EnumerateOutputDevices();
            for (var i = 0; i < 3; i++)
            {
                var outputs = backend.EnumerateOutputDevices();
                Assert.Equal(first.Count, outputs.Count);
                Assert.All(outputs, d => Assert.False(string.IsNullOrEmpty(d.Id)));
                _ = backend.EnumerateInputDevices();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            output.WriteLine($"skipped: miniaudio native library not loadable: {ex.Message}");
        }
    }
}
