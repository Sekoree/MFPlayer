namespace S.Media.Session;

/// <summary>How badly a validation issue affects loading the show.</summary>
public enum ShowValidationSeverity
{
    /// <summary>The document cannot be loaded as written. <see cref="ShowDocumentValidator.ThrowIfInvalid"/> throws.</summary>
    Error,

    /// <summary>Worth telling the operator about, but the show loads and runs.</summary>
    Warning,
}

/// <summary>
/// One problem found in a show document: what is wrong, how badly, and what it is wrong <em>about</em>.
/// </summary>
/// <param name="Severity">Whether this blocks the load.</param>
/// <param name="Message">Human-readable, and the text used in the load exception.</param>
/// <param name="SubjectKind">What sort of thing this is about - <c>"cue"</c>, <c>"clip"</c>,
/// <c>"composition"</c>, <c>"route"</c>, <c>"audioOutput"</c>, or <c>"document"</c> for whole-document rules.</param>
/// <param name="SubjectId">That thing's id, when the rule knows it.</param>
/// <remarks>
/// <para>
/// Replaces a bare string list. A status panel needs to sort errors from warnings and to jump to the row a
/// problem is about; neither is recoverable from prose, and parsing ids back out of a sentence is the kind
/// of thing that works until someone rewords a message.
/// </para>
/// <para>
/// <see cref="SubjectKind"/>/<see cref="SubjectId"/> together are the navigation target. Both are null for
/// rules about the document as a whole (an unsupported version), and <see cref="SubjectId"/> alone can be
/// null when the offending thing is precisely the one that has no id.
/// </para>
/// </remarks>
public sealed record ShowValidationIssue(
    ShowValidationSeverity Severity,
    string Message,
    string? SubjectKind = null,
    string? SubjectId = null)
{
    /// <summary>The message, so a list of issues prints as it always did.</summary>
    public override string ToString() => Message;
}

/// <summary>
/// Collects issues while validating. The plain <c>Add(message)</c> overload records an
/// <see cref="ShowValidationSeverity.Error"/>, so a rule states its severity only when it is not the usual one.
/// </summary>
internal sealed class ShowValidationIssues : List<ShowValidationIssue>
{
    /// <summary>An error about the document as a whole.</summary>
    public void Add(string message) => Add(new ShowValidationIssue(ShowValidationSeverity.Error, message));

    /// <summary>An error about a specific subject, which a host can navigate to.</summary>
    public void Add(string subjectKind, string? subjectId, string message) =>
        Add(new ShowValidationIssue(ShowValidationSeverity.Error, message, subjectKind, subjectId));

    /// <summary>A warning about a specific subject: reported, but the show still loads.</summary>
    public void Warn(string subjectKind, string? subjectId, string message) =>
        Add(new ShowValidationIssue(ShowValidationSeverity.Warning, message, subjectKind, subjectId));
}
