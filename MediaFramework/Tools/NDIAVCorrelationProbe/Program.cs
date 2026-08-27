// Phase 5 NDI A/V-correlation probe. Quantifies the divergence between the two ways an NDI source can be
// opened:
//   • TWO connections - what MediaPlayer's `ndi://` open does today: registry.SelectAndOpenVideo then SelectAndOpenAudio,
//     each opening a SEPARATE NDISource (video-only / audio-only). They connect and start receiving
//     independently, so their A and V timelines anchor at different moments.
//   • ONE connection - the SourceSyncGroup fix: a single receiver delivers both A and V, anchored together.
// NDI's ingest clock is AUDIO-driven (video is matched to the audio ring), so a video-only connection has no
// advancing clock - the measurable divergence is the first-frame anchor offset: how far apart the two
// independent receivers begin their A and V streams. Re-run after the fix: one connection ⇒ a single anchor.
using System.Diagnostics;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.Time;

var nameFilter = args.Length > 0 ? args[0] : null;
var settleFor = TimeSpan.FromSeconds(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10);

var rc = NDIRuntime.Create(out var runtime);
if (rc != 0 || runtime is null)
{
    Console.Error.WriteLine($"NDI runtime init failed ({rc}).");
    return 3;
}

Console.WriteLine("discovering NDI sources…");
var sources = NDISource.Find(TimeSpan.FromSeconds(3));
if (sources.Count == 0)
{
    Console.Error.WriteLine("no NDI sources on the network.");
    return 4;
}

