using BenchmarkDotNet.Attributes;
using S.Media.Routing;

namespace S.Media.Audio.Benchmarks;

/// <summary>
/// The HaCue extraction plan's Phase 0 measurement (Plans/HaCue-Extraction-And-Project-Audio-Patch-Plan.md,
/// "Performance budgets"): the fused kernel's published 8×8 datapoint says nothing about whether it
/// scales linearly in cells to the 64-wide matrices the project audio patch allows, so the plan's
/// cell-op budgets must come from these numbers, not from extrapolation. This class sweeps the dense
/// S×S settled pass at the widths the design allows - linear-in-cells means Width=64 lands at ~64×
/// the Width=8 time; a super-linear knee (SIMD efficiency loss, cache pressure) is exactly what
/// Phase 0 exists to find before the budgets are frozen.
/// </summary>
[MemoryDiagnoser]
public class WideMatrixBenchmarks
{
    private const int SamplesPerChannel = 480; // one 10 ms chunk at 48 kHz

    [Params(8, 16, 32, 64)]
    public int Width;

    private float[] _gains = null!;
    private float[] _src = null!;
    private float[] _dst = null!;

    [GlobalSetup]
    public void Setup()
    {
        _gains = new float[Width * Width];
        _src = new float[SamplesPerChannel * Width];
        _dst = new float[SamplesPerChannel * Width];
        for (var i = 0; i < _src.Length; i++)
            _src[i] = MathF.Sin(i * 0.01f) * 0.25f;
        for (var i = 0; i < _gains.Length; i++)
            _gains[i] = 0.11f;
    }

    [Benchmark]
    public void DenseSquare()
    {
        Array.Clear(_dst);
        AudioRouter.ApplyFusedMatrixSettled(_src, Width, _dst, Width, _gains, SamplesPerChannel);
    }
}

/// <summary>
/// One whole audio chunk of the plan's program-sum topology at its stated maximums: 8 voices each
/// sending N=2 source channels into a V=64-wide logical program bus, then one dense 64×64 pass per
/// each of 8 terminals - the shape behind the plan's "≈1.6 ms per 10 ms chunk" claim. The bus is
/// per-chunk scratch, exactly as the design requires (no queue). A per-pair (P×R) comparison is
/// deliberately absent: the plan already rejects that topology arithmetically (~5× over deadline);
/// measuring it would only characterize something nobody is building.
/// </summary>
[MemoryDiagnoser]
public class ProgramSumTopologyBenchmarks
{
    private const int SamplesPerChannel = 480;
    private const int Voices = 8;
    private const int SourceChannels = 2;
    private const int LogicalChannels = 64;
    private const int Terminals = 8;
    private const int TerminalChannels = 64;

    private float[] _sendGains = null!;   // N×V voice send
    private float[] _voiceSrc = null!;
    private float[] _bus = null!;         // V-wide program bus (per-chunk scratch in the design)
    private float[] _patchGains = null!;  // V×R terminal pass
    private float[] _terminalDst = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sendGains = new float[SourceChannels * LogicalChannels];
        _voiceSrc = new float[SamplesPerChannel * SourceChannels];
        _bus = new float[SamplesPerChannel * LogicalChannels];
        _patchGains = new float[LogicalChannels * TerminalChannels];
        _terminalDst = new float[SamplesPerChannel * TerminalChannels];
        for (var i = 0; i < _voiceSrc.Length; i++)
            _voiceSrc[i] = MathF.Sin(i * 0.01f) * 0.25f;
        for (var i = 0; i < _sendGains.Length; i++)
            _sendGains[i] = 0.11f;
        for (var i = 0; i < _patchGains.Length; i++)
            _patchGains[i] = 0.11f;
    }

    [Benchmark]
    public void ProgramSumChunkAtMaximums()
    {
        Array.Clear(_bus);
        for (var v = 0; v < Voices; v++)
            AudioRouter.ApplyFusedMatrixSettled(_voiceSrc, SourceChannels, _bus, LogicalChannels, _sendGains, SamplesPerChannel);

        for (var t = 0; t < Terminals; t++)
        {
            Array.Clear(_terminalDst);
            AudioRouter.ApplyFusedMatrixSettled(_bus, LogicalChannels, _terminalDst, TerminalChannels, _patchGains, SamplesPerChannel);
        }
    }
}
