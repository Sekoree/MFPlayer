using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FFmpeg.AutoGen;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.FFmpeg.Config;
using S.Media.FFmpeg.Media;
using S.Media.FFmpeg.Sources;
using S.Media.OpenGL.Avalonia.Controls;
using S.Media.OpenGL.Avalonia.Output;
using S.Media.PortAudio.Engine;

namespace VideoStress;

public partial class MainWindow : Window
{
    private const double SeekStepSeconds = 5.0;

    private readonly Lock _seekGate = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly CancellationTokenSource _playbackCts = new();

    private FFMediaItem? _mediaItem;
    private FFVideoSource? _videoSource;
    private AvaloniaVideoOutput[] _outputs = [];
    private AvaloniaOpenGLHostControl[] _hosts = [];
    private Task? _playbackLoopTask;
    private bool _hudEnabled;
    private bool _isPaused;
    private bool _isDisposed;

    public MainWindow()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Opened += (_, _) => InitializePlayback();
        Closing += (_, _) => DisposePlayback();

        var summary = $"Wired modules: {nameof(FFMediaItem)}, {nameof(AvaloniaVideoOutput)}, {nameof(PortAudioEngine)}";

        SummaryText.Text = summary;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Space:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(-SeekStepSeconds);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(SeekStepSeconds);
                e.Handled = true;
                break;
            case Key.Home:
                SeekAbsolute(0);
                e.Handled = true;
                break;
            case Key.End:
                SeekToEnd();
                e.Handled = true;
                break;
            case Key.H:
                ToggleHud();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void InitializePlayback()
    {
        var inputUri = ResolveInputUri();
        if (inputUri is null)
        {
            StatusText.Text = "No input found. Pass a file path as arg[0] or set VIDEOSTRESS_INPUT.";
            return;
        }

        ConfigureFfmpegRuntime();

        try
        {
            _mediaItem = new FFMediaItem(
                new FFmpegOpenOptions
                {
                    InputUri = inputUri,
                    OpenAudio = false,
                    OpenVideo = true,
                    UseSharedDecodeContext = true,
                },
                new FFmpegDecodeOptions
                {
                    DecodeThreadCount = 0,
                    MaxQueuedPackets = 32,
                });

            _videoSource = _mediaItem.VideoSource;
            if (_videoSource is null)
            {
                StatusText.Text = "FFMediaItem did not expose a video source.";
                return;
            }

            if (_videoSource.Start() != MediaResult.Success)
            {
                StatusText.Text = "Failed to start video source.";
                return;
            }

            _outputs = [new AvaloniaVideoOutput(), new AvaloniaVideoOutput(), new AvaloniaVideoOutput(), new AvaloniaVideoOutput()];
            foreach (var output in _outputs)
            {
                _ = output.Start(new VideoOutputConfig());
            }

            _hosts = _outputs.Select(o => new AvaloniaOpenGLHostControl(o.Output)).ToArray();
            AttachHosts();

            _playbackLoopTask = Task.Run(() => PlaybackLoop(_playbackCts.Token));
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Startup failed: {ex.Message}";
        }
    }

    private void AttachHosts()
    {
        ViewGrid.Children.Clear();
        for (var i = 0; i < _hosts.Length; i++)
        {
            var host = _hosts[i];
            host.EnableHudOverlay = _hudEnabled;
            Grid.SetColumn(host, i % 2);
            Grid.SetRow(host, i / 2);
            ViewGrid.Children.Add(host);
        }
    }

    private async Task PlaybackLoop(CancellationToken cancellationToken)
    {
        if (_videoSource is null || _outputs.Length == 0)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_isPaused)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }

            var read = _videoSource.ReadFrame(out var frame);
            if (read != MediaResult.Success)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusText.Text = $"ReadFrame failed: code={read}, semantic={ErrorCodeRanges.ResolveSharedSemantic(read)}";
                });
                await Task.Delay(20, cancellationToken);
                continue;
            }

            try
            {
                foreach (var output in _outputs)
                {
                    _ = output.PushFrame(frame, frame.PresentationTime);
                }
            }
            finally
            {
                frame.Dispose();
            }

            var fps = _videoSource.StreamInfo.FrameRate.GetValueOrDefault(30);
            var delayMs = fps > 0 ? Math.Clamp((int)Math.Round(1000.0 / fps), 1, 33) : 16;
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    private void TogglePlayPause()
    {
        _isPaused = !_isPaused;
        RefreshStatus();
    }

    private void SeekRelative(double deltaSeconds)
    {
        if (_videoSource is null)
        {
            return;
        }

        lock (_seekGate)
        {
            var target = Math.Max(0, _videoSource.PositionSeconds + deltaSeconds);
            _ = _videoSource.Seek(target);
        }
    }

    private void SeekAbsolute(double positionSeconds)
    {
        if (_videoSource is null)
        {
            return;
        }

        lock (_seekGate)
        {
            _ = _videoSource.Seek(Math.Max(0, positionSeconds));
        }
    }

    private void SeekToEnd()
    {
        if (_videoSource is null)
        {
            return;
        }

        var duration = _videoSource.DurationSeconds;
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return;
        }

        SeekAbsolute(Math.Max(0, duration - 0.001));
    }

    private void ToggleHud()
    {
        _hudEnabled = !_hudEnabled;
        foreach (var host in _hosts)
        {
            host.EnableHudOverlay = _hudEnabled;
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_videoSource is null)
        {
            return;
        }

        var state = _isPaused ? "Paused" : "Playing";
        var pos = _videoSource.PositionSeconds;
        var dur = _videoSource.DurationSeconds;
        var durText = double.IsFinite(dur) ? dur.ToString("0.###") : "live/unknown";
        StatusText.Text = $"{state} | pos={pos:0.###}s / {durText}s | frame={_videoSource.CurrentFrameIndex} | hud={(_hudEnabled ? "on" : "off")}";
    }

    private void DisposePlayback()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _statusTimer.Stop();
        _playbackCts.Cancel();

        try
        {
            _playbackLoopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // best-effort cleanup path
        }

        if (_videoSource is not null)
        {
            _ = _videoSource.Stop();
        }

        foreach (var output in _outputs)
        {
            _ = output.Stop();
            output.Dispose();
        }

        _outputs = [];
        _hosts = [];
        _mediaItem?.Dispose();
        _playbackCts.Dispose();
    }

    private static string? ResolveInputPath()
    {
        var argPath = Program.LaunchArgs.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (!string.IsNullOrWhiteSpace(argPath) && File.Exists(argPath))
        {
            return argPath;
        }

        var envCandidates = new[]
        {
            Environment.GetEnvironmentVariable("VIDEOSTRESS_INPUT"),
            Environment.GetEnvironmentVariable("VIDEOTEST_INPUT"),
        };

        foreach (var candidate in envCandidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveInputUri()
    {
        var path = ResolveInputPath();
        if (path is null)
        {
            return null;
        }

        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private static void ConfigureFfmpegRuntime()
    {
        var envRoot = Environment.GetEnvironmentVariable("SMEDIA_FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            ffmpeg.RootPath = envRoot;
        }
    }
}
