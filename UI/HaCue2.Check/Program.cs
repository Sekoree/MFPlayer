using HaCue2.Core.Serialization;
using HaCue2.Core.Validation;

namespace HaCue2.Check;

/// <summary>
/// Runs Project status over a <c>.hacue2proj</c> and exits non-zero while errors remain.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately dependency-free beyond the project model: no audio backend, no windowing. That is what
/// lets it gate a committed fixture project in CI, where enumerating devices is impossible and would
/// otherwise report every interface as missing.
/// </para>
/// <para>
/// Consequently the DEVICE half reports as "not checked" here, not as passing. The checks that mean
/// the same thing on any machine — dangling references, unpatched outputs, missing media — are the
/// ones this is for.
/// </para>
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var path = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("usage: hacue2-check <project" + HaCueProjectFile.Extension + "> [--json]");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"hacue2-check: {path} does not exist.");
            return 2;
        }

        try
        {
            var project = await HaCueProjectFile.LoadAsync(path).ConfigureAwait(false);
            var report = ProjectStatus.Run(project, path);

            Console.Write(args.Contains("--json") ? report.ToJson() : report.ToText());
            Console.WriteLine();

            return report.ExitCode;
        }
        catch (HaCueProjectFormatException error)
        {
            // 2, not 1: "this file is not readable" is a different answer from "this show has errors",
            // and a script that retries on 1 should not retry on a corrupt file.
            Console.Error.WriteLine($"hacue2-check: {error.Message}");
            return 2;
        }
    }
}
