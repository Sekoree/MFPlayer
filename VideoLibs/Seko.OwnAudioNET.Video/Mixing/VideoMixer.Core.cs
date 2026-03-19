using System.Collections.Concurrent;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Events;
using Seko.OwnAudioNET.Video.Sources;

namespace Seko.OwnAudioNET.Video.Mixing;

/// <summary>
/// FFmpeg-centric video mixer that combines shared-clock source playback with explicit output routing.
/// </summary>
public sealed partial class VideoMixer : IVideoMixer
{
    private readonly IVideoTransportEngine _engine;
    private readonly bool _ownsEngine;
    private readonly ConcurrentDictionary<Guid, FFVideoSource> _sources = new();
    private readonly ConcurrentDictionary<Guid, IVideoOutput> _outputs = new();
    private readonly Lock _syncLock = new();
    private readonly Dictionary<Guid, Guid> _outputSourceBindings = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _sourceOutputBindings = new();
    private bool _disposed;


    public VideoMixer(IVideoTransportEngine engine, bool ownsEngine = false)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
        _engine.SourceError += OnEngineSourceError;
    }

    public VideoTransportEngineConfig Config => _engine.Config;

    public IVideoClock Clock => _engine.Clock;

    public double Position => _engine.Position;

    public bool IsRunning => _engine.IsRunning;

    public int SourceCount => _sources.Count;

    public int OutputCount => _outputs.Count;

    public event EventHandler<VideoErrorEventArgs>? SourceError;

    public event EventHandler<VideoOutputSourceChangedEventArgs>? OutputSourceChanged;

    private void OnEngineSourceError(object? sender, VideoErrorEventArgs e)
    {
        SourceError?.Invoke(sender, e);
    }
}

