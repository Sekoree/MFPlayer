namespace S.Media.Session;

/// <summary>
/// Validates a cue list's own invariants: unique ids and numbers, supported fault policies, follow-on links
/// that resolve, and an acyclic auto-continue subgraph.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="ShowDocumentValidator"/> along the engine/cue seam. Everything here is a question
/// only a cue list can answer, and none of it constrains a clip, a composition or a route - so an engine host
/// with no cues runs none of it. <see cref="ShowDocumentValidator"/> still calls it for a document that has
/// cues, so callers see one validation pass and one error list, exactly as before.
/// </para>
/// <para>
/// One rule deliberately did NOT come across: "a clip binds a known cue". That was never a cue-list
/// invariant - it forced every clip to correspond to a cue, which is what made a cue-free document
/// unloadable.
/// </para>
/// </remarks>
public static class CueListValidator
{
    /// <summary>Every problem found in <paramref name="cues"/>. Empty when the list is valid (including empty).</summary>
    public static IReadOnlyList<ShowValidationIssue> Validate(IReadOnlyList<CueDefinition> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var errors = new ShowValidationIssues();

        // Cues: non-empty unique ids, and unique numbers (GO advances by number, so duplicates break the cursor).
        var cueIds = new HashSet<string>(StringComparer.Ordinal);
        var cueNumbers = new HashSet<int>();
        foreach (var cue in cues)
        {
            if (string.IsNullOrEmpty(cue.Id))
                errors.Add("a cue has an empty id.");
            else if (!cueIds.Add(cue.Id))
                errors.Add("cue", cue.Id, $"duplicate cue id '{cue.Id}'.");
            if (string.IsNullOrEmpty(cue.Label))
                // WARNING, not an error: a missing label is a cosmetic gap in the operator's list. Refusing
                // to open a show over it - which is what this used to do - is out of all proportion.
                errors.Warn("cue", cue.Id, $"cue '{cue.Id}' has an empty label.");
            if (!cueNumbers.Add(cue.Number))
                errors.Add("cue", cue.Id, $"duplicate cue number {cue.Number} - GO uses the number as its cursor, so it must be unique.");
            if (!CueFaultPolicySupport.IsSupported(cue.FaultPolicy))
                errors.Add("cue", cue.Id, $"cue '{cue.Id}' uses unsupported fault policy {CueFaultPolicySupport.Display(cue.FaultPolicy)}; "
                    + $"this runtime supports only {CueFaultPolicy.StopShow} and {CueFaultPolicy.Continue}.");
        }

        ValidateFollowOn(cues, cueIds, errors);

        // Stop targets must reference existing cues.
        foreach (var cue in cues)
            foreach (var target in cue.StopTargetIds ?? [])
                if (!cueIds.Contains(target))
                    errors.Add("cue", cue.Id, $"cue '{cue.Id}' lists unknown stop-target cue '{target}'.");

        return errors;
    }

    /// <summary>Checks that follow-on links resolve, and that the <em>auto-continue</em> subgraph (the only one
    /// that recurses in <see cref="CueGraph"/>) is acyclic - a cycle would auto-continue forever.</summary>
    private static void ValidateFollowOn(IReadOnlyList<CueDefinition> cues, HashSet<string> cueIds, ShowValidationIssues errors)
    {
        // Out-degree ≤ 1 functional graph over auto-continue follow-on edges.
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cue in cues)
        {
            if (cue.FollowOnCueId is not { } follow)
                continue;
            if (!cueIds.Contains(follow))
                errors.Add($"cue '{cue.Id}' has an unknown follow-on cue '{follow}'.");
            else if (cue.AutoContinue && !string.IsNullOrEmpty(cue.Id))
                next[cue.Id] = follow; // only auto-continue links recurse; a plain follow-on is a GO target
        }

        var settled = new HashSet<string>(StringComparer.Ordinal); // walked already, proven cycle-free
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in next.Keys)
        {
            if (settled.Contains(start))
                continue;
            var path = new HashSet<string>(StringComparer.Ordinal);
            var node = start;
            while (node is not null && !settled.Contains(node))
            {
                if (!path.Add(node))
                {
                    if (reported.Add(node))
                        errors.Add($"the auto-continue follow-on chain through cue '{node}' contains a cycle (it would never terminate).");
                    break;
                }
                node = next.GetValueOrDefault(node);
            }
            foreach (var n in path)
                settled.Add(n);
        }
    }
}
