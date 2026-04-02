using NDILib;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// Verifies <see cref="NDIRuntime"/> reference-counting semantics (P2.12).
/// These tests may be skipped on CI environments where the NDI SDK is not installed.
/// </summary>
public sealed class NDIRuntimeLifecycleTests
{
    [Fact]
    public void Create_TwiceDisposeBoth_RefCountingSemantics()
    {
        // First Create
        var code1 = NDIRuntime.Create(out var runtime1);

        // If NDI SDK not installed, skip gracefully
        if (code1 != 0)
        {
            Assert.Null(runtime1);
            return; // NDI not available — skip test
        }

        Assert.NotNull(runtime1);

        // Second Create — should succeed (ref-counted)
        var code2 = NDIRuntime.Create(out var runtime2);
        Assert.Equal(0, code2);
        Assert.NotNull(runtime2);

        // Dispose first — should NOT destroy the runtime (ref count > 0)
        runtime1!.Dispose();

        // Second instance should still be usable (version call should not crash)
        var version = NDIRuntime.Version;
        Assert.False(string.IsNullOrEmpty(version));

        // Dispose second — should destroy the runtime (ref count == 0)
        runtime2!.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var code = NDIRuntime.Create(out var runtime);
        if (code != 0)
            return; // NDI not available — skip test

        Assert.NotNull(runtime);
        runtime!.Dispose();
        runtime.Dispose(); // Should not throw or double-destroy
    }
}

