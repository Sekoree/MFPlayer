using Avalonia;
using Avalonia.Media;
using S.Media.OpenGL;

namespace S.Media.OpenGL.Avalonia.Diagnostics;

public sealed class MediaHudOverlay
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, HudEntry> _entries = [];

	public void Update(HudEntry entry)
	{
		lock (_gate)
		{
			_entries[entry.Key] = entry;
		}
	}

	public string BuildOverlayTextSnapshot()
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

			return $"R:{renderFps:F1} V:{videoFps:F1} {format}{Environment.NewLine}Q:{queueDepth} U:{uploadMs:F2} AV:{avDriftMs:F1} GPU:{gpu} D:{dropped}";
		}
	}

	public void Render(DrawingContext context, Rect bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		lock (_gate)
		{
			if (_entries.Count == 0)
			{
				return;
			}

			var panelBounds = new Rect(bounds.X + 8, bounds.Y + 8, Math.Min(360, bounds.Width - 16), Math.Min(96, bounds.Height - 16));
			context.DrawRectangle(Brushes.Black, null, panelBounds);
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
