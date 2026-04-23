namespace S.Media.Core.Video;

/// <summary>
/// Optional capability on a video endpoint: "I can consume a YUV color-matrix hint
/// supplied by the source channel". When both the source channel implements
/// <see cref="IVideoColorMatrixHint"/> <i>and</i> the endpoint implements this
/// interface, <c>AVRouter.CreateVideoRoute</c> calls
/// <see cref="ApplyColorMatrixHint"/> once at route-creation time so the endpoint
/// can pick a matching shader path without the host application having to pump
/// the hint by hand.
///
/// <para>
/// Implementers are expected to treat an incoming <see cref="YuvColorMatrix.Auto"/>
/// / <see cref="YuvColorRange.Auto"/> as "no change" (they already have a sensible
/// default and the source simply doesn't know), and to preserve any
/// <i>explicit</i> value a caller previously set via a concrete property — the
/// hint is advisory, not authoritative. The router only fires the callback once
/// on route creation; late-arriving hints (e.g. NDI sources that learn the
/// matrix from the first frame) re-fire via the channel's existing re-push path.
/// </para>
///
/// <para>Closes review item §5.3.</para>
/// </summary>
public interface IVideoColorMatrixReceiver
{
    /// <summary>
    /// Apply a color-matrix hint from the connected source. Called on the
    /// thread that created the route — implementations must marshal onto their
    /// own render thread if they touch GL state.
    /// </summary>
    void ApplyColorMatrixHint(YuvColorMatrix matrix, YuvColorRange range);
}

