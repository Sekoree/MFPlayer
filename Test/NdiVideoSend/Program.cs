using NdiLib;
using Ownaudio.Core;
using Ownaudio.Native;
using OwnaudioNET.Mixing;
using SDL3;
using Seko.OwnAudioNET.Video.Clocks;
using Seko.OwnAudioNET.Video.Engine;
using Seko.OwnAudioNET.Video.Mixing;
using Seko.OwnAudioNET.Video.NDI;
using Seko.OwnAudioNET.Video.SDL3;
using Seko.OwnAudioNET.Video.Sources;

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  NdiVideoSend [discover-timeout-seconds] [stable|balanced|lowlatency]");
    Console.WriteLine();
    Console.WriteLine("Receives first discovered NDI source and plays it through AudioVideoMixer + SDL3.");
    Console.WriteLine("Tuning profile defaults to 'balanced'.");
    return;
}

var discoverTimeoutSeconds = 10;
var tuningProfile = NdiReceiveTuningProfile.Balanced;
var unknownArgs = new List<string>();
foreach (var arg in args)
{
    if (int.TryParse(arg, out var parsedTimeout) && parsedTimeout > 0)
    {
        discoverTimeoutSeconds = parsedTimeout;
        continue;
    }

    if (Enum.TryParse<NdiReceiveTuningProfile>(arg, ignoreCase: true, out var parsedProfile))
    {
        tuningProfile = parsedProfile;
        continue;
    }

    unknownArgs.Add(arg);
}

if (unknownArgs.Count > 0)
{
    Console.WriteLine($"Warning: Ignoring unknown arguments: {string.Join(", ", unknownArgs)}");
    Console.WriteLine("Accepted profile values: stable, balanced, lowlatency");
}

using var runtime = new NdiRuntimeScope();
Console.WriteLine($"NDI runtime version: {NdiRuntime.Version}");

using var finder = new NdiFinder();
NdiDiscoveredSource? selected = null;
var discoverEnd = DateTime.UtcNow.AddSeconds(discoverTimeoutSeconds);

while (DateTime.UtcNow < discoverEnd)
{
    _ = finder.WaitForSources(1000);
    var sources = finder.GetCurrentSources();
    if (sources.Length == 0)
    {
        Console.WriteLine("Waiting for NDI sources...");
        continue;
    }

    selected = sources[0];
    break;
}

if (selected is null)
{
    Console.WriteLine("No NDI source found within timeout.");
    return;
}

Console.WriteLine($"Connecting to first source: {selected.Value.Name}");

using var receiver = new NdiReceiver(new NdiReceiverSettings
{
    ColorFormat = NdiRecvColorFormat.RgbxRgba,
    Bandwidth = NdiRecvBandwidth.Highest,
    AllowVideoFields = false,
    ReceiverName = "MFPlayer NdiVideoPreview"
});
receiver.Connect(selected.Value);

using var frameSync = new NdiFrameSync(receiver);
var frameSyncLock = new Lock();
var clockOptions = NdiReceiveTuningPresets.CreateClockOptions(tuningProfile);
var audioSourceOptions = NdiReceiveTuningPresets.CreateAudioOptions(tuningProfile);
var timelineClock = new NdiExternalTimelineClock(clockOptions);
Console.WriteLine($"NDI receive tuning profile: {tuningProfile}");

var requestedAudioConfig = AudioConfig.Default;
var negotiatedSampleRate = requestedAudioConfig.SampleRate;
var negotiatedChannels = requestedAudioConfig.Channels;

NdiAudioFrameV3 probeAudio;
lock (frameSyncLock)
    frameSync.CaptureAudio(out probeAudio, 0, 0, 0);
try
{
    if (probeAudio.SampleRate > 0)
        negotiatedSampleRate = probeAudio.SampleRate;

    if (probeAudio.NoChannels > 0)
        negotiatedChannels = probeAudio.NoChannels;

    Console.WriteLine($"NDI audio probe: {probeAudio.SampleRate}Hz, {probeAudio.NoChannels}ch");
}
finally
{
    lock (frameSyncLock)
        frameSync.FreeAudio(probeAudio);
}

var audioConfig = new AudioConfig
{
    SampleRate = negotiatedSampleRate,
    // Keep local playback stereo for broad device compatibility.
    Channels = Math.Clamp(negotiatedChannels, 1, 2),
    BufferSize = requestedAudioConfig.BufferSize
};

using var audioEngine = new NativeAudioEngine();
if (audioEngine.Initialize(audioConfig) < 0)
{
    Console.WriteLine("Failed to initialize OwnAudio NativeAudioEngine.");
    return;
}

if (audioEngine.Start() < 0)
{
    Console.WriteLine("Failed to start OwnAudio NativeAudioEngine.");
    return;
}

var negotiatedBufferSize = audioEngine.FramesPerBuffer > 0
    ? audioEngine.FramesPerBuffer
    : audioConfig.BufferSize;

audioConfig = new AudioConfig
{
    SampleRate = audioConfig.SampleRate,
    Channels = audioConfig.Channels,
    BufferSize = negotiatedBufferSize
};

using var audioMixer = new AudioMixer(audioEngine, negotiatedBufferSize);

var videoTransportConfig = new VideoTransportEngineConfig
{
    PresentationSyncMode = VideoTransportPresentationSyncMode.PreferVSync,
    ClockSyncMode = VideoTransportClockSyncMode.AudioLed
}.CloneNormalized();