var chosen = sources.FirstOrDefault(x => nameFilter is null || x.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
if (chosen.Name is null)
{
    Console.Error.WriteLine($"no NDI source matching '{nameFilter}'. Available: {string.Join(", ", sources.Select(x => x.Name))}");
    return 4;
}

Console.WriteLine($"source '{chosen.Name}' - two-connection vs one-connection A/V anchoring…");

var running = true;
long firstVideoMs = 0; // set-once, 0 = not yet
long firstAudioMs = 0;

// --- TWO-connection model: open video-only then audio-only back-to-back, exactly as MediaPlayer's
//     registry.SelectAndOpenVideo then SelectAndOpenAudio would. A shared stopwatch starts at the first open. ----------
var wall = Stopwatch.StartNew();
using var videoOnly = NDISource.Open(chosen, new NDISourceOptions { ReceiveVideo = true, ReceiveAudio = false });
using var audioOnly = NDISource.Open(chosen, new NDISourceOptions { ReceiveVideo = false, ReceiveAudio = true });

var drainVideo = new Thread(() =>
{
    while (Volatile.Read(ref running))
    {
        if (videoOnly.Video.TryReadNextFrame(out var f))
        {
            Interlocked.CompareExchange(ref firstVideoMs, wall.ElapsedMilliseconds, 0);
            f.Dispose();
        }
        else Thread.Sleep(1);
    }
}) { IsBackground = true, Name = "drain-video" };
var drainAudio = new Thread(() =>
{
    var buf = new float[48_000];
    while (Volatile.Read(ref running))
    {
        if (audioOnly.Audio.ReadInto(buf) > 0)
            Interlocked.CompareExchange(ref firstAudioMs, wall.ElapsedMilliseconds, 0);
        else Thread.Sleep(1);
    }
}) { IsBackground = true, Name = "drain-audio" };
drainVideo.Start();
drainAudio.Start();

if (!SpinWait.SpinUntil(
        () => Volatile.Read(ref firstVideoMs) != 0 && Volatile.Read(ref firstAudioMs) != 0,
        TimeSpan.FromSeconds(12)))
{
    Volatile.Write(ref running, false);
    Console.Error.WriteLine("FAIL: a stream never delivered a frame - does the sender send both audio and video?");
    return 5;
}

var vFirst = Interlocked.Read(ref firstVideoMs);
var aFirst = Interlocked.Read(ref firstAudioMs);
var anchorOffset = vFirst - aFirst; // +ve ⇒ video started later than audio

// Let both run a while to confirm they keep flowing (no measurement drift expected - RebaseToLatest on the
// video side re-anchors continuously, so this offset is the *startup* lip-sync the two-connection open ships).
Thread.Sleep(settleFor);
Volatile.Write(ref running, false);

// --- ONE-connection model (direct): one receiver delivers both, by construction. -------------------------
using var combined = NDISource.Open(chosen, new NDISourceOptions { ReceiveVideo = true, ReceiveAudio = true });
// WaitForStreams, not a bare IsAdvancing spin: the ingest clock is audio-driven and can start
// advancing milliseconds before the first VIDEO frame latches its format - checking the formats
// at that instant intermittently reported "NOT confirmed" against a healthy sender.
var combinedReady = combined.WaitForStreams(TimeSpan.FromSeconds(8))
                    && SpinWait.SpinUntil(() => combined.IngestClock.IsAdvancing, TimeSpan.FromSeconds(4));
var hasVideo = combined.TryGetVideoFormat(out var vf);
var hasAudio = combined.TryGetAudioFormat(out var af);

// --- THE FIX: open ndi:// through the registry exactly as MediaPlayer does (the atomic OpenAsync).
//     The paired lease lands both streams on ONE receiver - LiveConnectionCount rises by 1, not 2 -
//     and disposing the result tears it down. (The single-stream SelectAndOpenVideo/Audio calls get
//     independent receivers by design now; correlated A/V is the atomic open's contract.) ----------
// OpenAsync is a default interface member - reach it through IMediaRegistry, as MediaPlayer does.
IMediaRegistry registry = MediaRegistry.Build(b => b.Use(new NDIModule()));
var ndiUri = "ndi://" + Uri.EscapeDataString(chosen.Name);
var connectionsBefore = NDISource.LiveConnectionCount;
var opened = await registry.OpenAsync(new MediaOpenRequest(ndiUri)
{
    Video = new VideoSourceOpenOptions(),
    Audio = new AudioSourceOpenOptions(),
});
var registryOpened = NDISource.LiveConnectionCount - connectionsBefore;
await opened.DisposeAsync();
var releasedToBaseline = NDISource.LiveConnectionCount == connectionsBefore;

Console.WriteLine();
Console.WriteLine($"two-connection (old)   : V first frame @ {vFirst} ms, A first frame @ {aFirst} ms → independent anchor offset {anchorOffset:+0;-0;0} ms");
Console.WriteLine($"one-connection (direct): both on one receiver (V={(hasVideo ? $"{vf.Width}x{vf.Height}" : "-")} + A={(hasAudio ? $"{af.SampleRate}Hz/{af.Channels}ch" : "-")}): {(combinedReady && hasVideo && hasAudio ? "confirmed" : "NOT confirmed")}");
Console.WriteLine($"registry path (fix)    : one atomic ndi:// OpenAsync opened {registryOpened} receiver(s) - expected 1 (shared); released to baseline: {releasedToBaseline}");
Console.WriteLine();

var registryOk = registryOpened == 1 && releasedToBaseline;
var combinedOk = combinedReady && hasVideo && hasAudio;
var fixWorks = registryOk && combinedOk;
if (fixWorks)
{
    Console.WriteLine($"NDIAVCorrelationProbe OK - the registry now shares ONE receiver for A+V (was two, ~{Math.Abs(anchorOffset)} ms apart at startup). A and V anchor together on the one audio-driven ingest clock; the startup lip-sync offset is removed.");
}
else
{
    // Name the check that actually failed - a FAIL that always prints the registry numbers sent
    // a reader chasing the wrong half when the direct combined open was the culprit.
    var reasons = new List<string>();
    if (!registryOk)
        reasons.Add($"registry opened {registryOpened} receiver(s) (expected 1), releasedToBaseline={releasedToBaseline}");
    if (!combinedOk)
        reasons.Add($"direct one-connection open: ready={combinedReady}, video={(hasVideo ? "yes" : "no")}, audio={(hasAudio ? "yes" : "no")}");
    Console.WriteLine($"NDIAVCorrelationProbe FAIL - {string.Join("; ", reasons)}.");
}
return fixWorks ? 0 : 6;
