using HaPlay.Models;
using HaPlay.Playback;
using HaPlay.ViewModels;
using S.Media.Session;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Board-global launch quantization (Ideas/Next-Round-Plan-2026-07-28.md E): a tap on an idle tile
/// arms instead of playing, and the workspace's single pending pump fires it on the next quantum
/// boundary. The transport clock is injected so boundaries are exact and no timer is involved.
/// </summary>
public sealed class SoundboardQuantizedLaunchTests
{
    /// <summary>120 BPM ⇒ 500 ms per beat; 4 beats ⇒ a 2 s bar.</summary>
    private const double Bpm = 120;

    private static (SoundboardWorkspaceViewModel Vm, SoundboardViewModel Board, List<Guid> Played) CreateWorkspace(
        double quantizeBeats, TimeSpan start = default)
    {
        var vm = new SoundboardWorkspaceViewModel();
        var board = vm.Boards[0];
        board.Bpm = Bpm;
        board.LaunchQuantizeBeats = quantizeBeats;
        var played = new List<Guid>();
        vm.PlaySoundCallback = r =>
        {
            played.Add(r.TileId);
            return Task.FromResult<string?>(null);
        };
        vm.TransportNow = () => start;
        return (vm, board, played);
    }

    private static SoundboardTileViewModel BindTile(SoundboardViewModel board, int index = 0)
    {
        var tile = board.Tiles[index];
        board.BindTile(tile, $"/tmp/stinger{index}.wav");
        return tile;
    }

    private static void SetNow(SoundboardWorkspaceViewModel vm, TimeSpan now) => vm.TransportNow = () => now;

    // ---- the pure boundary math (shared with the framework grid primitive) ----

    [Fact]
    public void NextBoundary_RoundsUpAndLeavesExactBoundariesAlone()
    {
        var quantum = TimeSpan.FromSeconds(2);

        Assert.Equal(TimeSpan.FromSeconds(2), SoundboardQuantization.NextBoundary(TimeSpan.FromSeconds(0.1), quantum));
        Assert.Equal(TimeSpan.FromSeconds(4), SoundboardQuantization.NextBoundary(TimeSpan.FromSeconds(3.9), quantum));
        // Already on a boundary: returned as-is, not pushed a whole quantum out.
        Assert.Equal(TimeSpan.FromSeconds(4), SoundboardQuantization.NextBoundary(TimeSpan.FromSeconds(4), quantum));
        // Quantization off.
        Assert.Equal(TimeSpan.FromSeconds(3.3), SoundboardQuantization.NextBoundary(TimeSpan.FromSeconds(3.3), TimeSpan.Zero));
    }

