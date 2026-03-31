using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Runtime;
using S.Media.NDI.Config;
using S.Media.NDI.Runtime;
using TestShared;

namespace NDISendTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        FFmpegRuntime.EnsureInitialized();
        var a = CommonTestArgs.Parse(args);
        var senderName = TestHelpers.GetArg(args, "--sender-name") ?? "MFPlayer NDISendTest";
        var seconds = a.Seconds > 0 ? a.Seconds : 60;

        if (a.ShowHelp) { PrintUsage(); return 0; }

        if (string.IsNullOrWhiteSpace(a.Input))
        {
            Console.Error.WriteLine("Missing --input <path>. Use --help for usage.");
            return 1;
        }

        var uri = TestHelpers.ResolveUri(a.Input);
        if (uri is null) { Console.Error.WriteLine($"Input file not found: {a.Input}"); return 2; }

        Console.WriteLine($"Input: {uri}");
        Console.WriteLine($"NDI sender name: {senderName}");

        try
        {
            var rErr = NDIRuntime.Create(out var runtimeInst);
            if (rErr != 0) { Console.Error.WriteLine($"NDI init failed: {rErr}"); return 1; }
            using var _runtime = runtimeInst!;
            Console.WriteLine($"NDI runtime version: {NDIRuntime.Version}");

            using var media = FFmpegMediaItem.Open(uri);

            var videoSource = media.VideoSource;
            if (videoSource is null) { Console.Error.WriteLine("No video source in media."); return 3; }
            if (videoSource.Start() != MediaResult.Success) { Console.Error.WriteLine("Video source start failed."); return 3; }

            var audioSource = media.AudioSource;
            var hasAudio = audioSource is not null;
            var sampleRate = audioSource?.StreamInfo.SampleRate.GetValueOrDefault(48_000) ?? 48_000;
            var channelCount = audioSource?.StreamInfo.ChannelCount.GetValueOrDefault(2) ?? 2;

            if (hasAudio && audioSource!.Start() != MediaResult.Success)
            {
                Console.WriteLine("Warning: audio source start failed — sending video only.");
                hasAudio = false;
            }

            var fps = videoSource.StreamInfo.FrameRate.GetValueOrDefault(30);
            var (fpsN, fpsD) = FpsToRational(fps);

            // Align audio packet size to one video frame duration for clean A/V interleaving.
            // Buffer must hold at least one full video frame of interleaved audio.
            var samplesPerPacket = fps > 0 ? Math.Max(128, (int)Math.Round(sampleRate / fps)) : 1600;
            var audioBuf = new float[samplesPerPacket * channelCount];

            Console.WriteLine($"Video: {fps:0.###} fps  |  Audio: {(hasAudio ? $"{sampleRate} Hz / {channelCount}ch" : "none")}");

            // NDI engine + output.
            // ClockVideo = true lets the NDI SDK pace video precisely — no Thread.Sleep needed.
            using var engine = new NDIEngine();
            var init = engine.Initialize();
            if (init != MediaResult.Success) { Console.Error.WriteLine($"NDI engine init failed: {init}"); return 4; }

            var createOut = engine.CreateOutput(senderName,
                new NDIOutputOptions
                {
                    EnableVideo = true,
                    EnableAudio = hasAudio,
                    FrameRateN = fpsN,
                    FrameRateD = fpsD,
                    ClockVideo = true,   // NDI SDK paces the send loop — eliminates stutter
                },
                out var ndiOutput);
            if (createOut != MediaResult.Success || ndiOutput is null)
            {
                Console.Error.WriteLine($"NDI CreateOutput failed: {createOut}");
                return 4;
            }

            if (ndiOutput.Start(new VideoOutputConfig()) != MediaResult.Success)
            {
                Console.Error.WriteLine("NDI output start failed.");
                return 4;
            }

            Console.WriteLine($"NDI output created: {ndiOutput.OutputName}");
            Console.WriteLine($"Sending ~{seconds:0.#}s via NDI. Ctrl+C to stop.");

            var pushed = 0L;
            var failed = 0L;
            var audioPushed = 0L;
            var audioPts = TimeSpan.Zero;
            var ndiAudioSink = (S.Media.Core.Audio.IAudioSink)ndiOutput;

            TestHelpers.RunWithDeadline(seconds, () =>
            {
                // Push one full video-frame-duration of audio before each video frame.
                // ReadSamples returns at most one FFmpeg chunk (~1024 samples) per call, so we
                // loop until we have enough samples to cover the full frame duration. Without
                // this, the NDI receiver's audio buffer starves and produces glitches.
                if (hasAudio)
                {
                    var totalRead = 0;
                    while (totalRead < samplesPerPacket)
                    {
                        var toRequest = samplesPerPacket - totalRead;
                        var rc = audioSource!.ReadSamples(
                            audioBuf.AsSpan(totalRead * channelCount, toRequest * channelCount),
                            toRequest, out var fr);
                        if (rc != MediaResult.Success || fr <= 0) break;
                        totalRead += fr;
                    }

                    if (totalRead > 0)
                    {
                        var audioFrame = new AudioFrame(
                            Samples: new ReadOnlyMemory<float>(audioBuf, 0, totalRead * channelCount),
                            FrameCount: totalRead,
                            SourceChannelCount: channelCount,
                            Layout: AudioFrameLayout.Interleaved,
                            SampleRate: sampleRate,
                            PresentationTime: audioPts);

                        ndiAudioSink.PushFrame(in audioFrame);
                        audioPts += TimeSpan.FromSeconds((double)totalRead / sampleRate);
                        audioPushed++;
                    }
                }

                // Read next video frame and push it.
                // With ClockVideo=true the SendVideo call blocks until the right output time,
                // so no Thread.Sleep is needed — the loop is naturally paced.
                var read = videoSource.ReadFrame(out var frame);
                if (read != MediaResult.Success)
                {
                    Console.WriteLine($"ReadFrame ended: code={read}");
                    return false;
                }

                try
                {
                    var pushCode = ndiOutput.PushFrame(frame, frame.PresentationTime);
                    if (pushCode == MediaResult.Success) pushed++;
                    else
                    {
                        failed++;
                        Console.WriteLine($"PushFrame failed: code={pushCode}");
                    }
                }
                finally
                {
                    frame.Dispose();
                }

                return true;
            }, () =>
            {
                var diag = ndiOutput.Diagnostics;
                Console.WriteLine(
                    $"pos={videoSource.PositionSeconds:0.###}s frame={videoSource.CurrentFrameIndex} | " +
                    $"vPushed={pushed} vFailed={failed} aPushed={audioPushed} | " +
                    $"ndi: vOk={diag.VideoPushSuccesses} vFail={diag.VideoPushFailures} " +
                    $"aOk={diag.AudioPushSuccesses} aFail={diag.AudioPushFailures}");
            });

            _ = ndiOutput.Stop();
            _ = videoSource.Stop();
            if (hasAudio) _ = audioSource!.Stop();
            _ = engine.Terminate();

            Console.WriteLine($"Done. vPushed={pushed} vFailed={failed} aPushed={audioPushed}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 10;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("NDISendTest — video/audio file → NDI output");
        Console.WriteLine("Usage: NDISendTest --input <file> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --input <path>           Input file path");
        Console.WriteLine("  --sender-name <name>     NDI sender name (default: MFPlayer NDISendTest)");
        Console.WriteLine("  --seconds <n>            Send duration (default: 60)");
    }

    /// <summary>
    /// Converts a double fps value to a standard NDI integer N/D rational pair.
    /// Recognises common drop-frame (NTSC) rates; everything else is rounded.
    /// </summary>
    private static (int N, int D) FpsToRational(double fps)
    {
        if (fps <= 0) return (30000, 1001);

        var ntscPairs = new (double fps, int n, int d)[]
        {
            (23.976, 24000, 1001),
            (29.97,  30000, 1001),
            (47.952, 48000, 1001),
            (59.94,  60000, 1001),
            (119.88, 120000, 1001),
        };

        foreach (var (f, n, d) in ntscPairs)
        {
            if (Math.Abs(fps - f) < 0.01)
                return (n, d);
        }

        var rounded = (int)Math.Round(fps);
        return (rounded, 1);
    }
}
