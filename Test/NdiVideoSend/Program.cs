using NdiLib;
using Seko.OwnAudioNET.Video;
using Seko.OwnAudioNET.Video.SDL3;

if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  NdiVideoSend [discover-timeout-seconds]");
    Console.WriteLine();
    Console.WriteLine("Displays the first discovered NDI source in an SDL3 window.");
    return;
}

var discoverTimeoutSeconds = 10;
if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0)
    discoverTimeoutSeconds = parsed;

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

using var output = new VideoSDL();
var windowInitialized = false;
var lastStatus = DateTime.UtcNow;

while (true)
{
    using var capture = receiver.CaptureScoped(1000);
    switch (capture.FrameType)
    {
        case NdiFrameType.None:
            if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 2)
            {
                Console.WriteLine("No frame received yet...");
                lastStatus = DateTime.UtcNow;
            }
            break;

        case NdiFrameType.SourceChange:
        case NdiFrameType.StatusChange:
            Console.WriteLine($"Receiver status: {capture.FrameType}");
            break;

        case NdiFrameType.Video:
        {
            var video = capture.Video;
            if (video.PData == nint.Zero || video.Xres <= 0 || video.Yres <= 0)
                break;

            var stride = video.LineStrideInBytes > 0 ? video.LineStrideInBytes : video.Xres * 4;
            var byteCount = stride * video.Yres;

            unsafe
            {
                var src = new ReadOnlySpan<byte>((void*)video.PData, byteCount);
                using var frame = VideoFrame.CreateExternalRgba32(src, video.Xres, video.Yres, stride);

                if (!windowInitialized)
                {
                    if (!output.Initialize(video.Xres, video.Yres, $"NDI Preview - {selected.Value.Name}", out var error))
                    {
                        Console.WriteLine($"SDL init failed: {error}");
                        return;
                    }

                    output.Start();
                    windowInitialized = true;
                    Console.WriteLine("SDL preview started. Close window or press Escape to exit.");
                }

                _ = output.PushFrame(frame, 0);
            }

            if (windowInitialized && !output.IsRunning)
                break;

            continue;
        }

        case NdiFrameType.Audio:
            // Video preview app: audio is ignored for now.
            break;

        case NdiFrameType.Metadata:
            break;

        case NdiFrameType.Error:
            Console.WriteLine("NDI receiver reported an error.");
            return;
    }

    if (windowInitialized && !output.IsRunning)
        break;
}

output.Stop();

