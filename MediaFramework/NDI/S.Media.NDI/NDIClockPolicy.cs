namespace S.Media.NDI;

/// <summary>
/// §4.16 / N4 — policy deciding which NDI capture channel writes timestamps
/// into the shared <see cref="NDIClock"/>. A mixed source graph (video +
/// audio) otherwise has both channels fighting for clock authority, which
/// causes sub-frame jitter when the two streams carry independently-rounded
/// timestamps.
/// </summary>
public enum NDIClockPolicy
{
    /// <summary>
    /// Legacy behaviour — both audio and video capture loops call
    /// <c>UpdateFromFrame</c>. Kept as the default so existing callers don't
    /// change behaviour; prefer <see cref="VideoPreferred"/> or
    /// <see cref="AudioPreferred"/> for new code.
    /// </summary>
    Both = 0,

    /// <summary>
    /// Video leads: only the video channel writes to the clock. Audio-only
    /// NDI sources (PTZ / metadata) transparently fall back to the audio
    /// writer because there is no video channel to claim the lead.
    /// </summary>
    VideoPreferred,

    /// <summary>
    /// Audio leads: only the audio channel writes to the clock. Video-only
    /// NDI sources transparently fall back to the video writer.
    /// </summary>
    AudioPreferred,

    /// <summary>
    /// Whichever channel fires <c>UpdateFromFrame</c> first wins — the other
    /// becomes a silent reader. Useful when neither audio nor video is
    /// reliably faster to arrive (e.g. receiver-side jitter buffers mask
    /// the source ordering). First-write wins are resolved via an atomic
    /// CAS on <see cref="NDIClock"/>, so races resolve deterministically.
    /// </summary>
    FirstWriter,
}