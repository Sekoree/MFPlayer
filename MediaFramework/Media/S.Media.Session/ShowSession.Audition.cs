using S.Media.Core.Video;

namespace S.Media.Session;

/// <summary>The audition rig's video half: a hidden composition a previewed cue is placed onto.</summary>
/// <param name="Width">Canvas width. Match the show's compositions to see placement/fit as it will really land.</param>
/// <param name="Height">Canvas height.</param>
/// <param name="FrameRateNum">Canvas rate numerator.</param>
/// <param name="FrameRateDen">Canvas rate denominator.</param>
/// <param name="Mapping">Optional output mapping, so an audition surface can carry the same keystone/mesh
/// the real projector does - the point of auditioning through a composition rather than straight to a sink.</param>
public sealed record AuditionCompositionSpec(
    int Width = 1920,
    int Height = 1080,
    int FrameRateNum = 60,
    int FrameRateDen = 1,
    ClipOutputMappingSpec? Mapping = null);

public sealed partial class ShowSession
{
    /// <summary>The id the audition canvas answers to. Deliberately not a legal document composition id
    /// shape, so a show can never author a composition that collides with the rig.</summary>
    public const string AuditionCompositionId = "_audition";

    /// <summary>
    /// The audition canvas, or null when the rig is off.
    /// </summary>
    /// <remarks>
    /// Held OUTSIDE <c>_compositions</c> on purpose: that dictionary is rebuilt from the document on every
    /// load, and the audition rig is part of the operator's setup rather than part of the show. Living in
    /// there would mean the monitor went dark every time a show was reloaded, and would put a composition
    /// in <c>GetCompositions()</c> that no document declares.
    /// </remarks>
    private ClipCompositionRuntime? _auditionComposition;

    /// <summary>The live audition canvas for <see cref="VoicePlayer"/> to place a preview onto.</summary>
    internal ClipCompositionRuntime? AuditionComposition => _auditionComposition;

    /// <summary>Whether the audition canvas is currently running.</summary>
    public bool IsAuditionCompositionEnabled => _auditionComposition is not null;

    /// <summary>
    /// Brings up the audition canvas: a composition with its own pump and GL context that a previewed cue's
    /// video is placed onto, so the monitor shows placement, fit, effects and mapping exactly as the real
    /// output will.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This costs a driver thread and a GL context for as long as it is enabled, which is why it is opt-in
    /// and tearable rather than always present. The cheaper alternative - attaching the preview player's
    /// video straight to a sink at source resolution - was rejected deliberately: it shows the media, but
    /// it cannot show what the media will LOOK like in the show, which is the question auditioning asks.
    /// </para>
    /// <para>
    /// Calling this while already enabled reconfigures: an unchanged spec is a no-op (the canvas keeps
    /// running and any attached surface keeps its frames), a changed one rebuilds. Attached outputs do not
    /// survive a rebuild - the caller re-attaches, exactly as it would after enabling for the first time.
    /// </para>
    /// </remarks>
    public Task EnableAuditionCompositionAsync(AuditionCompositionSpec? spec = null) =>
        InvokeAsync(() =>
        {
            var wanted = spec ?? new AuditionCompositionSpec();
            if (wanted.Width <= 0 || wanted.Height <= 0)
                throw new ArgumentException(
                    $"audition canvas dimensions must be positive (got {wanted.Width}x{wanted.Height}).", nameof(spec));

            if (_auditionComposition is not null && _auditionSpec == wanted)
                return Task.CompletedTask;

            _auditionComposition?.Dispose();
            _auditionComposition = new ClipCompositionRuntime(
                new ClipCompositionDefinition(
                    AuditionCompositionId, "Audition",
                    wanted.Width, wanted.Height, wanted.FrameRateNum, wanted.FrameRateDen),
                // A session-owned discarding line keeps the pump composing before (and after) a host attaches
                // a real surface - the same headless-keepalive the document compositions get.
                [new ClipCompositionOutputLease(
                    $"{AuditionCompositionId}_null", "Audition", new DiscardingVideoOutput(),
                    DisposeOutputOnRuntimeDispose: true)],
                compositorFactory: _compositorFactory,
                compositionMapping: wanted.Mapping,
                effectRegistry: _effectRegistry);
            _auditionSpec = wanted;
            return Task.CompletedTask;
        });

    private AuditionCompositionSpec? _auditionSpec;

    /// <summary>Tears the audition canvas down and gives its driver thread back. A preview already placed
    /// on it keeps playing - it simply stops being monitored, which is what "turn the monitor off" means.</summary>
    public Task DisableAuditionCompositionAsync() =>
        InvokeAsync(() =>
        {
            var composition = _auditionComposition;
            _auditionComposition = null;
            _auditionSpec = null;
            composition?.Dispose();
            return Task.CompletedTask;
        });

    /// <summary>Attaches a live surface (a UI preview control, an NDI sender) to the audition canvas.
    /// Returns false when the rig is not enabled. The caller owns the output's lifetime.</summary>
    public Task<bool> AttachAuditionOutputAsync(IVideoOutput output, string outputId = "audition")
    {
        ArgumentNullException.ThrowIfNull(output);
        return InvokeAsync(() => Task.FromResult(
            _auditionComposition?.AddOutput(new ClipCompositionOutputLease(outputId, outputId, output)) ?? false));
    }

    /// <summary>Detaches a surface from the audition canvas. False when the rig is off or had no such output.</summary>
    public Task<bool> DetachAuditionOutputAsync(string outputId = "audition") =>
        InvokeAsync(() => Task.FromResult(_auditionComposition?.RemoveOutput(outputId) ?? false));

    /// <summary>The audition canvas's pump stats, or null when the rig is off - how a host (or a test)
    /// confirms a previewed cue is actually reaching the monitor.</summary>
    public Task<ClipCompositionRuntimeStats?> GetAuditionCompositionStatsAsync() =>
        InvokeAsync(() => Task.FromResult(_auditionComposition?.GetStats()));
}
