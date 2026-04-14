// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.NDISender
//   1. Enter an audio file path
//   2. Optionally enter an NDI source name  (default: "MFPlayer NDI Audio")
//   3. Broadcast audio to the local network so NDI clients can receive it
//   4. Press Enter or Ctrl+C to stop; auto-stops at end of file
//
// Pipeline
//   FFmpegDecoder ──► FFmpegAudioChannel ──► AVMixer(audio path) ──► NDIAVSink ──► NDISender
//   VirtualAudioOutput drives the clock (Stopwatch-based, no hardware device needed)
// ═══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using FFmpeg.AutoGen;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Audio.Routing;
using S.Media.Core.Media;
using S.Media.Core.Mixing;
using S.Media.FFmpeg;
using S.Media.NDI;

// Catch unhandled background-thread exceptions before the process dies.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"\n[FATAL] {e.ExceptionObject}");

Console.WriteLine("╔═══════════════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  NDI Audio Sender       ║");
Console.WriteLine("╚═══════════════════════════════════════╝\n");

// Set FFmpeg library path.
ffmpeg.RootPath = "/lib";

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

    // ── 2. Audio file path ────────────────────────────────────────────────────

    Console.Write("Audio file path: ");
    string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

    if (!File.Exists(filePath))
    {
        Console.WriteLine("File not found.");
        return;
    }

    // ── 3. NDI source name ────────────────────────────────────────────────────

    Console.Write("NDI source name [MFPlayer NDI Audio]: ");
    string? rawName   = Console.ReadLine()?.Trim();
    string senderName = string.IsNullOrEmpty(rawName) ? "MFPlayer NDI Audio" : rawName;

    // ── 4. Open FFmpeg decoder ────────────────────────────────────────────────

    Console.Write("\nOpening decoder… ");
    FFmpegDecoder decoder;
    try { decoder = FFmpegDecoder.Open(filePath); }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }

    if (decoder.AudioChannels.Count == 0)
    {
        Console.WriteLine("No audio streams found in file.");
        decoder.Dispose();
        return;
    }

    using (decoder)
    {
        var audioChannel = decoder.AudioChannels[0];
        var srcFmt       = audioChannel.SourceFormat;

        // NDI target format: 48 kHz, up to stereo.
        // The mixer's LinearResampler handles any sample-rate conversion automatically.
        var ndiFormat = new AudioFormat(48000, Math.Min(srcFmt.Channels, 2));
        var routeMap  = BuildRouteMap(srcFmt.Channels, ndiFormat.Channels);

        Console.WriteLine("OK");
        Console.WriteLine($"  Source:  {srcFmt.SampleRate} Hz / {srcFmt.Channels} ch  " +
                          $"({Path.GetFileName(filePath)})");
        Console.WriteLine($"  NDI out: {ndiFormat.SampleRate} Hz / {ndiFormat.Channels} ch");

        // ── 5. Create NDI sender ──────────────────────────────────────────────

        // clockAudio: false — VirtualAudioOutput drives timing via Stopwatch;
        // we do NOT want NDI's internal clock to add a second layer of blocking.
        Console.Write("Creating NDI sender… ");
        int senderRet = NDISender.Create(out var sender, senderName, clockAudio: false);
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
            //   VirtualAudioOutput (clock master, no hardware device)
            //       └─► AVMixer (audio routing + sink fan-out)
            //               └─► NDIAVSink (audio path: interleaved→planar, SendAudio)
            //                       └─► NDISender ──► network
            //
            // The AV mixer attached to VirtualAudioOutput pulls from FFmpegAudioChannel
            // and routes that audio to NDIAVSink on each tick.

            const int framesPerBuffer = 1024; // ~21 ms @ 48 kHz — matches NDIAudioChannel chunk size

            using var virtualOut = new VirtualAudioOutput(ndiFormat, framesPerBuffer);
            using var avMixer = new AVMixer(ndiFormat);
            var ndiSink = new NDIAVSink(
                sender,
                audioTargetFormat: ndiFormat,
                audioFramesPerBuffer: framesPerBuffer,
                name: $"NDIAVSink({senderName})");

            using (ndiSink)
            {
                avMixer.AttachAudioOutput(virtualOut);
                avMixer.RegisterAudioSink(ndiSink, ndiFormat.Channels);

                // Route source audio explicitly to the NDI sink.
                avMixer.AddAudioChannel(audioChannel, routeMap);
                avMixer.RouteAudioChannelToSink(audioChannel.Id, ndiSink, routeMap);

            // ── 7. Completion detection (source-ended + drain) ───────────────

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            int sourceEnded = 0;
            long sourceEndedTicks = 0;
            const double DrainGraceSeconds = 0.30;

            void MarkSourceEnded(string tag)
            {
                Volatile.Write(ref sourceEnded, 1);
                Interlocked.Exchange(ref sourceEndedTicks, Stopwatch.GetTimestamp());
                Console.WriteLine($"\n[{tag}: waiting for drain]");
            }

            decoder.EndOfMedia += (_, _) => MarkSourceEnded("demux EOF");
            audioChannel.EndOfStream += (_, _) => MarkSourceEnded("decode EOF");

            // Ignore underruns during the first 2 s (buffer warm-up while the decoder
            // pre-fills its ring). Underrun alone is not treated as EOF.
            var warmUp = Stopwatch.StartNew();
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

            // ── 8. Start ──────────────────────────────────────────────────────

                Console.Write("Starting… ");
                decoder.Start();
                await ndiSink.StartAsync();
                await virtualOut.StartAsync();
                Console.WriteLine("OK\n");

            Console.WriteLine($"Broadcasting:  {senderName}");
            Console.WriteLine("NDI clients on the local network can now connect to this source.");
            Console.WriteLine("Press [Enter] or [Ctrl+C] to stop.\n");

            // ── 9. Status display ─────────────────────────────────────────────

                _ = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        int     receivers = sender.GetConnectionCount();
                        TimeSpan position = audioChannel.Position;
                        Console.Write($"\r  {position:mm\\:ss\\:ffff}   Receivers connected: {receivers}   ");
                        try   { await Task.Delay(10, cts.Token).ConfigureAwait(false); }
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
                await virtualOut.StopAsync();
                await ndiSink.StopAsync();
                Console.WriteLine("Done.");
            }
        } // sender disposed
    } // decoder disposed
} // ndiRuntime disposed

// ── Helpers ───────────────────────────────────────────────────────────────────

// Mono source -> both stereo channels; multi-channel -> straight-across up to min(src, dst).
static ChannelRouteMap BuildRouteMap(int srcChannels, int dstChannels)
{
    var b = new ChannelRouteMap.Builder();
    if (srcChannels == 1 && dstChannels >= 2)
    {
        b.Route(0, 0).Route(0, 1);
    }
    else
    {
        int common = Math.Min(srcChannels, dstChannels);
        for (int i = 0; i < common; i++) b.Route(i, i);
    }
    return b.Build();
}

