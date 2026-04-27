namespace S.Media.Core.Clock;

/// <summary>
/// Optional marker for a router master <see cref="IMediaClock"/> whose
/// <see cref="IMediaClock.Position"/> does <b>not</b> correspond to the decoder PTS
/// timeline — i.e. it represents wall-time, output-side time (DAC/SDI/NDI sender
/// timecode) or any other reference that the decoder-side
/// <see cref="S.Media.Core.Routing.IAVRouter.GetAvDrift"/> formula cannot meaningfully
/// compare against.
///
/// <para>
/// Concrete examples:
/// </para>
/// <list type="bullet">
///   <item><c>NDIClock</c> — fed from the NDI sender timecode, not the decoder.</item>
///   <item><see cref="HardwareClock"/> (e.g. <c>PortAudioClock</c>) — represents
///         <c>Pa_GetStreamTime</c>/DAC output time, offset from decoder PTS by the
///         entire audio output pipeline depth.</item>
///   <item><see cref="StopwatchClock"/> — pure wall time, not anchored to any
///         decoder-side timeline.</item>
/// </list>
///
/// <para>
/// When the active router master implements this marker the playback layer uses
/// <see cref="S.Media.Core.Routing.IAVRouter.GetAvStreamHeadDrift"/> for auto
/// input-offset nudges. That function baselines the first measurement and tracks
/// only relative change, so it is robust against pipeline-depth offsets and
/// startup settling transients that would otherwise show up as huge "phantom"
/// drift in <see cref="S.Media.Core.Routing.IAVRouter.GetAvDrift"/>. The router's
/// sender-clock bypass still folds the resulting video input offset into wall-time
/// delivery.
/// </para>
/// </summary>
public interface ISuppressesAutoAvDriftCorrection
{
}
