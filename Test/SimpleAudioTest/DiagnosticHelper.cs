using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;

namespace SimpleAudioTest;

internal static class DiagnosticHelper
{
    internal static void RunDiagnostics(string uri)
    {
        Console.WriteLine("=== FFmpeg Diagnostics ===");
        Console.WriteLine($"URI: {uri}");

        // 1. Check native FFmpeg availability
        try
        {
            var version = ffmpeg.avformat_version();
            Console.WriteLine($"[OK] avformat_version = {version >> 16}.{(version >> 8) & 0xFF}.{version & 0xFF}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] avformat_version: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // 2. Try open with shared decode context
        try
        {
            var openOpts = new FFmpegOpenOptions { InputUri = uri };
            Console.WriteLine($"OpenOptions: OpenAudio={openOpts.OpenAudio}, OpenVideo={openOpts.OpenVideo}, UseShared={openOpts.UseSharedDecodeContext}");

            var openCode = FFmpegMediaItem.Create(uri, out var media);
            if (openCode != MediaResult.Success)
            {
                Console.Error.WriteLine($"[FAIL] FFmpegMediaItem.Create returned {openCode}");
                return;
            }
            using var _media = media!;
            Console.WriteLine($"[OK] FFmpegMediaItem.Create succeeded");
            Console.WriteLine($"  AudioSource: {(_media.AudioSource is not null ? "present" : "null")}");
            Console.WriteLine($"  VideoSource: {(_media.VideoSource is not null ? "present" : "null")}");

            if (_media.AudioSource is not null)
            {
                var info = _media.AudioSource.StreamInfo;
                Console.WriteLine($"  Audio: channels={info.ChannelCount}, rate={info.SampleRate}, codec={info.Codec}, duration={info.Duration}");

                var startResult = _media.AudioSource.Start();
                Console.WriteLine($"  Start: {startResult}");

                var buf = new float[4096];
                var readResult = _media.AudioSource.ReadSamples(buf, 1024, out var framesRead);
                Console.WriteLine($"  ReadSamples: result={readResult}, framesRead={framesRead}");

                if (readResult == MediaResult.Success && framesRead > 0)
                {
                    // Check for non-zero samples
                    var nonZero = 0;
                    for (var i = 0; i < Math.Min(framesRead * 2, buf.Length); i++)
                        if (Math.Abs(buf[i]) > 1e-8f) nonZero++;
                    Console.WriteLine($"  Non-zero samples in first read: {nonZero}");
                }
            }

            Console.WriteLine("[OK] Diagnostics complete");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
        }

        Console.WriteLine("=== End Diagnostics ===\n");
    }
}