var videoClock = new MasterClockVideoClockAdapter(audioMixer.MasterClock);
using var videoTransport = new VideoTransportEngine(videoClock, videoTransportConfig, ownsClock: false);
using var videoMixer = new VideoMixer(videoTransport, ownsEngine: false);
using var playbackMixer = new AudioVideoMixer(
    audioMixer,
    videoMixer,
    new AudioVideoDriftCorrectionConfig { Enabled = true },
    ownsAudioMixer: false,
    ownsVideoMixer: false);

using var ndiAudioSource = new NdiAudioStreamSource(frameSync, audioConfig, timelineClock, frameSyncLock, audioSourceOptions);
using var ndiVideoDecoder = new NdiVideoStreamDecoder(frameSync, timelineClock, frameSyncLock);
using var ndiVideoSource = new VideoStreamSource(
    ndiVideoDecoder,
    new VideoStreamSourceOptions
    {
        HoldLastFrameOnEndOfStream = true
    },
    ownsDecoder: false);

ndiAudioSource.AttachToClock(audioMixer.MasterClock);

if (!playbackMixer.AddAudioSource(ndiAudioSource))
{
    Console.WriteLine("Failed to add NDI audio source to AudioVideoMixer.");
    return;
}

if (!playbackMixer.AddVideoSource(ndiVideoSource))
{
    Console.WriteLine("Failed to add NDI video source to AudioVideoMixer.");
    return;
}

using var output = new VideoSDL();
var streamInfo = ndiVideoDecoder.StreamInfo;
var initialWidth = streamInfo.Width > 0 ? streamInfo.Width : 1280;
var initialHeight = streamInfo.Height > 0 ? streamInfo.Height : 720;

if (!output.Initialize(initialWidth, initialHeight, $"NDI Preview - {selected.Value.Name}", out var sdlError))
{
    Console.WriteLine($"SDL init failed: {sdlError}");
    return;
}

output.Start();
output.KeyDown += key =>
{
    if (key == SDL.Keycode.H)
    {
        output.SetHudOverlayEnabled(!output.EnableHudOverlay);
        Console.WriteLine($"HUD {(output.EnableHudOverlay ? "enabled" : "disabled")}");
    }
};

if (!playbackMixer.AddVideoOutput(output))
{
    Console.WriteLine("Failed to add SDL output to AudioVideoMixer.");
    return;
}

if (!playbackMixer.BindVideoOutputToSource(output, ndiVideoSource))
{
    Console.WriteLine("Failed to bind SDL output to NDI video source.");
    return;
}

ndiAudioSource.Play();
playbackMixer.Start();

Console.WriteLine("AudioVideoMixer started. Close window or press Escape to exit.");
Console.WriteLine("Hotkeys: H = toggle SDL HUD, Esc = quit");

var lastStatus = DateTime.UtcNow;
var lastReadRequests = 0L;
var lastCapturedBlocks = 0L;
var lastDecoded = 0L;
var lastPresented = 0L;
var lastDropped = 0L;
var lastRendered = 0L;
while (output.IsRunning)
{
    Thread.Sleep(25);

    if ((DateTime.UtcNow - lastStatus).TotalSeconds < 2)
        continue;

    var connections = receiver.GetConnectionCount();
    if (connections <= 0)
        Console.WriteLine("Receiver disconnected; waiting for source...");
    else
    {
        var readRequests = ndiAudioSource.ReadRequestCount;
        var capturedBlocks = ndiAudioSource.CapturedBlockCount;
        var decoded = ndiVideoSource.DecodedFrameCount;
        var presented = ndiVideoSource.PresentedFrameCount;
        var dropped = ndiVideoSource.DroppedFrameCount;
        var sdlDiag = output.GetDiagnosticsSnapshot();
        var rendered = sdlDiag.FramesRendered;

        var decodedDelta = decoded - lastDecoded;
        var presentedDelta = presented - lastPresented;
        var droppedDelta = dropped - lastDropped;
        var renderedDelta = rendered - lastRendered;

        output.UpdateFormatInfo(
            ndiVideoSource.DecoderSourcePixelFormatName,
            ndiVideoSource.DecoderOutputPixelFormatName,
            ndiVideoDecoder.StreamInfo.FrameRate);
        output.UpdateHudDiagnostics(
            ndiVideoSource.QueueDepth,
            uploadMsPerFrame: 0,
            avDriftMs: (ndiVideoSource.CurrentFramePtsSeconds - playbackMixer.Position) * 1000.0,
            isHardwareDecoding: ndiVideoSource.IsHardwareDecoding,
            droppedFrames: dropped);

        Console.WriteLine(
            $"AUDIO fill={ndiAudioSource.RingFillRatio:P0} underruns={ndiAudioSource.UnderrunCount} read+={readRequests - lastReadRequests} captured+={capturedBlocks - lastCapturedBlocks} state={ndiAudioSource.State} | VIDEO fps(dec/pres/rnd)={decodedDelta / 2.0:F1}/{presentedDelta / 2.0:F1}/{renderedDelta / 2.0:F1} drop+={droppedDelta} q={ndiVideoSource.QueueDepth} pts={ndiVideoSource.CurrentFramePtsSeconds:F3}s clk={playbackMixer.Position:F3}s hud={(output.EnableHudOverlay ? "on" : "off")}");
        lastReadRequests = readRequests;
        lastCapturedBlocks = capturedBlocks;
        lastDecoded = decoded;
        lastPresented = presented;
        lastDropped = dropped;
        lastRendered = rendered;
    }

    lastStatus = DateTime.UtcNow;
}

playbackMixer.Stop();
output.Stop();
audioEngine.Stop();


