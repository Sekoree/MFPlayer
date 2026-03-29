using S.Media.Core.Audio;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.SDL3;
using S.Media.PortAudio.Engine;
using SDL3;

namespace TestShared;

/// <summary>
/// Common CLI arguments parsed from test app command lines.
/// </summary>
public sealed record CommonTestArgs
{
    public string? Input { get; init; }
    public double Seconds { get; init; } = 10;
    public string? HostApi { get; init; }
    public int DeviceIndex { get; init; } = -1;
    public bool ListDevices { get; init; }
    public bool ListHostApis { get; init; }
    public bool ShowHelp { get; init; }

    /// <summary>
    /// Parses standard test app arguments: --input, --seconds, --host-api, --device-index,
    /// --list-devices, --list-host-apis, --help / -h.
    /// </summary>
    public static CommonTestArgs Parse(string[] args) => new()
    {
        Input = TestHelpers.GetArg(args, "--input") ?? Environment.GetEnvironmentVariable("SMEDIA_TEST_INPUT"),
        Seconds = double.TryParse(TestHelpers.GetArg(args, "--seconds"), out var s) && s > 0 ? s : 10,
        HostApi = TestHelpers.GetArg(args, "--host-api"),
        DeviceIndex = int.TryParse(TestHelpers.GetArg(args, "--device-index"), out var di) ? di : -1,
        ListDevices = args.Contains("--list-devices"),
        ListHostApis = args.Contains("--list-host-apis"),
        ShowHelp = args.Contains("--help") || args.Contains("-h"),
    };
}

/// <summary>
/// Shared utility methods for test/demo applications to reduce boilerplate.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Resolves a CLI input string to an absolute URI. Handles both file paths and URLs.
    /// </summary>
    public static string? ResolveUri(string input)
    {
        if (Uri.TryCreate(input, UriKind.Absolute, out var u) &&
            !string.IsNullOrWhiteSpace(u.Scheme) && u.Scheme != "file")
        {
            return u.AbsoluteUri;
        }

        var path = Path.GetFullPath(input);
        return File.Exists(path) ? new Uri(path).AbsoluteUri : null;
    }

    /// <summary>
    /// Gets the value of a named CLI argument (e.g. <c>GetArg(args, "--input")</c>).
    /// </summary>
    public static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    /// <summary>
    /// Initializes a PortAudio engine and creates an output device.
    /// Returns the engine and output, or throws on failure.
    /// </summary>
    public static (PortAudioEngine Engine, IAudioOutput Output) InitAudioOutput(
        string? hostApi = null,
        int deviceIndex = -1)
    {
        var engine = new PortAudioEngine();
        var init = engine.Initialize(new AudioEngineConfig { PreferredHostApi = hostApi });
        if (init != MediaResult.Success)
        {
            engine.Dispose();
            throw new InvalidOperationException($"Audio engine init failed: {init}");
        }

        var start = engine.Start();
        if (start != MediaResult.Success)
        {
            engine.Dispose();
            throw new InvalidOperationException($"Audio engine start failed: {start}");
        }

        var createOut = engine.CreateOutputByIndex(deviceIndex, out var output);
        if (createOut != MediaResult.Success || output is null)
        {
            engine.Stop();
            engine.Dispose();
            throw new InvalidOperationException($"Audio output creation failed: {createOut}");
        }

        var outStart = output.Start(new AudioOutputConfig());
        if (outStart != MediaResult.Success)
        {
            engine.Stop();
            engine.Dispose();
            throw new InvalidOperationException($"Audio output start failed: {outStart}");
        }

        return (engine, output);
    }

    /// <summary>
    /// Initializes an SDL3 video view with common defaults.
    /// Returns the initialized view, or throws on failure.
    /// </summary>
    public static SDL3VideoView InitVideoView(
        string title = "MFPlayer Test",
        int width = 1280,
        int height = 720,
        VideoOutputConfig? videoConfig = null)
    {
        var view = new SDL3VideoView();
        var viewInit = view.Initialize(new SDL3VideoViewOptions
        {
            Width = width,
            Height = height,
            WindowTitle = title,
            WindowFlags = SDL.WindowFlags.Resizable,
            ShowOnInitialize = true,
            BringToFrontOnShow = true,
            PreserveAspectRatio = true,
        });

        if (viewInit != MediaResult.Success)
        {
            view.Dispose();
            throw new InvalidOperationException($"SDL3 view init failed: {viewInit}");
        }

        var viewStart = view.Start(videoConfig ?? new VideoOutputConfig());
        if (viewStart != MediaResult.Success)
        {
            view.Dispose();
            throw new InvalidOperationException($"SDL3 view start failed: {viewStart}");
        }

        return view;
    }

    /// <summary>
    /// Runs a loop until <paramref name="seconds"/> elapse or Ctrl+C is pressed.
    /// Calls <paramref name="tick"/> each iteration and <paramref name="statusCallback"/>
    /// once per second. Returns when the loop exits.
    /// </summary>
    public static void RunWithDeadline(
        double seconds,
        Func<bool> tick,
        Action? statusCallback = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var lastStatus = DateTime.UtcNow;
        var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (!tick())
            {
                break;
            }

            if (statusCallback is not null && (DateTime.UtcNow - lastStatus).TotalSeconds >= 1)
            {
                statusCallback();
                lastStatus = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Lists audio host APIs and/or output devices on the given engine.
    /// </summary>
    public static int ListAudioRuntime(string? hostApi, bool listApis, bool listDevices)
    {
        using var engine = new PortAudioEngine();
        var init = engine.Initialize(new AudioEngineConfig { PreferredHostApi = hostApi });
        if (init != MediaResult.Success)
        {
            Console.Error.WriteLine($"Engine init failed: {init}");
            return 1;
        }

        if (listApis)
        {
            Console.WriteLine("Host APIs:");
            foreach (var api in engine.GetHostApis())
            {
                Console.WriteLine($"  {(api.IsDefault ? "*" : " ")} {api.Id} ({api.Name}) devices={api.DeviceCount}");
            }
        }

        if (listDevices)
        {
            _ = engine.Start();
            Console.WriteLine("Output devices:");
            var devices = engine.GetOutputDevices();
            for (var i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($"  [{i}] {devices[i].Name} (host={devices[i].HostApi})");
            }

            engine.Stop();
        }

        _ = engine.Terminate();
        return 0;
    }
}

