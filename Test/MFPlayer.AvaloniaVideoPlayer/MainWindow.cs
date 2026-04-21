using System.Diagnostics;
using Avalonia.Controls;
using FFmpeg.AutoGen;
using S.Media.Avalonia;
using S.Media.Core.Audio;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.Core.Routing;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace MFPlayer.AvaloniaVideoPlayer;

public sealed class MainWindow : Window
{
    private static string MatrixLabel(YuvColorMatrix m) => m switch
    {
        YuvColorMatrix.Bt709 => "709",
        YuvColorMatrix.Bt601 => "601",
        _ => "auto"
    };

    private static string RangeLabel(YuvColorRange r) => r switch
    {
        YuvColorRange.Full => "full",
        YuvColorRange.Limited => "limited",
        _ => "auto"
    };

    private readonly string[] _args;
    private readonly AvaloniaOpenGlVideoOutput _videoOutput;
    private FFmpegDecoder? _decoder;
    private bool _started;
    private bool _shutdown;

    private CancellationTokenSource? _diagCts;
    private Task? _diagTask;
    private IVideoChannel? _activeChannel;
    private AVRouter? _router;

    public MainWindow(string[] args)
    {
        _args = args;

        Title = "MFPlayer - Avalonia Video Player";
        Width = 1280;
        Height = 720;

        _videoOutput = new AvaloniaOpenGlVideoOutput();
        Content = _videoOutput;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            ffmpeg.RootPath = S.Media.FFmpeg.FFmpegLoader.ResolveDefaultSearchPath() ?? "/lib";

            string? hardwareDeviceType = GetOptionValue(_args, "--hw");
            bool forceSoftwareDecode = HasOption(_args, "--sw");
            int decoderThreads = GetIntOptionValue(_args, "--threads", 0);
            int videoBufferDepth = Math.Max(1, GetIntOptionValue(_args, "--video-buffer", 4));
            int catchupLagMs = Math.Max(1, GetIntOptionValue(_args, "--catchup-lag-ms", 45));
            int maxCatchupPulls = Math.Max(0, GetIntOptionValue(_args, "--max-catchup-pulls", 6));

            string? filePath = GetFilePathFromArgs(_args);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new InvalidOperationException("Pass a valid video file path as the first command-line argument.");

            _videoOutput.CatchupLagThreshold = TimeSpan.FromMilliseconds(catchupLagMs);
            _videoOutput.MaxCatchupPullsPerRender = maxCatchupPulls;

            _decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions
            {
                EnableAudio = false,
                EnableVideo = true,
                DecoderThreadCount = decoderThreads, // 0 = FFmpeg auto threading.
                VideoBufferDepth = videoBufferDepth,
                PreferHardwareDecoding = !forceSoftwareDecode,
                VideoTargetPixelFormat = PixelFormat.Rgba32
            });

            var decDiag = _decoder.GetDiagnosticsSnapshot();
            Console.WriteLine(
                $"[MFPlayer.AvaloniaVideoPlayer] decoder hwPref={decDiag.PreferHardwareDecoding} activeHw={decDiag.ActiveHardwareDeviceType ?? "none"} " +
                $"videoChannels={decDiag.VideoChannelCount} hwVideoChannels={decDiag.HardwareAcceleratedVideoChannelCount}");
            foreach (var chDiag in decDiag.VideoChannels)
            {
                Console.WriteLine(
                    $"[MFPlayer.AvaloniaVideoPlayer] vch stream={chDiag.StreamIndex} decoder={chDiag.DecoderName} hw={chDiag.IsHardwareAccelerated} fmt={chDiag.TargetPixelFormat}");
            }

            Console.WriteLine(
                $"[MFPlayer.AvaloniaVideoPlayer] hw={(forceSoftwareDecode ? "sw-forced" : "auto")} " +
                $"threads={decoderThreads} videoBuffer={videoBufferDepth} catchupLagMs={catchupLagMs} maxCatchupPulls={maxCatchupPulls}");
            _ = hardwareDeviceType; // suppress unused-variable warning (--hw flag kept for backward compat but no longer used)

            if (_decoder.VideoChannels.Count == 0)
                throw new InvalidOperationException("No video streams in file.");

            var channel = _decoder.VideoChannels[0];
            _activeChannel = channel;
            var srcFmt = channel.SourceFormat;
            var hint = channel as IVideoColorMatrixHint;
            var hintedRange = hint?.SuggestedYuvColorRange ?? YuvColorRange.Auto;
            var hintedMatrix = hint?.SuggestedYuvColorMatrix ?? YuvColorMatrix.Auto;
            var resolvedRange = YuvAutoPolicy.ResolveRange(hintedRange);
            var resolvedMatrix = YuvAutoPolicy.ResolveMatrix(hintedMatrix, srcFmt.Width, srcFmt.Height);
            Console.WriteLine(
                $"[MFPlayer.AvaloniaVideoPlayer] yuvPolicy hint[{RangeLabel(hintedRange)}/{MatrixLabel(hintedMatrix)}] " +
                $"resolved[{RangeLabel(resolvedRange)}/{MatrixLabel(resolvedMatrix)}] path=cpu-convert-to-rgba");

            _videoOutput.Open(
                title: "MFPlayer - Avalonia Video Player",
                width: srcFmt.Width > 0 ? srcFmt.Width : 1280,
                height: srcFmt.Height > 0 ? srcFmt.Height : 720,
                format: srcFmt);

