using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaViz.Desktop;

/// <summary>
/// Per-box persisted device choices (HaViz-Clocking doc P4: a show machine's "default" input moves
/// with USB churn, so the operator's picks must survive restarts). Devices are matched by NAME on
/// load - PortAudio ids are enumeration indexes and reshuffle when hardware comes and goes.
/// Engine/NDI settings are deliberately not persisted here (they are per-show, not per-box).
/// </summary>
public sealed record HaVizDesktopSettings
{
    /// <summary>Line-in capture device name; null = pick the system default input.</summary>
    public string? InputDeviceName { get; init; }

    /// <summary>0-based capture channel indices last used with <see cref="InputDeviceName"/>.</summary>
    public int[] InputChannelIndices { get; init; } = [];

    /// <summary>Local-monitor output device name; null = system default.</summary>
    public string? OutputDeviceName { get; init; }

    /// <summary>Whether local monitoring ("Play on this device") was on.</summary>
    public bool PlayOnDevice { get; init; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HaViz", "desktop.json");

    /// <summary>Best-effort load; a missing or unreadable file is a fresh default (never throws).
    /// F-23: an unreadable MAIN file first falls back to the one-deep <c>.bak</c> the atomic save
    /// keeps, so a write torn by power loss costs one save, not the box's device picks.</summary>
    public static HaVizDesktopSettings Load()
        => LoadFrom(SettingsPath);

    /// <summary>Path-injected core for recovery/fault tests; production uses <see cref="Load()"/>.</summary>
    internal static HaVizDesktopSettings LoadFrom(string path)
    {
        return TryRead(path) ?? TryRead(path + ".bak") ?? new HaVizDesktopSettings();

        static HaVizDesktopSettings? TryRead(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize(stream, HaVizDesktopSettingsJsonContext.Default.HaVizDesktopSettings);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Best-effort save; settings persistence must never take the app down (never throws).
    /// F-23: writes through a unique flushed temp file and an atomic replace that keeps the previous
    /// good file as <c>.bak</c> - the old bare <c>File.Create</c> could tear the ONLY copy.</summary>
    public void Save()
        => SaveTo(SettingsPath);

    /// <summary>Path-injected atomic writer for recovery/fault tests; production uses <see cref="Save()"/>.</summary>
    internal void SaveTo(string path)
    {
        var temp = (string?)null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, HaVizDesktopSettingsJsonContext.Default.HaVizDesktopSettings);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temp, path, path + ".bak");
            else
                File.Move(temp, path);
            temp = null;
        }
        catch (Exception)
        {
            // Read-only home / full disk - the box just falls back to defaults next start.
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (Exception) { }
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HaVizDesktopSettings))]
internal sealed partial class HaVizDesktopSettingsJsonContext : JsonSerializerContext;
