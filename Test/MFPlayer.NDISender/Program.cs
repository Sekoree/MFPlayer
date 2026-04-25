// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.NDISender
//   1. Enter a media file path (audio, video, or both)
//   2. Optionally enter an NDI source name  (default: "MFPlayer NDI")
//   3. Broadcast A/V to the local network so NDI clients can receive it
//   4. Press Enter or Ctrl+C to stop; auto-stops at end of file
//
// Pipeline
//   FFmpegDecoder channels ──► MediaPlayer builder (external-input mode) ──► NDIAVEndpoint ──► NDISender
//   VirtualClockEndpoint.Clock is registered as override clock (no hardware output device required)
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using FFmpeg.AutoGen;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.NDI;

// Catch unhandled background-thread exceptions before the process dies.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"\n[FATAL] {e.ExceptionObject}");

Console.WriteLine("╔═══════════════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  NDI A/V Sender         ║");
Console.WriteLine("╚═══════════════════════════════════════╝\n");

// Set FFmpeg library path.
ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

// ── 1. Initialise NDI runtime ─────────────────────────────────────────────────

if (!NDIRuntime.IsSupportedCpu())
{
    Console.WriteLine("This CPU does not meet the NDI requirements (SSE4.2 required).");
    return;
}

int ndiRet = NDIRuntime.Create(out var ndiRuntime);
if (ndiRet != 0 || ndiRuntime == null)
{
    Console.WriteLine($"Failed to initialise NDI runtime (code {ndiRet}).");
    Console.WriteLine("Make sure the NDI runtime is installed: https://ndi.video/tools/");
    return;
}