            _router = new AVRouter();
            var epId = _router.RegisterEndpoint(_videoOutput);
            _router.SetClock(_videoOutput.Clock);
            var inputId = _router.RegisterVideoInput(channel);
            _router.CreateRoute(inputId, epId);

            _decoder.Start();
            await _videoOutput.StartAsync();
            await _router.StartAsync();
            _started = true;

            StartDiagnostics();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MFPlayer.AvaloniaVideoPlayer] startup failed: {ex.Message}");
            Close();
        }
    }

    private void StartDiagnostics()
    {
        _diagCts?.Cancel();
        _diagCts?.Dispose();

        _diagCts = new CancellationTokenSource();
        var ct = _diagCts.Token;
        double expectedFps = _videoOutput.OutputFormat.FrameRate > 0 ? _videoOutput.OutputFormat.FrameRate : 30.0;

        _diagTask = Task.Run(async () =>
        {
            AvaloniaOpenGlVideoOutput.DiagnosticsSnapshot? prevOut = null;
            TimeSpan? prevClock = null;
            TimeSpan? prevSrc = null;
            long prevWallTicks = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var outSnap = _videoOutput.GetDiagnosticsSnapshot();
                var clockNow = _videoOutput.Clock.Position;
                var srcNow = _activeChannel?.Position ?? TimeSpan.Zero;
                long wallNowTicks = Stopwatch.GetTimestamp();

                if (prevOut.HasValue)
                {
                    var o0 = prevOut.Value;
                    var o1 = outSnap;

                    long renderDelta = o1.RenderCalls - o0.RenderCalls;
                    long presentDelta = o1.PresentedFrames - o0.PresentedFrames;
                    long blackDelta = o1.BlackFrames - o0.BlackFrames;
                    long exDelta = o1.RenderExceptions - o0.RenderExceptions;
                    long uploadDelta = o1.TextureUploads - o0.TextureUploads;
                    long reuseDelta = o1.TextureReuseDraws - o0.TextureReuseDraws;
                    long catchupDelta = o1.CatchupSkips - o0.CatchupSkips;

                    string speedMark = presentDelta < Math.Max(1, (long)Math.Round(expectedFps * 0.75)) ? " slow" : "";
                    string exMark = exDelta > 0 ? " ex" : "";

                    double driftMs = (clockNow - srcNow).TotalMilliseconds;
                    string rtfClockText = "n/a";
                    string rtfSrcText = "n/a";

                    if (prevClock.HasValue && prevSrc.HasValue && prevWallTicks > 0)
                    {
                        double wallDeltaSeconds = (wallNowTicks - prevWallTicks) / (double)Stopwatch.Frequency;
                        if (wallDeltaSeconds > 0)
                        {
                            double clockDeltaSeconds = (clockNow - prevClock.Value).TotalSeconds;
                            double srcDeltaSeconds = (srcNow - prevSrc.Value).TotalSeconds;
                            rtfClockText = (clockDeltaSeconds / wallDeltaSeconds).ToString("F3");
                            rtfSrcText = (srcDeltaSeconds / wallDeltaSeconds).ToString("F3");
                        }
                    }

                    Console.WriteLine(
                        $"[vstats] clock={Fmt(clockNow)} src={Fmt(srcNow)} " +
                        $"fps={presentDelta,3}/{expectedFps,5:F1} r={renderDelta,4} p={presentDelta,4} b={blackDelta,3} " +
                        $"up={uploadDelta,4} reuse={reuseDelta,4} catchup={catchupDelta,3} ep=n/a ex={exDelta,2} " +
                        $"driftMs={driftMs,7:F1} rtf={rtfClockText} srcRtf={rtfSrcText}{speedMark}{exMark}");
                }

                prevOut = outSnap;
                prevClock = clockNow;
                prevSrc = srcNow;
                prevWallTicks = wallNowTicks;
            }
        }, ct);
    }

    private static string Fmt(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        int hours = (int)ts.TotalHours;
        return hours > 0
            ? $"{hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_shutdown) return;
        _shutdown = true;

        try
        {
            _diagCts?.Cancel();
            if (_diagTask != null)
                await _diagTask;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MFPlayer.AvaloniaVideoPlayer] diagnostics stop failed: {ex.Message}");
        }
        finally
        {
            _diagCts?.Dispose();
            _diagCts = null;
            _diagTask = null;
        }

        try
        {
            if (_started)
                await _videoOutput.StopAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MFPlayer.AvaloniaVideoPlayer] stop failed: {ex.Message}");
        }

        _decoder?.Dispose();
        _router?.Dispose();
        _router = null;
        _videoOutput.Dispose();
    }

    private static string? GetFilePathFromArgs(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;
            if (arg.StartsWith("-"))
                continue;
            return arg.Trim('"', ' ');
        }

        return null;
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(option.Length + 1)..].Trim('"', ' ');

            if (arg.Equals(option, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim('"', ' ');
        }

        return null;
    }

    private static int GetIntOptionValue(string[] args, string option, int defaultValue)
    {
        var valueText = GetOptionValue(args, option);
        return int.TryParse(valueText, out int parsed) ? parsed : defaultValue;
    }

    private static bool HasOption(string[] args, string option)
    {
        foreach (var arg in args)
            if (arg.Equals(option, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
