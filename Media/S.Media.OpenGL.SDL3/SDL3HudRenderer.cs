using S.Media.Core.Errors;

namespace S.Media.OpenGL.SDL3;

public sealed class SDL3HudRenderer
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HudEntry> _entries = [];

    public int Update(HudEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.Key] = entry;
            return MediaResult.Success;
        }
    }

    public string BuildHudTextSnapshot()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return string.Empty;
            }

            var renderFps = ResolveDouble("render.fps");
            var videoFps = ResolveDouble("video.fps");
            var format = NormalizeFormat(ResolveText("pixel.format"));
            var queueDepth = ResolveInt("queue.depth");
            var uploadMs = ResolveDouble("upload.ms");
            var avDriftMs = ResolveDouble("av.drift.ms");
            var gpu = ResolveBool("gpu.decode") ? 1 : 0;
            var dropped = ResolveInt("drop.frames");

            return $"RENDER:{renderFps:F1} VIDEO:{videoFps:F1} {format}{Environment.NewLine}Q:{queueDepth} UP:{uploadMs:F2} AV:{avDriftMs:F1} GPU:{gpu} DROP:{dropped}";
        }
    }

    public int Render()
    {
        lock (_gate)
        {
            _ = _entries.Count;
            return MediaResult.Success;
        }
    }

    private double ResolveDouble(params string[] keys)
    {
        if (TryGetEntry(keys, out var entry) && entry.Value is not null)
        {
            if (entry.Value is double d)
            {
                return d;
            }

            if (entry.Value is float f)
            {
                return f;
            }

            if (entry.Value is int i)
            {
                return i;
            }

            if (double.TryParse(entry.Value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private long ResolveInt(params string[] keys)
    {
        if (TryGetEntry(keys, out var entry) && entry.Value is not null)
        {
            if (entry.Value is long l)
            {
                return l;
            }

            if (entry.Value is int i)
            {
                return i;
            }

            if (long.TryParse(entry.Value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private bool ResolveBool(params string[] keys)
    {
        if (TryGetEntry(keys, out var entry) && entry.Value is not null)
        {
            if (entry.Value is bool b)
            {
                return b;
            }

            if (entry.Value is int i)
            {
                return i != 0;
            }

            if (bool.TryParse(entry.Value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return false;
    }

    private string ResolveText(params string[] keys)
    {
        if (TryGetEntry(keys, out var entry) && entry.Value is not null)
        {
            return entry.Value.ToString() ?? "UNKNOWN";
        }

        return "UNKNOWN";
    }

    private bool TryGetEntry(string[] keys, out HudEntry info)
    {
        foreach (var key in keys)
        {
            if (_entries.TryGetValue(key, out info))
            {
                return true;
            }

            foreach (var pair in _entries)
            {
                if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.EndsWith($".{key}", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.EndsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    info = pair.Value;
                    return true;
                }
            }
        }

        info = default;
        return false;
    }

    private static string NormalizeFormat(string value)
    {
        return value
            .ToUpperInvariant()
            .Replace("->", "/", StringComparison.Ordinal)
            .Replace("→", "/", StringComparison.Ordinal);
    }
}
