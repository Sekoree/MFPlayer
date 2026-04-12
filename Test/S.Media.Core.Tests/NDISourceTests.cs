using NDILib;
using S.Media.NDI;
using Xunit;

namespace S.Media.Core.Tests;

/// <summary>
/// Tests for <see cref="NDISource"/> source matching, state management,
/// and <see cref="NDISourceState"/> lifecycle.
/// These tests exercise only the pure .NET logic (no native NDI library required).
/// </summary>
public sealed class NDISourceTests
{
    // ── MatchSource tests ────────────────────────────────────────────────────

    private static readonly NDIDiscoveredSource[] TestSources =
    [
        new NDIDiscoveredSource("MY-PC (OBS)", "192.168.1.10:5960"),
        new NDIDiscoveredSource("STUDIO-A (Camera 1)", "192.168.1.20:5960"),
        new NDIDiscoveredSource("STUDIO-A (Camera 2)", "192.168.1.20:5961"),
        new NDIDiscoveredSource("REMOTE-HOST (NDI Source)", "10.0.0.5:5960"),
    ];

    [Fact]
    public void MatchSource_ExactMatch_CaseInsensitive()
    {
        var result = NDISource.MatchSource(TestSources, "my-pc (obs)");
        Assert.NotNull(result);
        Assert.Equal("MY-PC (OBS)", result.Value.Name);
    }

    [Fact]
    public void MatchSource_ExactMatch_Preferred_Over_Contains()
    {
        // "STUDIO-A (Camera 1)" is an exact match; "STUDIO-A (Camera 2)" also contains "Camera"
        var result = NDISource.MatchSource(TestSources, "STUDIO-A (Camera 1)");
        Assert.NotNull(result);
        Assert.Equal("STUDIO-A (Camera 1)", result.Value.Name);
    }

    [Fact]
    public void MatchSource_PartialMatch_HostName()
    {
        var result = NDISource.MatchSource(TestSources, "REMOTE-HOST");
        Assert.NotNull(result);
        Assert.Equal("REMOTE-HOST (NDI Source)", result.Value.Name);
    }

    [Fact]
    public void MatchSource_PartialMatch_SourceName()
    {
        var result = NDISource.MatchSource(TestSources, "OBS");
        Assert.NotNull(result);
        Assert.Equal("MY-PC (OBS)", result.Value.Name);
    }

    [Fact]
    public void MatchSource_PartialMatch_CaseInsensitive()
    {
        var result = NDISource.MatchSource(TestSources, "camera 2");
        Assert.NotNull(result);
        Assert.Equal("STUDIO-A (Camera 2)", result.Value.Name);
    }

    [Fact]
    public void MatchSource_NoMatch_ReturnsNull()
    {
        var result = NDISource.MatchSource(TestSources, "NonExistent");
        Assert.Null(result);
    }

    [Fact]
    public void MatchSource_EmptySources_ReturnsNull()
    {
        var result = NDISource.MatchSource([], "anything");
        Assert.Null(result);
    }

    [Fact]
    public void MatchSource_AmbiguousPartial_ReturnsFirst()
    {
        // Both "STUDIO-A (Camera 1)" and "STUDIO-A (Camera 2)" contain "Camera"
        var result = NDISource.MatchSource(TestSources, "Camera");
        Assert.NotNull(result);
        Assert.Equal("STUDIO-A (Camera 1)", result.Value.Name);
    }

    // ── NDISourceState tests ─────────────────────────────────────────────────

    [Fact]
    public void StateChanged_EventArgs_RoundTrip()
    {
        var args = new NDISourceStateChangedEventArgs(
            NDISourceState.Connected,
            NDISourceState.Reconnecting,
            "MY-PC (OBS)");

        Assert.Equal(NDISourceState.Connected, args.OldState);
        Assert.Equal(NDISourceState.Reconnecting, args.NewState);
        Assert.Equal("MY-PC (OBS)", args.SourceName);
    }

    [Fact]
    public void StateChanged_EventArgs_NullSourceName()
    {
        var args = new NDISourceStateChangedEventArgs(
            NDISourceState.Disconnected,
            NDISourceState.Discovering,
            null);

        Assert.Null(args.SourceName);
    }

    [Fact]
    public void NDISourceState_AllValues_Defined()
    {
        // Verify all expected states exist
        var values = Enum.GetValues<NDISourceState>();
        Assert.Contains(NDISourceState.Disconnected, values);
        Assert.Contains(NDISourceState.Discovering, values);
        Assert.Contains(NDISourceState.Connected, values);
        Assert.Contains(NDISourceState.Reconnecting, values);
        Assert.Equal(4, values.Length);
    }
}

