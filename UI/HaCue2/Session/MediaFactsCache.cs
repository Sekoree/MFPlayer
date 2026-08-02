using HaCue2.Core.Media;
using HaCue2.Core.Model;
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

    /// <summary>Whether a stored path names a place on this machine at all.</summary>
    private static bool IsResolvable(HaCueProject project, string path, string? projectPath) =>
        Path.IsPathRooted(MediaPaths.Resolve(project, path, projectPath));

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
