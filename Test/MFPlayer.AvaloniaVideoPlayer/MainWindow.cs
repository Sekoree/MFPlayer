using System.Diagnostics;
using Avalonia.Controls;
using FFmpeg.AutoGen;
using S.Media.Avalonia;
using S.Media.Core.Media;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace MFPlayer.AvaloniaVideoPlayer;

public sealed class MainWindow : Window
{
    private readonly string[] _args;
    private readonly AvaloniaOpenGlVideoOutput _videoOutput;
    private FFmpegDecoder? _decoder;
    private bool _started;
    private bool _shutdown;

    private CancellationTokenSource? _diagCts;
    private Task? _diagTask;
    private IVideoChannel? _activeChannel;
    private VideoMixer? _videoMixer;

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
            ffmpeg.RootPath = "/lib";

            string? filePath = GetFilePathFromArgs(_args);
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new InvalidOperationException("Pass a valid video file path as the first command-line argument.");

            _decoder = FFmpegDecoder.Open(filePath, new FFmpegDecoderOptions
            {
                EnableAudio = false,
                EnableVideo = true,
                DecoderThreadCount = 0, // FFmpeg auto threading is typically best for 4K60 software decode.
                VideoTargetPixelFormat = PixelFormat.Rgba32
            });

            if (_decoder.VideoChannels.Count == 0)
                throw new InvalidOperationException("No video streams in file.");

            var channel = _decoder.VideoChannels[0];
            _activeChannel = channel;
            var srcFmt = channel.SourceFormat;

            _videoOutput.Open(
                title: "MFPlayer - Avalonia Video Player",
                width: srcFmt.Width > 0 ? srcFmt.Width : 1280,
                height: srcFmt.Height > 0 ? srcFmt.Height : 720,
                format: srcFmt);

            _videoOutput.Mixer.AddChannel(channel);
            _videoOutput.Mixer.SetActiveChannel(channel.Id);
            _videoMixer = _videoOutput.Mixer as VideoMixer;

            _decoder.Start();
            await _videoOutput.StartAsync();
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
            VideoMixer.DiagnosticsSnapshot? prevMix = null;
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
                var mixSnap = _videoMixer?.GetDiagnosticsSnapshot();
                var clockNow = _videoOutput.Clock.Position;
                var srcNow = _activeChannel?.Position ?? TimeSpan.Zero;
                long wallNowTicks = Stopwatch.GetTimestamp();

                if (prevOut.HasValue && mixSnap.HasValue && prevMix.HasValue)
                {
                    var o0 = prevOut.Value;
                    var o1 = outSnap;
                    var m0 = prevMix.Value;
                    var m1 = mixSnap.Value;

                    long renderDelta = o1.RenderCalls - o0.RenderCalls;
                    long presentDelta = o1.PresentedFrames - o0.PresentedFrames;
                    long blackDelta = o1.BlackFrames - o0.BlackFrames;
                    long exDelta = o1.RenderExceptions - o0.RenderExceptions;
                    long holdDelta = m1.Held - m0.Held;
                    long dropDelta = m1.Dropped - m0.Dropped;
                    long pullDelta = m1.PullHits - m0.PullHits;
                    long pullAttemptDelta = m1.PullAttempts - m0.PullAttempts;

                    string speedMark = presentDelta < Math.Max(1, (long)Math.Round(expectedFps * 0.75)) ? " slow" : "";
                    string dropMark = dropDelta > 0 ? " drop" : "";
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
                        $"held={holdDelta,4} drop={dropDelta,3} pull={pullDelta,3}/{pullAttemptDelta,3} ex={exDelta,2} " +
                        $"driftMs={driftMs,7:F1} rtf={rtfClockText} srcRtf={rtfSrcText}{speedMark}{dropMark}{exMark}");
                }

                prevOut = outSnap;
                prevMix = mixSnap;
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
}
