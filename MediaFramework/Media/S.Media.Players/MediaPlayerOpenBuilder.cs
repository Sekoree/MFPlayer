using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.Players;

/// <summary>
/// Registry-driven open builder for one media object (P2 - opens through <see cref="IMediaRegistry"/>, no
/// globals). The registry is injected at builder creation, so the cue/session layer keeps its
/// <c>OpenFile(...).WithOptions(...).Build()</c> ergonomics without a concrete decoder dependency.
/// </summary>
public abstract class MediaPlayerOpenBuilder
{
    private protected MediaPlayerOpenOptions OpenOptions = MediaPlayerOpenOptions.Default;
    private protected IVideoOutput? VideoLead;
    private protected bool DisposeVideoLeadOnPlayerDispose;

    /// <summary>The options currently configured on this builder.</summary>
    public MediaPlayerOpenOptions CurrentOptions => OpenOptions;

    /// <summary>Token that aborts a slow/blocked media open mid-flight (NXT-03). Honoured by the registry-driven
    /// file/URI builder and, on the live-source builder, by the bounded live first-frame wait (NXT-26).
    /// Default: none.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Builds the player; returns <see langword="false"/> instead of throwing on open/wiring failure.</summary>
    public abstract bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error);

    /// <summary>Builds the player or throws <see cref="InvalidOperationException"/> with the open failure.</summary>
    public MediaPlayer Build() =>
        TryBuild(out var player, out var error)
            ? player
            : throw new InvalidOperationException(error ?? "MediaPlayer open failed.");
}

/// <summary>Opens a file path or URI through the registry (the provider selects on the URI scheme - D2).</summary>
public sealed class MediaPlayerOpenFileBuilder : MediaPlayerOpenBuilder
{
    private readonly IMediaRegistry _registry;

    internal MediaPlayerOpenFileBuilder(IMediaRegistry registry, string filePathOrUri)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Uri = filePathOrUri ?? throw new ArgumentNullException(nameof(filePathOrUri));
    }

    /// <summary>The file path or URI being opened.</summary>
    public string Uri { get; }

    public MediaPlayerOpenFileBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        OpenOptions = options;
        return this;
    }

    public MediaPlayerOpenFileBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        OpenOptions = configure(OpenOptions);
        return this;
    }

    /// <summary>Uses <paramref name="output"/> as the video negotiation lead (local display/preview output).</summary>
    public MediaPlayerOpenFileBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        VideoLead = output ?? throw new ArgumentNullException(nameof(output));
        DisposeVideoLeadOnPlayerDispose = disposeOnPlayerDispose;
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error) =>
        MediaPlayer.TryOpen(_registry, Uri, OpenOptions, VideoLead, out player, out error, Cancellation);
}

/// <summary>
/// Builds a player from already-opened live/source objects (NDI/capture, or a host-owned container decoder's
/// <see cref="IAudioSource"/>/<see cref="IVideoSource"/>). The registry-free counterpart to
/// <see cref="MediaPlayerOpenFileBuilder"/>, for callers that manage their own decoders (e.g. a track-switch
/// cache). The video source provided here is the negotiated/processed source - there is no separate override.
/// </summary>
public sealed class MediaPlayerOpenLiveBuilder : MediaPlayerOpenBuilder
{
    private readonly IAudioSource? _audio;
    private readonly IVideoSource? _video;
    private bool _disposeSourcesOnPlayerDispose;
    private IMediaRegistry? _registry;

    internal MediaPlayerOpenLiveBuilder(IAudioSource? audio, IVideoSource? video)
    {
        _audio = audio;
        _video = video;
    }

    /// <summary>
    /// Supplies the registry whose CPU converter the router may use for fan-out branches.
    /// </summary>
    /// <remarks>
    /// Optional, because this builder exists for callers that own their decoders and may have no
    /// registry at all. Without it a branch that needs a pixel conversion is refused — the same
    /// behaviour this path has always had, now stated rather than silent.
    /// </remarks>
    public MediaPlayerOpenLiveBuilder WithRegistry(IMediaRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        return this;
    }

    public MediaPlayerOpenLiveBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        OpenOptions = options;
        return this;
    }

    public MediaPlayerOpenLiveBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        OpenOptions = configure(OpenOptions);
        return this;
    }

    /// <summary>Uses <paramref name="output"/> as the video negotiation lead (local display/preview output).</summary>
    public MediaPlayerOpenLiveBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        VideoLead = output ?? throw new ArgumentNullException(nameof(output));
        DisposeVideoLeadOnPlayerDispose = disposeOnPlayerDispose;
        return this;
    }

    /// <summary>When true, the player disposes the provided sources on its own dispose; when false the caller
    /// retains ownership (e.g. a shared decoder cache).</summary>
    public MediaPlayerOpenLiveBuilder WithDisposeSourcesOnPlayerDispose(bool dispose)
    {
        _disposeSourcesOnPlayerDispose = dispose;
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error) =>
        MediaPlayer.TryOpenLive(_audio, _video, OpenOptions, VideoLead, DisposeVideoLeadOnPlayerDispose,
            _disposeSourcesOnPlayerDispose, resamplerFactory: null,
            // With the same conversion hooks the file path gets, when a registry was supplied: an NDI
            // receiver fanned out to a composition needs them for the same reason a decoded file does.
            routerOptions: _registry is null ? null : MediaPlayer.VideoRouterOptionsFor(_registry),
            out player, out error, Cancellation);
}
