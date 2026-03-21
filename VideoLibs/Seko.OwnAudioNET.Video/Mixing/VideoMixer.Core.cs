using System.Collections.Concurrent;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// FFmpeg-centric video mixer that combines shared-clock source playback with an attached render engine.
/// </summary>
public sealed partial class VideoMixer : IVideoMixer
{
    private readonly VideoPlaybackEngine _transport;
    private readonly IVideoEngine _engine;
    private readonly IExternalClock? _externalClock;
    private readonly ConcurrentDictionary<Guid, VideoStreamSource> _sources = new();
    private readonly Lock _syncLock = new();
    private readonly Action<VideoFrame, double> _activeSourceFrameHandler;
    private Guid? _activeSourceId;
    private bool _disposed;


    public VideoMixer(IVideoEngine engine)
        : this(engine, videoClock: null, config: null, externalClock: null)
    {
    }


    public VideoMixer(IVideoEngine engine, IVideoClock? videoClock, VideoEngineConfig? config = null, IExternalClock? externalClock = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _transport = new VideoPlaybackEngine(videoClock, config, ownsClock: false);
        _externalClock = externalClock;
        _activeSourceFrameHandler = OnActiveSourceFrameReady;
        _transport.SourceError += OnEngineSourceError;
    }

    public VideoEngineConfig Config => _transport.Config;

    public IVideoClock Clock => _transport.Clock;

    public double Position => _externalClock?.CurrentSeconds ?? _transport.Position;

    public IExternalClock? ExternalClock => _externalClock;

    public bool IsRunning => _transport.IsRunning;

    public int SourceCount => _sources.Count;

    public VideoStreamSource? ActiveSource
    {
        get
        {
            lock (_syncLock)
            {
                if (!_activeSourceId.HasValue)
                    return null;

                return _sources.TryGetValue(_activeSourceId.Value, out var source) ? source : null;
            }
        }
    }


    public event EventHandler<VideoErrorEventArgs>? SourceError;

    public event EventHandler<VideoActiveSourceChangedEventArgs>? ActiveSourceChanged;

    private void OnEngineSourceError(object? sender, VideoErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }

    private void OnActiveSourceFrameReady(VideoFrame frame, double masterTimestamp)
    {
        try
        {
            _engine.PushFrame(frame, masterTimestamp);
        }
        catch
        {
            // Best effort push path.
        }
    }
}

