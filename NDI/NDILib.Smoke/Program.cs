using NDILib;

if (args.Length == 0)
{
    Console.WriteLine("NDILib.Smoke usage:");
    Console.WriteLine("  find [seconds]   Discover sources and print snapshots");
    Console.WriteLine("  recv [seconds]   Connect first source and log first captured frame");
    Console.WriteLine();
    Console.WriteLine("No command provided: exiting without loading native NDI runtime.");
    return;
}

var mode = args[0].ToLowerInvariant();
if (mode is not ("find" or "recv"))
{
    Console.WriteLine($"Unknown command '{args[0]}'.");
    return;
}

var seconds = 5;
if (args.Length > 1 && int.TryParse(args[1], out var parsed) && parsed > 0)
    seconds = parsed;

if (NDIRuntime.Create(out var runtime) is var initResult and not 0)
{
    Console.Error.WriteLine($"NDI runtime initialisation failed (code {initResult}). " +
                            "Is the NDI SDK installed? CPU must support SSE4.2.");
    return;
}

using (runtime)
{
    Console.WriteLine($"NDI runtime version: {NDIRuntime.Version}");

    if (NDIFinder.Create(out var finder) is not 0)
    {
        Console.Error.WriteLine("Failed to create NDI finder.");
        return;
    }

    using (finder)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);

        if (mode == "find")
        {
            while (DateTime.UtcNow < end)
            {
                _ = finder!.WaitForSources(1000);
                var sources = finder.GetCurrentSources();

                Console.WriteLine($"Sources ({sources.Length}):");
                for (var i = 0; i < sources.Length; i++)
                {
                    var source = sources[i];
                    Console.WriteLine($"  {i + 1}. {source.Name} [{source.UrlAddress ?? "n/a"}]");
                }
            }

            return;
        }

        // recv mode
        NdiDiscoveredSource? selected = null;
        while (DateTime.UtcNow < end)
        {
            _ = finder!.WaitForSources(1000);
            var sources = finder.GetCurrentSources();
            if (sources.Length == 0)
            {
                Console.WriteLine("Waiting for sources...");
                continue;
            }

            selected = sources[0];
            break;
        }

        if (selected is null)
        {
            Console.WriteLine("No sources found within the timeout window.");
            return;
        }

        Console.WriteLine($"Connecting to: {selected.Value.Name}");

        if (NDIReceiver.Create(out var receiver) is not 0)
        {
            Console.Error.WriteLine("Failed to create NDI receiver.");
            return;
        }

        using (receiver)
        {
            receiver!.Connect(selected.Value);

            while (DateTime.UtcNow < end)
            {
                using var capture = receiver.CaptureScoped(2000);
                switch (capture.FrameType)
                {
                    case NdiFrameType.None:
                        Console.WriteLine("No data yet...");
                        continue;

                    case NdiFrameType.Video:
                        Console.WriteLine(
                            $"Video: {capture.Video.Xres}x{capture.Video.Yres} " +
                            $"{capture.Video.FourCC} stride={capture.Video.LineStrideInBytes}");
                        return;

                    case NdiFrameType.Audio:
                        Console.WriteLine(
                            $"Audio: {capture.Audio.SampleRate}Hz ch={capture.Audio.NoChannels} " +
                            $"samples={capture.Audio.NoSamples} {capture.Audio.FourCC}");
                        return;

                    case NdiFrameType.Metadata:
                        Console.WriteLine($"Metadata: {capture.Metadata.Data ?? "(null)"}");
                        return;

                    case NdiFrameType.Error:
                        Console.WriteLine("Capture returned Error (source disconnected or receiver error).");
                        return;

                    default:
                        Console.WriteLine($"Frame type: {capture.FrameType}");
                        return;
                }
            }

            Console.WriteLine("No capturable frame arrived before timeout.");
        }
    }
}
