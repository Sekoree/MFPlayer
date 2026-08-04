using HaCue2.Core.Media;
using HaCue2.Core.Model;
using HaCue2.Core.Compile;
using HaCue2.Machine;

namespace HaCue2.Session;

/// <summary>
/// What every cue's media turned out to be, asked once and kept.
/// </summary>
/// <remarks>
/// <para>
/// The bridge between the probe and <see cref="ShowRuntime"/>: durations, broken files and stream
/// facts are MACHINE answers, so they arrive here rather than in the document. Nothing is written
/// back into the project — two machines with different copies of the same file may legitimately
/// disagree, and a length baked into a show would be wrong on the other one.
/// </para>
/// <para>
/// Probing is asynchronous and the views never wait for it. A cue whose file has not been looked at
/// yet reads "—", which is the same thing the shell said before any of this existed: the honest
/// answer to "how long is it" is "nobody has looked".
/// </para>
/// </remarks>
public sealed class MediaFactsCache
{
    private readonly Dictionary<string, MediaFacts> _byPath = [];
    private readonly HashSet<string> _inFlight = [];
    private readonly Lock _gate = new();

    /// <summary>Raised on the thread that finished a probe, once a batch has landed.</summary>
    public event Action? Changed;

    /// <summary>What is known about a path, or null if nobody has looked yet.</summary>
    public MediaFacts? Facts(string path)
    {
        lock (_gate)
            return _byPath.TryGetValue(path, out var facts) ? facts : null;
    }

    /// <summary>Cue ids whose media could not be opened — what <see cref="ShowRuntime.Broken"/> wants.</summary>
    public IReadOnlyDictionary<Guid, TimeSpan> DurationsIn(HaCueProject project, string? projectPath)
    {
        var durations = new Dictionary<Guid, TimeSpan>();

        foreach (var cue in project.AllCues().OfType<MediaCueNode>())
        {
            if (cue.MediaPath.Length == 0)
                continue;

            // A source that told us its length when it was added — a prepared YouTube video. Nothing
            // on this machine can be probed for it, and the number is not a guess.
            if (cue.SourceDurationMs > 0)
            {
                durations[cue.Id] = TimeSpan.FromMilliseconds(cue.SourceDurationMs);
                continue;
            }

            var resolved = MediaPaths.Resolve(project, cue.MediaPath, projectPath);
            if (Facts(resolved) is { Duration: { } duration })
                durations[cue.Id] = duration;
        }

        return durations;
    }

    /// <summary>
    /// Cues whose file has been looked at and could not be opened.
    /// </summary>
    /// <remarks>
    /// Two things are deliberately NOT broken. A path nobody has probed yet is unknown — painting a
    /// cue red before anybody looked is the failure this whole seam exists to avoid. And a RELATIVE
    /// path with no media root configured is UNRESOLVED: nobody has said where this show's media
    /// lives, so "not found" would be reporting a question as an answer. The project-status pass
    /// reports the missing root; the cue list stays quiet about it.
    /// </remarks>
    public IReadOnlySet<Guid> BrokenIn(HaCueProject project, string? projectPath)
    {
        var broken = new HashSet<Guid>();

        foreach (var cue in project.AllCues().OfType<MediaCueNode>())
        {
            if (cue.MediaPath.Length == 0 || !IsResolvable(project, cue.MediaPath, projectPath))
                continue;

            var resolved = MediaPaths.Resolve(project, cue.MediaPath, projectPath);

            if (Facts(resolved) is { } facts && !facts.IsKnown)
                broken.Add(cue.Id);
        }

        return broken;
    }

    /// <summary>
    /// Stream indices checked against the content signatures in the file currently on this machine.
    /// </summary>
    public IReadOnlyDictionary<Guid, ResolvedMediaTracks> TracksIn(
        HaCueProject project, string? projectPath)
    {
        var resolved = new Dictionary<Guid, ResolvedMediaTracks>();

        foreach (var cue in project.AllCues().OfType<MediaCueNode>())
        {
            if (cue.MediaPath.Length == 0
                || Facts(MediaPaths.Resolve(project, cue.MediaPath, projectPath)) is not { } facts)
                continue;

            var audio = Resolve(
                facts.AudioTracks, cue.AudioTrackIndex, cue.AudioTrackSignature, cue.AudioTrackIndex);
            var video = cue.VideoTrackIndex == -1
                ? -1
                : Resolve(facts.VideoTracks, cue.VideoTrackIndex, cue.VideoTrackSignature, cue.VideoTrackIndex);

            var subtitles = cue.Subtitles.Select(selection =>
            {
                if (selection.Path.Length > 0 || selection.StreamIndex < 0)
                    return selection.StreamIndex;

                return Resolve(
                    facts.SubtitleTracks,
                    selection.StreamIndex,
                    selection.Signature,
                    selection.StreamIndex) ?? -1;
            }).ToArray();

            resolved[cue.Id] = new ResolvedMediaTracks(audio, video, subtitles);
        }

        return resolved;
    }

    private static int? Resolve(
        IReadOnlyList<MediaTrack> tracks,
        int? index,
        string signature,
        int? unsignedFallback)
    {
        // Old projects have no signature. Their explicit choice remains authoritative; there is no
        // remembered identity with which to prove that the index moved.
        if (string.IsNullOrEmpty(signature))
            return unsignedFallback;

        return MediaFacts.Resolve(tracks, index, signature)?.Index;
    }

    /// <summary>Whether a stored path names a place on this machine at all.</summary>
    /// <remarks>
    /// A source URI never does: it is not a file, so it can be neither found nor missing, and the
    /// answer to "is the camera there" belongs to the moment the cue fires rather than to a probe.
    /// </remarks>
    private static bool IsResolvable(HaCueProject project, string path, string? projectPath) =>
        !SourceUri.IsSource(path)
        && Path.IsPathRooted(MediaPaths.Resolve(project, path, projectPath));

    /// <summary>
    /// Probes every media file the project references that has not been looked at yet.
    /// </summary>
    /// <remarks>
    /// Fire and forget by design — the caller carries on drawing and the answers arrive through
    /// <see cref="Changed"/>. Paths already in flight are skipped, so calling this on every document
    /// edit costs nothing after the first pass.
    /// </remarks>
    public void Refresh(HaCueProject project, string? projectPath = null)
    {
        var pending = new List<string>();

        lock (_gate)
        {
            foreach (var reference in MediaPaths.ReferencesIn(project))
            {
                var resolved = MediaPaths.Resolve(project, reference.Path, projectPath);

                // Nothing to open: a relative path with no media root is not a location.
                if (!Path.IsPathRooted(resolved))
                    continue;

                if (_byPath.ContainsKey(resolved) || !_inFlight.Add(resolved))
                    continue;

                pending.Add(resolved);
            }
        }

        if (pending.Count == 0)
            return;

        _ = ProbeAsync(pending);
    }

    private async Task ProbeAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var facts = await MediaProbe.ProbeAsync(path).ConfigureAwait(false);

            lock (_gate)
            {
                _byPath[path] = facts;
                _inFlight.Remove(path);
            }
        }

        Changed?.Invoke();
    }
}