using (ndiRuntime)
{
    Console.WriteLine($"NDI runtime  v{NDIRuntime.Version}\n");

    // ── 2. Media file path ────────────────────────────────────────────────────

    Console.Write("Media file path: ");
    string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

    if (!File.Exists(filePath))
    {
        Console.WriteLine("File not found.");
        return;
    }

    // ── 3. NDI source name ────────────────────────────────────────────────────

    Console.Write("NDI source name [MFPlayer NDI]: ");
    string? rawName   = Console.ReadLine()?.Trim();
    string senderName = string.IsNullOrEmpty(rawName) ? "MFPlayer NDI" : rawName;

    // ── 4. Open FFmpeg decoder (audio + video) ──────────────────────────────

    Console.Write("\nOpening decoder… ");
    FFmpegDecoder decoder;
    try
    {
        decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions
        {
            EnableAudio = true,
            EnableVideo = true,
            // RGBA is the most universally compatible NDI pixel format
            VideoTargetPixelFormat = PixelFormat.Rgba32
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }

    bool hasAudio = decoder.AudioChannels.Count > 0;
    bool hasVideo = decoder.VideoChannels.Count > 0;

    if (!hasAudio && !hasVideo)
    {
        Console.WriteLine("No audio or video streams found in file.");
        decoder.Dispose();
        return;
    }

    using (decoder)
    {
        IAudioChannel? audioChannel = hasAudio ? decoder.AudioChannels[0] : null;
        IVideoChannel? videoChannel = hasVideo ? decoder.VideoChannels[0] : null;

        // ── Determine NDI audio format ──
        AudioFormat? ndiAudioFormat = null;
        if (audioChannel != null)
        {
            var srcFmt = audioChannel.SourceFormat;
            ndiAudioFormat = new AudioFormat(48000, Math.Min(srcFmt.Channels, 2));
        }

        // ── Determine NDI video format ──
        VideoFormat? ndiVideoFormat = null;
        if (videoChannel != null)
        {
            var srcVid = videoChannel.SourceFormat;
            ndiVideoFormat = new VideoFormat(
                srcVid.Width,
                srcVid.Height,
                PixelFormat.Rgba32,
                srcVid.FrameRateNumerator > 0 ? srcVid.FrameRateNumerator : 30000,
                srcVid.FrameRateDenominator > 0 ? srcVid.FrameRateDenominator : 1001);
        }

        Console.WriteLine("OK");
        if (audioChannel != null)
        {
            var srcFmt = audioChannel.SourceFormat;
            Console.WriteLine($"  Audio source:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch");
            Console.WriteLine($"  Audio NDI out: {ndiAudioFormat!.Value.SampleRate} Hz / {ndiAudioFormat.Value.Channels} ch");
        }
        if (videoChannel != null)
        {
            var srcVid = videoChannel.SourceFormat;
            Console.WriteLine($"  Video source:  {srcVid.Width}×{srcVid.Height} {srcVid.PixelFormat} @ {srcVid.FrameRate:F2} fps");
            Console.WriteLine($"  Video NDI out: {ndiVideoFormat!.Value.Width}×{ndiVideoFormat.Value.Height} {ndiVideoFormat.Value.PixelFormat}");
        }
        if (!hasAudio) Console.WriteLine("  (no audio track)");
        if (!hasVideo) Console.WriteLine("  (no video track)");

        // ── 5. Create NDI sender ──────────────────────────────────────────────

        // clockAudio: false — VirtualClockEndpoint drives timing via Stopwatch;
        // clockVideo: false — same (router push thread drives video delivery).
        Console.Write("Creating NDI sender… ");
        int senderRet = NDISender.Create(out var sender, senderName, clockAudio: false, clockVideo: false);
        if (senderRet != 0 || sender == null)
        {
            Console.WriteLine($"FAILED (code {senderRet}).");
            return;
        }

        using (sender)
        {
            string? advertisedName = sender.GetSourceName();
            Console.WriteLine("OK");
            Console.WriteLine($"  Advertised as: {advertisedName ?? senderName}");

            // ── 6. Wire the pipeline ─────────────────────────────────────────
            //
            //   FFmpegDecoder channels (external inputs) ─► MediaPlayer builder
            //       ├─► NDIAVEndpoint (audio/video sink)
            //       └─► VirtualClockEndpoint.Clock registered as Override

            const int framesPerBuffer = 1024; // ~21 ms @ 48 kHz

            using var virtualClock = new VirtualClockEndpoint();

            var ndiSink = new NDIAVEndpoint(
                sender,
                videoTargetFormat: ndiVideoFormat,
                audioTargetFormat: ndiAudioFormat,
                audioFramesPerBuffer: framesPerBuffer,
                name: $"NDIAVEndpoint({senderName})");

            using (ndiSink)
            {
                var builder = MediaPlayer.Create()
                    .WithAVOutput(ndiSink)
                    .WithClock(virtualClock.Clock, ClockPriority.Override);
                if (audioChannel != null)
                    builder.WithAudioInput(audioChannel);
                if (videoChannel != null)
                    builder.WithVideoInput(videoChannel);
                using var player = builder.Build();

            // ── 7. Completion detection (source-ended + drain) ───────────────

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            int sourceEnded = 0;
            long sourceEndedTicks = 0;
            const double DrainGraceSeconds = 0.30;

            void MarkSourceEnded(string tag)
            {
                if (Interlocked.CompareExchange(ref sourceEnded, 1, 0) == 0)
                {
                    Interlocked.Exchange(ref sourceEndedTicks, Stopwatch.GetTimestamp());
                    Console.WriteLine($"\n[{tag}: waiting for drain]");
                }
            }

            decoder.EndOfMedia += (_, _) => MarkSourceEnded("demux EOF");
            if (audioChannel != null)
                audioChannel.EndOfStream += (_, _) => MarkSourceEnded("audio decode EOF");

            // Ignore underruns during the first 2 s (buffer warm-up while the decoder
            // pre-fills its ring). Underrun alone is not treated as EOF.
            var warmUp = Stopwatch.StartNew();
            if (audioChannel != null)
            {
                audioChannel.BufferUnderrun += (_, _) =>
                {
                    if (warmUp.Elapsed.TotalSeconds <= 2 || cts.IsCancellationRequested)
                        return;

                    if (Volatile.Read(ref sourceEnded) == 1 && audioChannel.BufferAvailable == 0)
                    {
                        long endedAt = Interlocked.Read(ref sourceEndedTicks);
                        double sinceEnded = (Stopwatch.GetTimestamp() - endedAt) / (double)Stopwatch.Frequency;
                        if (endedAt != 0 && sinceEnded >= DrainGraceSeconds)
                        {
                            Console.WriteLine("\n[Playback drained]");
                            cts.Cancel();
                        }
                    }
                };
            }

            // For video-only files, use the video channel's EndOfStream + buffer check
            if (videoChannel != null && audioChannel == null)
            {
                videoChannel.EndOfStream += (_, _) => MarkSourceEnded("video decode EOF");
                videoChannel.BufferUnderrun += (_, _) =>
                {
                    if (warmUp.Elapsed.TotalSeconds <= 2 || cts.IsCancellationRequested)
                        return;

                    if (Volatile.Read(ref sourceEnded) == 1 && videoChannel.BufferAvailable == 0)
                    {
                        long endedAt = Interlocked.Read(ref sourceEndedTicks);
                        double sinceEnded = (Stopwatch.GetTimestamp() - endedAt) / (double)Stopwatch.Frequency;
                        if (endedAt != 0 && sinceEnded >= DrainGraceSeconds)
                        {
                            Console.WriteLine("\n[Playback drained]");
                            cts.Cancel();
                        }
                    }
                };
            }

            // ── 8. Start ──────────────────────────────────────────────────────

                Console.Write("Starting… ");
                decoder.Start();
                await virtualClock.StartAsync();
                await player.PlayAsync();
                Console.WriteLine("OK\n");

            string modeLabel = (hasAudio, hasVideo) switch
            {
                (true, true)   => "A/V",
                (true, false)  => "Audio-only",
                (false, true)  => "Video-only",
                _              => "???"
            };
            Console.WriteLine($"Broadcasting ({modeLabel}):  {senderName}");
            Console.WriteLine("NDI clients on the local network can now connect to this source.");
            Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

            // ── 9. Status display ─────────────────────────────────────────────

                _ = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        int receivers = sender.GetConnectionCount();
                        TimeSpan clockPos = player.Router.Clock.Position;
                        string clockStr = clockPos.ToString(@"mm\:ss\.ff");

                        var parts = new List<string> { $"Clock: {clockStr}" };

                        if (audioChannel != null)
                            parts.Add($"A: {audioChannel.Position:mm\\:ss\\.ff}");

                        if (videoChannel != null)
                            parts.Add($"V: {videoChannel.Position:mm\\:ss\\.ff}");

                        if (audioChannel != null && videoChannel != null)
                        {
                            var drift = audioChannel.Position - videoChannel.Position;
                            parts.Add($"A-V: {drift.TotalMilliseconds:+0;-0}ms");
                        }

                        if (hasVideo)
                        {
                            var diag = ndiSink.GetDiagnosticsSnapshot();
                            parts.Add($"VQ: {diag.QueueDepth}  Drop: {diag.DroppedFrames}");
                        }

                        parts.Add($"Rx: {receivers}");

                        Console.Write($"\r  {string.Join("  |  ", parts)}   ");
                        try   { await Task.Delay(100, cts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                });

            // Allow Enter to cancel (guard against stdin at EOF in Rider's piped console).
                _ = Task.Run(() =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var line = Console.ReadLine();
                        if (line != null) { cts.Cancel(); break; }
                        Thread.Sleep(200);
                    }
                });

                try   { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
                catch (OperationCanceledException) { }

            // ── 10. Stop ──────────────────────────────────────────────────────

                Console.WriteLine("\n\nStopping… ");
                await player.StopAsync();
                await virtualClock.StopAsync();
                Console.WriteLine("Done.");
            }
        } // sender disposed
    } // decoder disposed
} // ndiRuntime disposed
