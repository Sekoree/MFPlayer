// ═══════════════════════════════════════════════════════════════════════════════
// MFPlayer.VideoPlayer
//   1. Enter a video file path
//   2. Opens an SDL3 window and plays the video
//   3. Close the window, press Enter, or Ctrl+C to stop
// ═══════════════════════════════════════════════════════════════════════════════

using FFmpeg.AutoGen;
using S.Media.Core.Media;
using S.Media.FFmpeg;
using S.Media.SDL3;

Console.WriteLine("╔═══════════════════════════════╗");
Console.WriteLine("║   MFPlayer  —  Video Player   ║");
Console.WriteLine("╚═══════════════════════════════╝\n");

ffmpeg.RootPath = "/lib";

// ── 1. Enter file path ───────────────────────────────────────────────────────

Console.Write("Video file path: ");
string filePath = (Console.ReadLine() ?? "").Trim('"', ' ');

if (!File.Exists(filePath))
{
    Console.WriteLine("File not found.");
    return;
}

// ── 2. Open decoder ──────────────────────────────────────────────────────────

Console.Write("Opening decoder… ");
FFmpegDecoder decoder;
try
{
    decoder = FFmpegDecoder.Open(filePath);
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED\n  {ex.Message}");
    return;
}

if (decoder.VideoChannels.Count == 0)
{
    Console.WriteLine("No video streams in file.");
    decoder.Dispose();
    return;
}

using (decoder)
{
    var videoChannel = decoder.VideoChannels[0];
    var srcFmt       = videoChannel.SourceFormat;

    Console.WriteLine("OK");
    Console.WriteLine($"  Video: {srcFmt}");

    // ── 3. Open video output ─────────────────────────────────────────────

    Console.Write("Creating SDL3 video output… ");
    using var videoOutput = new SDL3VideoOutput();
    try
    {
        videoOutput.Open("MFPlayer — Video Player",
            srcFmt.Width  > 0 ? srcFmt.Width  : 1280,
            srcFmt.Height > 0 ? srcFmt.Height : 720,
            srcFmt);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED\n  {ex.Message}");
        return;
    }
    Console.WriteLine("OK");

    // ── 4. Wire up ───────────────────────────────────────────────────────

    videoOutput.Mixer.AddChannel(videoChannel);
    videoOutput.Mixer.SetActiveChannel(videoChannel.Id);

    // ── 5. Auto-stop on window close ─────────────────────────────────────

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    videoOutput.WindowClosed += () =>
    {
        Console.WriteLine("\n[Window closed]");
        if (!cts.IsCancellationRequested) cts.Cancel();
    };

    // ── 6. Start playback ────────────────────────────────────────────────

    decoder.Start();
    await videoOutput.StartAsync();

    Console.WriteLine($"\nPlaying: {Path.GetFileName(filePath)}");
    Console.WriteLine("Close the window, press [Enter], or [Ctrl+C] to stop.\n");

    // Wait for Ctrl+C, Enter, window close, or end of file.
    _ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });
    try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); }
    catch (OperationCanceledException) { }

    // ── 7. Stop ──────────────────────────────────────────────────────────

    Console.Write("\nStopping… ");
    await videoOutput.StopAsync();
    Console.WriteLine("Done.");
}

