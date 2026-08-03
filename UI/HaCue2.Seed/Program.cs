using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;
using HaCue2.Machine;

namespace HaCue2.Seed;

/// <summary>
/// Writes a <c>.hacue2proj</c> built from real media.
/// </summary>
/// <remarks>
/// <para>
/// Usage: <c>hacue2-seed --out show.hacue2proj [--title "Name"] [--max-mb 400] [--audio N]
/// [--video N] &lt;directory&gt;…</c>
/// </para>
/// <para>
/// The project it writes exercises every cue kind against files that actually exist, so opening it
/// puts real durations in the Len column, real track lists in the inspector, and a real failure in
/// Project status if something has moved since. It also leaves one logical output deliberately
/// unpatched, so the status pass has a genuine error to report rather than a clean bill nobody
/// learns anything from.
/// </para>
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.Error.WriteLine(
                "usage: hacue2-seed [--out <file>] [--title <name>] [--max-mb <n>] "
                + "[--audio <n>] [--video <n>] <directory>...");
            return 2;
        }

        var directories = args.Where(argument => !argument.StartsWith('-')).ToList();

        // A flag's VALUE is not a directory. Dropping it here rather than filtering by existence
        // means a typo'd path is reported instead of silently scanned as nothing.
        foreach (var flag in new[] { "--out", "--title", "--max-mb", "--audio", "--video" })
        {
            if (Value(args, flag) is { } value)
                directories.Remove(value);
        }

        if (directories.Count == 0)
        {
            Console.Error.WriteLine("hacue2-seed: name at least one directory to scan.");
            return 2;
        }

        var missing = directories.Where(directory => !Directory.Exists(directory)).ToList();

        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"hacue2-seed: not a directory: {string.Join(", ", missing)}");
            return 2;
        }

        var title = Value(args, "--title") ?? "Library sample";
        var output = Value(args, "--out") ?? Path.Combine(
            Directory.GetCurrentDirectory(), "library-sample" + HaCueProjectFile.Extension);

        var maxBytes = (long)Number(args, "--max-mb", 400) * 1024 * 1024;
        var audioTake = Number(args, "--audio", 8);
        var videoTake = Number(args, "--video", 4);

        // At least a megabyte: a library is full of one-second stingers and stray artwork, and a cue
        // list of those demonstrates nothing about playback.
        const long MinBytes = 1024 * 1024;

        var audio = directories
            .SelectMany(directory => LibraryScan.Find(
                directory, LibraryScan.AudioExtensions, maxBytes, audioTake, MinBytes))
            .Take(audioTake)
            .ToList();

        var video = directories
            .SelectMany(directory => LibraryScan.Find(
                directory, LibraryScan.VideoExtensions, maxBytes, videoTake, MinBytes))
            .Take(videoTake)
            .ToList();

        if (audio.Count == 0 && video.Count == 0)
        {
            Console.Error.WriteLine(
                $"hacue2-seed: found no media between 1 MB and {maxBytes / 1024 / 1024} MB under "
                + string.Join(", ", directories));
            return 1;
        }

        var root = LibraryScan.CommonRoot(directories) ?? "";

        var project = LibrarySeeder.Build(new LibrarySeed(title, root, audio, video));
        await HaCueProjectFile.SaveAsync(project, output).ConfigureAwait(false);

        Console.WriteLine($"wrote {output}");
        Console.WriteLine(
            $"  {audio.Count} audio · {video.Count} video · media root {(root.Length == 0 ? "(none)" : root)}");
        Console.WriteLine($"  {project.AllCues().Count()} cues across {project.CueLists.Count} lists");

        // Run the same pass hacue2-check runs, so the tool cannot hand back a project its own status
        // screen would reject — and so the deliberate unpatched output is visible rather than a
        // surprise on first open.
        var report = ProjectStatus.Run(project, output);
        Console.WriteLine();
        Console.Write(report.ToText());

        return 0;
    }

    private static string? Value(string[] args, string flag)
    {
        var at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int Number(string[] args, string flag, int fallback) =>
        int.TryParse(Value(args, flag), out var value) && value > 0 ? value : fallback;
}
