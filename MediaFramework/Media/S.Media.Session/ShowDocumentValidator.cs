namespace S.Media.Session;

/// <summary>Thrown when a <see cref="ShowDocument"/> fails validation at load. The running show is left
/// untouched - validation happens before any teardown (NXT-12), so a malformed document can never destroy
/// a live show or leave a half-built replacement.</summary>
public sealed class ShowDocumentValidationException(IReadOnlyList<ShowValidationIssue> errors)
    : Exception("show document is invalid:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", errors))
{
    /// <summary>Every blocking problem found, so a caller/editor can surface them all at once.</summary>
    /// <remarks>Errors only - warnings never reach here, because a document that only warns still loads.</remarks>
    public IReadOnlyList<ShowValidationIssue> Errors { get; } = errors;
}

/// <summary>
/// Validates a <see cref="ShowDocument"/>'s referential and semantic invariants before it is loaded (NXT-12):
/// supported version and cue fault policies, unique cue ids/numbers, single clip per cue, references that resolve, acyclic
/// auto-continue chains (NXT-07), and sane composition dimensions/rates. <see cref="Validate"/> returns every
/// problem found; <see cref="ThrowIfInvalid"/> throws a <see cref="ShowDocumentValidationException"/> when any
/// exist. Pure and allocation-light so it is cheap to run on every load.
/// </summary>
public static class ShowDocumentValidator
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 3;

    /// <summary>The oldest schema version this build can still load.</summary>
    /// <remarks>
    /// Older documents are accepted because every field added since is additive and nullable, and
    /// <c>LoadDocumentCoreAsync</c> null-coalesces missing collections - so an older document simply lacks
    /// features rather than being malformed.
    /// </remarks>
    public const int MinimumSupportedVersion = 1;

    /// <summary>The single schema version this build understands.</summary>
    [Obsolete($"Use {nameof(CurrentVersion)} (writing) or {nameof(MinimumSupportedVersion)} (loading).")]
    public const int SupportedVersion = CurrentVersion;

    /// <summary>Point-list rules for a user-drawn fade shape: at least two points, sorted, finite, and
    /// within the normalized 0..1 range the evaluator assumes.</summary>
    private static void ValidateFadeShape(
        ShowValidationIssues errors, string cueId, string which, CustomFadeCurve? shape)
    {
        if (shape is null)
            return;

        var points = shape.Points;
        if (points.Count < 2)
        {
            errors.Add($"the clip for cue '{cueId}' has a custom {which} shape with fewer than two points.");
            return;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (!double.IsFinite(p.Progress) || !double.IsFinite(p.Level))
                errors.Add($"the clip for cue '{cueId}' has a non-finite custom {which} shape point.");
            else if (p.Progress < 0 || p.Progress > 1 || p.Level < 0 || p.Level > 1)
                errors.Add($"the clip for cue '{cueId}' has a custom {which} shape point outside 0..1.");
            if (i > 0 && p.Progress < points[i - 1].Progress)
                errors.Add($"the clip for cue '{cueId}' has an unsorted custom {which} shape.");
        }
    }

    private static void ValidateEnvelope(
        ShowValidationIssues errors,
        string cueId,
        string which,
        IReadOnlyList<ShowEnvelopePoint>? points,
        bool opacity,
        bool unrestricted = false)
    {
        if (points is null)
            return;

        var valueScale = points.Count > 0 ? points[0].ValueScale : ShowEnvelopeValueScale.Linear;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Time < TimeSpan.Zero)
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has a negative {which} envelope time at point {i}.");
            if (i > 0 && point.Time < points[i - 1].Time)
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has an unsorted {which} envelope.");
            if (!float.IsFinite(point.Level))
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has a non-finite {which} envelope level at point {i}.");
            else if (!unrestricted && !opacity && point.ValueScale == ShowEnvelopeValueScale.Linear
                     && (point.Level < 0f || point.Level > VolumeEnvelopes.MaxLevel))
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has a volume envelope level outside 0..+12 dB at point {i}.");
            else if (!unrestricted && !opacity && point.ValueScale == ShowEnvelopeValueScale.Decibels
                     && point.Level is < VolumeEnvelopes.SilenceFloorDb or > VolumeEnvelopes.MaximumDb)
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has a volume envelope dB value outside " +
                    $"{VolumeEnvelopes.SilenceFloorDb}..+{VolumeEnvelopes.MaximumDb} dB at point {i}.");
            if (!Enum.IsDefined(point.ValueScale))
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has an unknown {which} envelope value scale at point {i}.");
            else if (point.ValueScale != valueScale)
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' mixes value scales in its {which} envelope.");
            else if ((opacity || unrestricted) && point.ValueScale != ShowEnvelopeValueScale.Linear)
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' uses a non-linear value scale for its {which} envelope.");
            if (!Enum.IsDefined(point.CurveToNext))
                errors.Add("clip", cueId,
                    $"the clip for cue '{cueId}' has an unknown {which} envelope curve at point {i}.");
        }
    }

    /// <summary>Validates <paramref name="document"/> and returns every problem found (empty ⇒ valid).</summary>
    public static IReadOnlyList<ShowValidationIssue> Validate(ShowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new ShowValidationIssues();

        // Tolerant on the low side, closed on the high side. Two actively developed apps plus an external
        // ABI consumer share this format, so a hard equality check would force lockstep releases for every
        // additive change; a NEWER document still fails closed and loudly, because this build genuinely
        // cannot know what it would be ignoring.
        if (document.Version < MinimumSupportedVersion || document.Version > CurrentVersion)
        {
            errors.Add(
                $"unsupported document version {document.Version} (this build loads " +
                $"{MinimumSupportedVersion}..{CurrentVersion}).");
        }

        // Compositions: unique ids, positive dimensions and frame rate.
        var compIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in document.Compositions ?? [])
        {
            if (string.IsNullOrEmpty(comp.Id))
                errors.Add("a composition has an empty id.");
            else if (!compIds.Add(comp.Id))
                errors.Add("composition", comp.Id, $"duplicate composition id '{comp.Id}'.");
            if (comp.Width <= 0 || comp.Height <= 0)
                errors.Add("composition", comp.Id, $"composition '{comp.Id}' has non-positive dimensions {comp.Width}x{comp.Height}.");
            if (comp.FrameRateNum <= 0 || comp.FrameRateDen <= 0)
                errors.Add("composition", comp.Id, $"composition '{comp.Id}' has a non-positive frame rate {comp.FrameRateNum}/{comp.FrameRateDen}.");
        }

        // Clips: ids are unique (the runtime keys clips by id - a duplicate throws at load) and any placement
        // names an existing composition.
        //
        // A clip whose id matches no cue is NOT an error. That rule belonged to the cue layer, not to the
        // document: a clip is a playable thing with an id, and whether some cue happens to fire it is a
        // question only a cue list asks. Enforcing it here is what forced HaPlay's deck - which has no cues
        // at all - to invent one per track just to get a document past validation.
        var clipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in document.Clips ?? [])
        {
            if (!clipIds.Add(clip.ClipId))
                errors.Add("clip", clip.ClipId, $"more than one clip has the id '{clip.ClipId}' - clip ids must be unique.");
            if (string.IsNullOrWhiteSpace(clip.ClipId))
                errors.Add("a clip has an empty id.");
            if (clip.CompositionId is { Length: > 0 } cid && !compIds.Contains(cid))
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' references unknown composition '{cid}'.");

            // DOC-01: scalar/path sanity so a malformed clip is caught at load, not silently mis-played.
            if (string.IsNullOrWhiteSpace(clip.MediaPath))
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an empty media path.");
            if (clip.StartOffset < TimeSpan.Zero)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative start offset.");
            if (clip.EndOffset < TimeSpan.Zero)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative end offset.");
            if (clip.FadeIn < TimeSpan.Zero)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative fade-in.");
            if (clip.FadeOut < TimeSpan.Zero)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative fade-out.");
            if (clip.PreEndNotify < TimeSpan.Zero)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative pre-end notify window.");
            if (clip.LoopCrossfade < TimeSpan.Zero)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative loop crossfade window.");
            if (clip.LayerIndex < 0)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a negative layer index {clip.LayerIndex}.");
            if (clip.VideoStreamIndex is { } vsi && vsi < -1)
                errors.Add("clip", clip.ClipId, $"clip '{clip.ClipId}': VideoStreamIndex {vsi} is invalid (null = automatic, -1 = disabled, >= 0 = stream index).");
            if (clip.AudioStreamIndex is { } asi && asi < -1)
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an audio stream index {asi} below -1 (use -1 for none, null for auto).");
            foreach (var sub in clip.GetSubtitleSelections())
                if (sub.StreamIndex < -1)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a subtitle stream index {sub.StreamIndex} below -1.");
            if (clip.Placement is { } placement)
                ValidatePlacement(clip.ClipId, "its placement", placement, errors);

            // A clip may fan its video onto several compositions (ExtraPlacements); every one must resolve too,
            // else the placement is silently dropped at play time instead of caught at load.
            foreach (var extra in clip.ExtraPlacements ?? [])
            {
                if (string.IsNullOrEmpty(extra.CompositionId))
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an extra placement with an empty composition id.");
                else if (!compIds.Contains(extra.CompositionId))
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an extra placement on unknown composition '{extra.CompositionId}'.");
                if (extra.LayerIndex < 0)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an extra placement with a negative layer index {extra.LayerIndex}.");
                if (extra.Placement is { } extraPlacement)
                    ValidatePlacement(clip.ClipId, $"its placement on '{extra.CompositionId}'", extraPlacement, errors);
            }

            foreach (var audioRoute in clip.AudioRoutes ?? [])
            {
                if (!float.IsFinite(audioRoute.Gain) || audioRoute.Gain < 0f)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an invalid audio-route gain.");
                if (audioRoute.SampleRate is { } rate && rate <= 0)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a non-positive audio route sample rate {rate}.");
                if (audioRoute.MatrixOutputChannels is <= 0)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a non-positive audio matrix output count.");
                foreach (var cell in audioRoute.MatrixCells ?? [])
                {
                    if (cell.InputChannel < 0 || cell.OutputChannel < 0
                        || audioRoute.MatrixOutputChannels is { } outputs && cell.OutputChannel >= outputs)
                        errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an audio matrix cell outside its declared dimensions.");
                    if (!float.IsFinite(cell.Gain) || cell.Gain < 0f)
                        errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an invalid audio matrix cell gain.");
                }
            }

            // User-drawn fade shapes: the constructor enforces sortedness and finiteness, but a document
            // deserialized straight into the record bypasses it, so the same rules are checked here.
            ValidateFadeShape(errors, clip.ClipId, "fade-in", clip.FadeInShape);
            ValidateFadeShape(errors, clip.ClipId, "fade-out", clip.FadeOutShape);
            if (!Enum.IsDefined(clip.FadeInCurve))
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an unknown fade-in curve.");
            if (!Enum.IsDefined(clip.FadeOutCurve))
                errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an unknown fade-out curve.");
            ValidateEnvelope(errors, clip.ClipId, "volume", clip.VolumeEnvelope, opacity: false);
            ValidateEnvelope(errors, clip.ClipId, "opacity", clip.OpacityEnvelope, opacity: true);
            foreach (var placementEnvelope in clip.PlacementOpacityEnvelopes ?? [])
            {
                if (string.IsNullOrWhiteSpace(placementEnvelope.CompositionId) || placementEnvelope.LayerIndex < 0)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an invalid placement opacity target.");
                if (!clip.GetPlacements().Any(placement =>
                        string.Equals(placement.CompositionId, placementEnvelope.CompositionId, StringComparison.Ordinal)
                        && placement.LayerIndex == placementEnvelope.LayerIndex))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has opacity automation for a placement it does not use.");
                ValidateEnvelope(errors, clip.ClipId, "placement opacity", placementEnvelope.Points, opacity: true);
            }
            foreach (var placementEnvelope in clip.PlacementTransformEnvelopes ?? [])
            {
                if (string.IsNullOrWhiteSpace(placementEnvelope.CompositionId) || placementEnvelope.LayerIndex < 0)
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has an invalid placement transform target.");
                if (!Enum.IsDefined(placementEnvelope.Property))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has an unknown placement transform property.");
                if (!clip.GetPlacements().Any(placement =>
                        string.Equals(placement.CompositionId, placementEnvelope.CompositionId, StringComparison.Ordinal)
                        && placement.LayerIndex == placementEnvelope.LayerIndex))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has transform automation for a placement it does not use.");
                ValidateEnvelope(
                    errors, clip.ClipId, "placement transform", placementEnvelope.Points,
                    opacity: false, unrestricted: true);
                if (placementEnvelope.Property is ShowPlacementProperty.DestWidth or ShowPlacementProperty.DestHeight
                    && placementEnvelope.Points.Any(point => point.Level <= 0))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has non-positive placement size automation.");
            }
            foreach (var effectEnvelope in clip.PlacementEffectEnvelopes ?? [])
            {
                if (string.IsNullOrWhiteSpace(effectEnvelope.EffectiveParameterId))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has placement-effect automation without a parameter id.");
                var effectTargetPlacement = clip.GetPlacements().FirstOrDefault(candidate =>
                    string.Equals(candidate.CompositionId, effectEnvelope.CompositionId, StringComparison.Ordinal)
                    && candidate.LayerIndex == effectEnvelope.LayerIndex);
                if (effectTargetPlacement is null || string.IsNullOrWhiteSpace(effectEnvelope.EffectInstanceId))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has an invalid placement effect target.");
                if (!Enum.IsDefined(effectEnvelope.Property))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has an unknown placement effect property.");
                if (effectTargetPlacement?.Placement is { } effectPlacement
                    && !string.Equals(effectPlacement.ChromaKeyInstanceId, effectEnvelope.EffectInstanceId,
                        StringComparison.Ordinal)
                    && !string.Equals(effectPlacement.ColorAdjustInstanceId, effectEnvelope.EffectInstanceId,
                        StringComparison.Ordinal)
                    && (effectPlacement.Effects?.All(effect =>
                        !string.Equals(effect.InstanceId, effectEnvelope.EffectInstanceId,
                            StringComparison.Ordinal)) ?? true))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has parameter automation for an effect it does not use.");
                ValidateEnvelope(
                    errors, clip.ClipId, "placement effect", effectEnvelope.Points,
                    opacity: false, unrestricted: true);
            }
            foreach (var effect in clip.AudioEffects ?? [])
            {
                if (string.IsNullOrWhiteSpace(effect.InstanceId)
                    || string.IsNullOrWhiteSpace(effect.EffectTypeId))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has an invalid audio effect instance.");
                if (effect.Parameters is null || effect.Parameters.Any(parameter =>
                        string.IsNullOrWhiteSpace(parameter.ParameterId) || !double.IsFinite(parameter.Value))
                    || effect.Parameters.GroupBy(parameter => parameter.ParameterId, StringComparer.Ordinal)
                        .Any(group => group.Count() > 1))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has invalid audio effect parameters.");
            }
            if ((clip.AudioEffects ?? []).GroupBy(effect => effect.InstanceId, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
                errors.Add("clip", clip.ClipId,
                    $"the clip for cue '{clip.ClipId}' has duplicate audio effect instance ids.");
            foreach (var envelope in clip.AudioEffectEnvelopes ?? [])
            {
                if (string.IsNullOrWhiteSpace(envelope.EffectInstanceId)
                    || string.IsNullOrWhiteSpace(envelope.ParameterId)
                    || (clip.AudioEffects?.All(effect => effect.InstanceId != envelope.EffectInstanceId) ?? true))
                    errors.Add("clip", clip.ClipId,
                        $"the clip for cue '{clip.ClipId}' has an invalid audio effect automation target.");
                ValidateEnvelope(
                    errors, clip.ClipId, "audio effect", envelope.Points,
                    opacity: false, unrestricted: true);
            }

            // Logical sends (HaCue two-matrix model): cell sanity only - whether a LogicalChannelId
            // exists is a PROJECT question the session cannot answer from the document alone (the
            // program-audio target owns the channel list; HaCue's preflight validator reports broken
            // references against the loaded project).
            foreach (var send in clip.LogicalSends ?? [])
            {
                if (send.SourceChannel < 0)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a logical send with a negative source channel {send.SourceChannel}.");
                if (string.IsNullOrWhiteSpace(send.LogicalChannelId))
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has a logical send with an empty logical channel id.");
                if (!float.IsFinite(send.Gain) || send.Gain < 0f)
                    errors.Add("clip", clip.ClipId, $"the clip for cue '{clip.ClipId}' has an invalid logical send gain.");
            }
        }

        errors.AddRange(CueListValidator.Validate(document.Cues ?? []));

        // Audio outputs: unique ids. Routes: an enabled route's SourceId is a cue id and its OutputId must be a
        // declared audio output or the implicit master - a dangling route otherwise silently never matches at
        // play time instead of being caught at load (NXT-25).
        var audioOutputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in document.AudioOutputs ?? [])
        {
            if (string.IsNullOrEmpty(output.Id))
                errors.Add("an audio output has an empty id.");
            else if (!audioOutputIds.Add(output.Id))
                errors.Add("audioOutput", output.Id, $"duplicate audio output id '{output.Id}'.");
            if (string.IsNullOrWhiteSpace(output.GroupId))
                errors.Add("audioOutput", output.Id, $"audio output '{output.Id}' has an empty group id.");
        }

        foreach (var route in document.Routes ?? [])
        {
            if (!route.Enabled)
                continue;
            // SourceId is matched against a CLIP id at play time (ResolveOutputChannelMap compares it to
            // ShowClipBinding.ClipId). Validating it against cue ids only ever worked because the two were
            // the same string; in a document with no cues it rejected perfectly good routes.
            if (!clipIds.Contains(route.SourceId))
                errors.Add("route", route.SourceId, $"route '{route.SourceId}' → '{route.OutputId}' references an unknown clip.");
            if (!string.Equals(route.OutputId, ShowSession.MasterOutputId, StringComparison.Ordinal)
                && !audioOutputIds.Contains(route.OutputId))
                errors.Add("route", route.SourceId, $"route '{route.SourceId}' → '{route.OutputId}' references an undeclared audio output.");
        }

        return errors;
    }

    /// <summary>DOC-01: a placement's geometry must be finite and in range so the compositor is never handed
    /// a NaN/Infinity transform, a collapsed/negative dest rect, or crops that erase the whole frame.</summary>
    private static void ValidatePlacement(string cueId, string where, ShowVideoPlacement p, ShowValidationIssues errors)
    {
        void Finite(double v, string name)
        {
            if (!double.IsFinite(v))
                errors.Add($"the clip for cue '{cueId}' has a non-finite {name} in {where}.");
        }

        Finite(p.DestX, "dest X");
        Finite(p.DestY, "dest Y");
        Finite(p.DestWidth, "dest width");
        Finite(p.DestHeight, "dest height");
        Finite(p.Opacity, "opacity");
        Finite(p.RotationDegrees, "rotation");
        Finite(p.CropLeft, "left crop");
        Finite(p.CropTop, "top crop");
        Finite(p.CropRight, "right crop");
        Finite(p.CropBottom, "bottom crop");

        if (double.IsFinite(p.DestWidth) && p.DestWidth <= 0)
            errors.Add($"the clip for cue '{cueId}' has a non-positive dest width in {where}.");
        if (double.IsFinite(p.DestHeight) && p.DestHeight <= 0)
            errors.Add($"the clip for cue '{cueId}' has a non-positive dest height in {where}.");
        if (double.IsFinite(p.Opacity) && p.Opacity is < 0 or > 1)
            errors.Add($"the clip for cue '{cueId}' has an opacity {p.Opacity} outside [0, 1] in {where}.");

        CheckCrop(p.CropLeft, "left crop");
        CheckCrop(p.CropTop, "top crop");
        CheckCrop(p.CropRight, "right crop");
        CheckCrop(p.CropBottom, "bottom crop");
        if (double.IsFinite(p.CropLeft) && double.IsFinite(p.CropRight) && p.CropLeft + p.CropRight >= 1)
            errors.Add($"the clip for cue '{cueId}' has horizontal crops that erase the whole frame in {where}.");
        if (double.IsFinite(p.CropTop) && double.IsFinite(p.CropBottom) && p.CropTop + p.CropBottom >= 1)
            errors.Add($"the clip for cue '{cueId}' has vertical crops that erase the whole frame in {where}.");

        if (p.Effects is { Count: > 0 } effects)
        {
            if (effects.Any(effect => string.IsNullOrWhiteSpace(effect.InstanceId)))
                errors.Add($"the clip for cue '{cueId}' has a layer effect without an instance id in {where}.");
            if (effects.Any(effect => string.IsNullOrWhiteSpace(effect.EffectTypeId)))
                errors.Add($"the clip for cue '{cueId}' has a layer effect without a type id in {where}.");
            if (effects.GroupBy(effect => effect.InstanceId, StringComparer.Ordinal)
                .Any(group => group.Count() > 1))
                errors.Add($"the clip for cue '{cueId}' has duplicate layer-effect instance ids in {where}.");
            foreach (var effect in effects)
            {
                if (effect.Parameters is null || effect.Parameters.Any(parameter =>
                        string.IsNullOrWhiteSpace(parameter.ParameterId) || !double.IsFinite(parameter.Value)))
                    errors.Add($"the clip for cue '{cueId}' has an invalid layer-effect parameter in {where}.");
                if (effect.Parameters is not null
                    && effect.Parameters.GroupBy(parameter => parameter.ParameterId, StringComparer.Ordinal)
                    .Any(group => group.Count() > 1))
                    errors.Add($"the clip for cue '{cueId}' has duplicate layer-effect parameters in {where}.");
            }
        }

        void CheckCrop(double v, string name)
        {
            if (double.IsFinite(v) && v is < 0 or > 1)
                errors.Add($"the clip for cue '{cueId}' has a {name} {v} outside [0, 1] in {where}.");
        }
    }

    /// <summary>Throws <see cref="ShowDocumentValidationException"/> if <paramref name="document"/> is invalid.</summary>
    public static void ThrowIfInvalid(ShowDocument document)
    {
        // Errors only: a warning is something the operator should see in a status panel, not something that
        // refuses to open their show.
        var errors = Validate(document)
            .Where(i => i.Severity == ShowValidationSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
            throw new ShowDocumentValidationException(errors);
    }


}
