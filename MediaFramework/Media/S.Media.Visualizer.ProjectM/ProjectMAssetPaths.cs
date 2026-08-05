using ProjectMLib.Runtime;

namespace S.Media.Visualizer.ProjectM;

/// <summary>Resolves the preset pack paired with MFPlayer's patched projectM native.</summary>
public static class ProjectMAssetPaths
{
    /// <summary>
    /// Finds the default Milkdrop pack from an explicit native override, app-local deployment, or
    /// repository development build. Null leaves projectM on its built-in idle preset.
    /// </summary>
    public static string? DefaultPresetDirectory()
    {
        if (PresetSiblingOfNativeOverride() is { } overridden)
            return overridden;

        // Backward compatibility with older HaViz bundles, which placed these directly beside the exe.
        var direct = Path.Combine(AppContext.BaseDirectory, "presets");
        if (ContainsPresets(direct))
            return direct;

        if (ProjectMLibraryResolver.TryFindDevBuildRoot() is { } root)
        {
            var deployed = Path.Combine(root, "presets");
            if (ContainsPresets(deployed))
                return deployed;
        }

        return null;
    }

    private static string? PresetSiblingOfNativeOverride()
    {
        var configured = Environment.GetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride);
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        try
        {
            var nativeDirectory = File.Exists(configured)
                ? Path.GetDirectoryName(Path.GetFullPath(configured))
                : Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
            var installRoot = nativeDirectory is null
                ? null
                : Directory.GetParent(nativeDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
            var presets = installRoot is null ? null : Path.Combine(installRoot, "presets");
            return presets is not null && ContainsPresets(presets) ? presets : null;
        }
        catch (Exception failure) when (
            failure is ArgumentException or IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool ContainsPresets(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        try
        {
            return Directory.EnumerateFiles(directory, "*.milk", SearchOption.AllDirectories).Any();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
