using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SPlayer.Core.Services;

public static class PlaylistIO
{
    public sealed class ParsedPlaylist
    {
        public string Title { get; init; } = "Playlist";
        public IReadOnlyList<PlaylistLine> Entries { get; init; } = Array.Empty<PlaylistLine>();
    }

    public sealed class PlaylistLine
    {
        public string FilePath { get; init; } = "";
        public string? Title { get; init; }
    }

    /// <summary>Parses a classic M3U (paths or URLs, optional EXTINF titles).</summary>
    public static ParsedPlaylist ReadM3u(string m3uFilePath, Encoding? encoding = null)
    {
        var text = File.ReadAllText(m3uFilePath, encoding ?? Encoding.UTF8);
        return ParseM3uText(m3uFilePath, text);
    }

    public static ParsedPlaylist ParseM3uText(string m3uFilePathForResolve, string text)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(m3uFilePathForResolve)) ?? "";
        var title = Path.GetFileNameWithoutExtension(m3uFilePathForResolve);
        var list = new List<PlaylistLine>();
        string? pendingTitle = null;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith('#'))
            {
                if (t.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = t.LastIndexOf(',');
                    if (comma >= 0 && comma < t.Length - 1)
                        pendingTitle = t[(comma + 1)..].Trim();
                }
                continue;
            }

            var resolved = Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.IsFile || u.Scheme == "http" || u.Scheme == "https")
                ? t
                : Path.GetFullPath(t, baseDir);
            var entry = new PlaylistLine
            {
                FilePath = resolved,
                Title = pendingTitle
            };
            list.Add(entry);
            pendingTitle = null;
        }

        if (string.IsNullOrWhiteSpace(title) && list.Count > 0)
            title = "Playlist";

        return new ParsedPlaylist { Title = title, Entries = list };
    }

    /// <summary>
    /// Batch list: one non-empty line = path to an M3U. Lines starting with # are comments.
    /// Extension suggestion: <c>.m3ubatch</c>.
    /// </summary>
    public static IReadOnlyList<string> ReadM3uBatchList(string batchFilePath, Encoding? encoding = null)
    {
        var text = File.ReadAllText(batchFilePath, encoding ?? Encoding.UTF8);
        return ParseM3uBatchList(Path.GetDirectoryName(Path.GetFullPath(batchFilePath)) ?? "", text);
    }

    public static IReadOnlyList<string> ParseM3uBatchList(string? baseDirForResolve, string text)
    {
        var dir = string.IsNullOrEmpty(baseDirForResolve) ? "" : baseDirForResolve;
        var result = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            var path = Path.IsPathRooted(t) ? t : Path.GetFullPath(t, dir);
            result.Add(path);
        }
        return result;
    }

    /// <summary>Writes M3U (UTF-8). Uses a relative path when the media file sits under the same directory as the M3U, otherwise the full path.</summary>
    public static void WriteM3u(string m3uFilePath, IReadOnlyList<PlaylistLine> entries, string? listTitleComment = null)
    {
        ArgumentNullException.ThrowIfNull(m3uFilePath);
        var fullM3U = Path.GetFullPath(m3uFilePath);
        var m3uDir = Path.GetDirectoryName(fullM3U) ?? "";
        using var sw = new StreamWriter(fullM3U, false, new UTF8Encoding(false));
        WriteM3uContent(sw, m3uDir, entries, listTitleComment);
    }

    /// <summary>Writes the same M3U content to a stream. Use <paramref name="m3uOutputDirForRelatives"/> for relative file lines; <see langword="null"/> uses absolute local paths only.</summary>
    public static void WriteM3u(Stream stream, IReadOnlyList<PlaylistLine> entries, string? listTitleComment = null, string? m3uOutputDirForRelatives = null, bool leaveStreamOpen = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var sw = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: leaveStreamOpen);
        using (sw)
            WriteM3uContent(sw, m3uOutputDirForRelatives, entries, listTitleComment);
    }

    private static void WriteM3uContent(TextWriter sw, string? m3uOutputDir, IReadOnlyList<PlaylistLine> entries, string? listTitleComment)
    {
        sw.WriteLine("#EXTM3U");
        if (!string.IsNullOrWhiteSpace(listTitleComment))
        {
            var safe = listTitleComment!.Replace("\r", " ").Replace("\n", " ");
            sw.WriteLine("# " + safe);
        }

        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.FilePath)) continue;
            var linePath = e.FilePath.Trim();
            var title = (e.Title ?? Path.GetFileNameWithoutExtension(linePath)).Replace("\r", " ").Replace("\n", " ");
            if (title.Contains(',') && !title.Contains('\"'))
                title = title.Replace(',', ' ');
            sw.WriteLine("#EXTINF:-1," + title);
            sw.WriteLine(FormatPathLineForM3U(m3uOutputDir, linePath));
        }
    }

    /// <summary>Writes the batch file format: one M3U path per line (for reopening with Load batch).</summary>
    public static void WriteM3uBatchList(string batchFilePath, IReadOnlyList<string> m3UPathsOnePerLine, string? headerComment = null)
    {
        var full = Path.GetFullPath(batchFilePath);
        var dir = Path.GetDirectoryName(full) ?? "";
        using var sw = new StreamWriter(full, false, new UTF8Encoding(false));
        if (!string.IsNullOrWhiteSpace(headerComment))
            sw.WriteLine("# " + headerComment.Replace("\r", " ").Replace("\n", " "));
        else
            sw.WriteLine("# MFPlayer — one M3U path per line (relative to this file or absolute).");

        foreach (var p in m3UPathsOnePerLine)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var t = p.Trim();
            try
            {
                if (t.Contains("://", StringComparison.Ordinal))
                {
                    sw.WriteLine(t);
                    continue;
                }

                var abs = Path.GetFullPath(t);
                var rel = Path.GetRelativePath(dir, abs);
                sw.WriteLine(!rel.StartsWith("..", StringComparison.Ordinal) && rel != "." ? rel : abs);
            }
            catch
            {
                sw.WriteLine(t);
            }
        }
    }

    public static string FormatPathLineForM3U(string? m3uOutputDirectory, string fileOrUrl)
    {
        if (fileOrUrl.Contains("://", StringComparison.Ordinal))
            return fileOrUrl;
        if (string.IsNullOrEmpty(m3uOutputDirectory))
            return Path.GetFullPath(fileOrUrl);
        try
        {
            var full = Path.GetFullPath(fileOrUrl);
            var m3uDir = Path.GetFullPath(m3uOutputDirectory);
            var rel = Path.GetRelativePath(m3uDir, full);
            if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal))
                return rel;
            return full;
        }
        catch
        {
            return fileOrUrl;
        }
    }

    public static string ToSafeFileName(string title, string defaultName = "playlist")
    {
        var t = string.IsNullOrWhiteSpace(title) ? defaultName : title;
        foreach (var c in Path.GetInvalidFileNameChars())
            t = t.Replace(c, '_');
        t = t.Trim();
        if (t.Length == 0) t = defaultName;
        if (t.Length > 80) t = t[..80];
        return t;
    }
}
