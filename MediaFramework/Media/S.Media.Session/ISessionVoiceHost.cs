namespace S.Media.Session;

/// <summary>
/// The narrow slice of a session that independent-player surfaces (soundboard voices, cue preview) need.
/// </summary>
/// <remarks>
/// <para>
/// This exists to state, in a form the compiler enforces, what those surfaces actually depend on: a serial
/// dispatcher, the level/stop bus, the completion tick, a master trim and a disposal flag. Nothing about
/// cues, documents, transport groups or the cue graph appears here - which is the point. A soundboard is
/// neutral one-shot playback, and while it took a whole <c>ShowSession</c> reference it read as though it
/// were part of the cue engine, so any app adopting the engine inherited soundboard responsibilities it
/// never asked for.
/// </para>
/// <para>
/// Deliberately an interface over the existing session rather than a new object: the dispatcher, bus and
/// completion monitor are one instance per session and must stay that way - two dispatchers would break
/// the serial-confinement invariant everything here relies on.
/// </para>
/// </remarks>
internal interface ISessionVoiceHost
{
    /// <summary>Marshals onto the session's serial dispatcher.</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> func);

    /// <summary>Marshals onto the session's serial dispatcher.</summary>
    Task InvokeAsync(Func<Task> func);

    /// <summary>The one level/stop bus. Voices register as program sources; a preview registers as
    /// monitoring, so a panic stop reaches the right things and the fader reaches only program.</summary>
    SoundingSourceRegistry SoundingSources { get; }

    /// <summary>Wakes the completion monitor when there is something new that can finish on its own.</summary>
    void NotifyCompletionWorkAvailable();

    /// <summary>Whether the session has been disposed - a commit that straddles teardown must abandon.</summary>
    bool IsDisposed { get; }

    /// <summary>The session-wide master trim a program voice inherits at fire time.</summary>
    float MasterTrim { get; }
}

/// <summary>The extra thing a cue preview needs and a soundboard voice does not: somewhere to show video.</summary>
/// <remarks>Split from <see cref="ISessionVoiceHost"/> rather than folded into it so the soundboard half
/// keeps its "no composition, no canvas" property visible in its own constructor signature.</remarks>
internal interface ISessionPreviewHost : ISessionVoiceHost
{
    /// <summary>The audition canvas a previewed clip is placed onto, or null when the rig is off.</summary>
    ClipCompositionRuntime? AuditionComposition { get; }
}