    [Fact]
    public void BeatsToQuantum_ConvertsTempo_AndTreatsNonPositiveAsOff()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.5), SoundboardQuantization.BeatsToQuantum(120, 1));
        Assert.Equal(TimeSpan.FromSeconds(2), SoundboardQuantization.BeatsToQuantum(120, 4));
        Assert.Equal(TimeSpan.FromSeconds(1), SoundboardQuantization.BeatsToQuantum(240, 4));
        Assert.Equal(TimeSpan.Zero, SoundboardQuantization.BeatsToQuantum(120, 0));
        Assert.Equal(TimeSpan.Zero, SoundboardQuantization.BeatsToQuantum(0, 4));
    }

    // ---- arming ----

    [Fact]
    public async Task QuantizeOff_PlaysImmediately_LikeBefore()
    {
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 0);
        var tile = BindTile(board);

        await vm.TapTileAsync(tile);

        Assert.Equal([tile.Id], played);
        Assert.False(tile.IsPendingLaunch);
        Assert.Equal(0, vm.PendingLaunchCount);
    }

    [Fact]
    public async Task Tap_WithQuantize_ArmsInsteadOfPlaying_AndFiresOnTheBoundary()
    {
        // Bar grid (2 s). Tap at 0.4 s ⇒ due at 2 s.
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 4, start: TimeSpan.FromSeconds(0.4));
        var tile = BindTile(board);

        await vm.TapTileAsync(tile);

        Assert.Empty(played);
        Assert.True(tile.IsPendingLaunch);
        Assert.Equal(1600, tile.PendingLaunchRemainingMs);

        // Before the boundary: still armed, countdown refreshed.
        SetNow(vm, TimeSpan.FromSeconds(1.5));
        await vm.PumpPendingLaunchesAsync();
        Assert.Empty(played);
        Assert.True(tile.IsPendingLaunch);
        Assert.Equal(500, tile.PendingLaunchRemainingMs);

        // On the boundary: fires exactly once and disarms.
        SetNow(vm, TimeSpan.FromSeconds(2));
        await vm.PumpPendingLaunchesAsync();
        Assert.Equal([tile.Id], played);
        Assert.False(tile.IsPendingLaunch);
        Assert.Equal(0, vm.PendingLaunchCount);

        // Later pumps must not re-fire it.
        SetNow(vm, TimeSpan.FromSeconds(4));
        await vm.PumpPendingLaunchesAsync();
        Assert.Single(played);
    }

    [Fact]
    public async Task TapExactlyOnABoundary_WaitsForTheNextOne()
    {
        // Landing dead on the grid must not fire in the same pass - that would read as unquantized.
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 4, start: TimeSpan.FromSeconds(2));
        var tile = BindTile(board);

        await vm.TapTileAsync(tile);
        Assert.True(tile.IsPendingLaunch);
        Assert.Equal(2000, tile.PendingLaunchRemainingMs);

        SetNow(vm, TimeSpan.FromSeconds(4));
        await vm.PumpPendingLaunchesAsync();
        Assert.Equal([tile.Id], played);
    }

    [Fact]
    public async Task SecondTap_DisarmsWithoutPlaying()
    {
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 4, start: TimeSpan.FromSeconds(0.4));
        var tile = BindTile(board);

        await vm.TapTileAsync(tile);
        Assert.True(tile.IsPendingLaunch);

        await vm.TapTileAsync(tile); // cancel
        Assert.False(tile.IsPendingLaunch);
        Assert.Equal(0, vm.PendingLaunchCount);

        SetNow(vm, TimeSpan.FromSeconds(4));
        await vm.PumpPendingLaunchesAsync();
        Assert.Empty(played); // never sounded
    }

    [Fact]
    public async Task ChordOfTaps_FiresTogetherInTapOrder()
    {
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 4, start: TimeSpan.FromSeconds(0.2));
        var first = BindTile(board, 0);
        var second = BindTile(board, 1);

        await vm.TapTileAsync(first);
        SetNow(vm, TimeSpan.FromSeconds(0.6)); // still the same bar
        await vm.TapTileAsync(second);
        Assert.Equal(2, vm.PendingLaunchCount);

        SetNow(vm, TimeSpan.FromSeconds(2));
        await vm.PumpPendingLaunchesAsync();

        Assert.Equal([first.Id, second.Id], played);
        Assert.Equal(0, vm.PendingLaunchCount);
    }

    [Fact]
    public async Task StopAll_DropsArmedLaunches_SoNothingFiresAfterwards()
    {
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 4, start: TimeSpan.FromSeconds(0.4));
        var tile = BindTile(board);
        var stopAllCalled = false;
        vm.StopAllSoundsCallback = () => { stopAllCalled = true; return Task.CompletedTask; };

        await vm.TapTileAsync(tile);
        Assert.True(tile.IsPendingLaunch);

        await vm.StopAllCommand.ExecuteAsync(null);

        Assert.True(stopAllCalled);
        Assert.False(tile.IsPendingLaunch);
        SetNow(vm, TimeSpan.FromSeconds(4));
        await vm.PumpPendingLaunchesAsync();
        Assert.Empty(played);
    }

    [Fact]
    public async Task PlayingTile_TappedWhileQuantized_StopsImmediately_NotOnTheGrid()
    {
        // Quantization is a LAUNCH grid; stopping stays immediate (an operator killing a stinger
        // must not wait a bar).
        var (vm, board, played) = CreateWorkspace(quantizeBeats: 4, start: TimeSpan.FromSeconds(0.4));
        var tile = BindTile(board);
        var stopped = new List<Guid>();
        vm.StopSoundCallback = id => { stopped.Add(id); return Task.CompletedTask; };

        tile.IsPlaying = true;
        await vm.TapTileAsync(tile);

        Assert.Equal([tile.Id], stopped);
        Assert.False(tile.IsPendingLaunch);
        Assert.Empty(played);
    }

    // ---- persistence ----

    [Fact]
    public void TempoAndQuantize_RoundTripThroughConfig_AndOldBoardsLoadUnquantized()
    {
        var vm = new SoundboardWorkspaceViewModel();
        var board = vm.Boards[0];
        board.Bpm = 128;
        board.LaunchQuantizeBeats = 1;

        var restored = SoundboardViewModel.FromConfig(board.ToConfig());
        Assert.Equal(128, restored.Bpm);
        Assert.Equal(1, restored.LaunchQuantizeBeats);
        Assert.Equal(TimeSpan.FromSeconds(60d / 128), restored.LaunchQuantum);

        // A board saved before the feature (no fields written) loads with quantization off and a
        // usable default tempo - i.e. exactly the old immediate-launch behavior.
        var legacy = SoundboardViewModel.FromConfig(new SoundboardConfig { Name = "old" });
        Assert.Equal(0, legacy.LaunchQuantizeBeats);
        Assert.Equal(TimeSpan.Zero, legacy.LaunchQuantum);
        Assert.Equal(120, legacy.Bpm);
    }
}
