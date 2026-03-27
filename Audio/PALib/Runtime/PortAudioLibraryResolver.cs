using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PALib.Runtime;

public static class PortAudioLibraryResolver
{
    private static readonly Lock Gate = new();
    private static bool _installed;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Installs an assembly-local DllImport resolver that probes the known PortAudio library names.
    /// Call this once at app startup before first use of <c>PALib.Native</c>.
    /// </summary>
    public static void Install(ILoggerFactory? loggerFactory = null)
    {
        lock (Gate)
        {
            if (_installed)
                return;

            _logger = loggerFactory?.CreateLogger("PALib.Runtime") ?? NullLogger.Instance;
            NativeLibrary.SetDllImportResolver(typeof(PortAudioLibraryResolver).Assembly, ResolveLibrary);
            _installed = true;
        }
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, PortAudioLibraryNames.Default, StringComparison.Ordinal))
            return nint.Zero;

        foreach (var candidate in GetCandidates())
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                _logger.LogDebug("Loaded PortAudio native library candidate '{Candidate}'.", candidate);
                return handle;
            }
        }

        _logger.LogDebug("Unable to load PortAudio using PALib fallback candidates.");
        return nint.Zero;
    }

    private static string[] GetCandidates()
    {
        if (OperatingSystem.IsWindows())
            return PortAudioLibraryNames.WindowsCandidates;
        if (OperatingSystem.IsMacOS())
            return PortAudioLibraryNames.MacCandidates;
        return PortAudioLibraryNames.LinuxCandidates;
    }
}
