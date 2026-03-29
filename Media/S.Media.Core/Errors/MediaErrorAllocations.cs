using System.Collections.ObjectModel;

namespace S.Media.Core.Errors;

/// <summary>
/// Allocation ranges for the error-code numeric space.
/// Use <see cref="MediaErrorCode"/> enum values directly (cast to <c>int</c>) instead of the
/// removed per-code shortcut properties that previously lived here.
/// </summary>
public static class MediaErrorAllocations
{
    // ── Top-level bands ───────────────────────────────────────────────────────

    public static ErrorCodeAllocationRange GenericCommon { get; } = new(0, 999, nameof(GenericCommon));
    public static ErrorCodeAllocationRange Playback      { get; } = new(1000, 1999, nameof(Playback));
    public static ErrorCodeAllocationRange Decoding      { get; } = new(2000, 2999, nameof(Decoding));
    public static ErrorCodeAllocationRange Mixing        { get; } = new(3000, 3999, nameof(Mixing));
    public static ErrorCodeAllocationRange OutputRender  { get; } = new(4000, 4999, nameof(OutputRender));
    public static ErrorCodeAllocationRange NDI           { get; } = new(5000, 5199, nameof(NDI));

    // ── Sub-ranges ────────────────────────────────────────────────────────────

    public static ErrorCodeAllocationRange FFmpegActive          { get; } = new(2000, 2099, nameof(FFmpegActive));
    public static ErrorCodeAllocationRange FFmpegRuntimeReserve  { get; } = new(2100, 2199, nameof(FFmpegRuntimeReserve));
    public static ErrorCodeAllocationRange FFmpegMappingReserve  { get; } = new(2200, 2299, nameof(FFmpegMappingReserve));
    public static ErrorCodeAllocationRange MixingActive          { get; } = new(3000, 3099, nameof(MixingActive));
    public static ErrorCodeAllocationRange OutputBackpressureActive { get; } = new(4000, 4099, nameof(OutputBackpressureActive));
    public static ErrorCodeAllocationRange PortAudioActive       { get; } = new(4300, 4399, nameof(PortAudioActive));
    public static ErrorCodeAllocationRange OpenGLActive          { get; } = new(4400, 4499, nameof(OpenGLActive));
    public static ErrorCodeAllocationRange NDIActiveNearTerm     { get; } = new(5000, 5079, nameof(NDIActiveNearTerm));
    public static ErrorCodeAllocationRange NDIFutureReserve      { get; } = new(5080, 5199, nameof(NDIFutureReserve));
    public static ErrorCodeAllocationRange MIDIReserve           { get; } = new(900, 949, nameof(MIDIReserve));

    // ── Aggregate ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<ErrorCodeAllocationRange> All { get; } =
        new ReadOnlyCollection<ErrorCodeAllocationRange>(
        [
            GenericCommon,
            Playback,
            Decoding,
            Mixing,
            OutputRender,
            NDI,
            FFmpegActive,
            FFmpegRuntimeReserve,
            FFmpegMappingReserve,
            MixingActive,
            OutputBackpressureActive,
            PortAudioActive,
            OpenGLActive,
            NDIActiveNearTerm,
            NDIFutureReserve,
            MIDIReserve,
        ]);
}
