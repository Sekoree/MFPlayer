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

    /// <summary>Best-effort load; a missing or unreadable file is a fresh default (never throws).</summary>
    public static HaVizDesktopSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(SettingsPath);
            return JsonSerializer.Deserialize(stream, HaVizDesktopSettingsJsonContext.Default.HaVizDesktopSettings)
                   ?? new HaVizDesktopSettings();
        }
        catch (Exception)
        {
            return new HaVizDesktopSettings();
        }
    }

    /// <summary>Best-effort save; settings persistence must never take the app down (never throws).</summary>
    public void Save()
    {
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            JsonSerializer.Serialize(stream, this, HaVizDesktopSettingsJsonContext.Default.HaVizDesktopSettings);
        }
        catch (Exception)
        {
            // Read-only home / full disk - the box just falls back to defaults next start.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HaVizDesktopSettings))]
internal sealed partial class HaVizDesktopSettingsJsonContext : JsonSerializerContext;
