using HaViz.Desktop;
using Xunit;

namespace HaViz.Core.Tests;

public sealed class DesktopSettingsDurabilityTests
{
    [Fact]
    public void CorruptMainFileFallsBackToTheOneDeepKnownGoodBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haviz-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "desktop.json");
        try
        {
            new HaVizDesktopSettings { InputDeviceName = "first", InputChannelIndices = [1, 3] }
                .SaveTo(path);
            new HaVizDesktopSettings { InputDeviceName = "second", InputChannelIndices = [2] }
                .SaveTo(path);

            File.WriteAllText(path, "{ torn write");
            var recovered = HaVizDesktopSettings.LoadFrom(path);

            Assert.Equal("first", recovered.InputDeviceName);
            Assert.Equal([1, 3], recovered.InputChannelIndices);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedAtomicReplacePreservesTheCurrentSettingsAndCleansItsTempFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haviz-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "desktop.json");
        try
        {
            new HaVizDesktopSettings { OutputDeviceName = "known-good", PlayOnDevice = true }
                .SaveTo(path);

            // File.Replace must create this backup path. Occupying it with a directory injects a
            // deterministic replacement failure after the new JSON has been fully flushed.
            Directory.CreateDirectory(path + ".bak");
            new HaVizDesktopSettings { OutputDeviceName = "must-not-win" }.SaveTo(path);

            var loaded = HaVizDesktopSettings.LoadFrom(path);
            Assert.Equal("known-good", loaded.OutputDeviceName);
            Assert.True(loaded.PlayOnDevice);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
