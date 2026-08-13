using HaCue2.Core.Model;
using HaCue2.Core.Serialization;

namespace HaCue2.Core.Journal;

/// <summary>
/// The undo journal, and the single commit point for the dirty flag, the project hash and autosave.
/// </summary>
/// <remarks>
/// <para>
/// Holds COMMANDS, not document snapshots. Snapshots are simpler but make undo depth cost
/// proportional to project size, and a large cue project is not cheap to clone.
/// </para>
/// <para>
/// Cleared on load and never persisted: it is an editing aid, not show state. An undo history that
/// survived a reopen would let someone undo past the show they actually arrived with.
/// </para>
/// <para>
/// "Is this project modified" is answered from the same place as "what changed", instead of being
/// inferred separately from what actually happened - which is how a dirty flag ends up disagreeing
/// with the file on disk.
/// </para>
/// </remarks>
public sealed class ProjectJournal
{
    private readonly List<IProjectCommand> _undo = [];
    private readonly List<IProjectCommand> _redo = [];

    /// <summary>The command at the top of the undo stack when the project was last saved.</summary>
    /// <remarks>
    /// A reference, not a depth. Comparing depths reports "clean" when the operator undoes past the
    /// save point and then makes a different edit that happens to restore the count.
    /// </remarks>
    private IProjectCommand? _savedAt;

    private CoalesceKey? _openGroup;
    private CompositeScope? _scope;

    public ProjectJournal(HaCueProject project) => Project = project;

    public HaCueProject Project { get; private set; }

    /// <summary>
    /// Blocks authoring commands while permitting the cue Note field. Transport/runtime state does
    /// not enter the journal and is therefore unaffected.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Raised after any change to the document, the stacks, or the dirty flag.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IProjectCommand? NextUndo => _undo.Count > 0 ? _undo[^1] : null;
    public IProjectCommand? NextRedo => _redo.Count > 0 ? _redo[^1] : null;

    /// <summary>Everything done since the journal was last reset, oldest first - the edit log.</summary>
    public IReadOnlyList<IProjectCommand> Log => _undo;

    /// <summary>Set by <see cref="MarkDirty"/> - a change that will be SAVED but cannot be UNDONE.</summary>
    private bool _dirtyOutsideTheStack;

    public bool IsDirty => _dirtyOutsideTheStack || !ReferenceEquals(NextUndo, _savedAt);

    /// <summary>
    /// Records that the document changed by a route that is deliberately not undoable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot recall and a patch cue firing both write real cell gains that travel in the file, and
    /// neither belongs on the undo stack - "undo" means un-edit my document, never un-recall my
    /// snapshot or un-fire my cue. But a document that differs from its file and reports itself clean
    /// is how those changes get lost at the end of the night.
    /// </para>
    /// <para>
    /// Cleared only by <see cref="MarkSaved"/>, so the flag means exactly "there is something here the
    /// file does not have".
    /// </para>
    /// </remarks>
    public void MarkDirty(bool documentChanged = true)
    {
        if (_dirtyOutsideTheStack)
            return;

        _dirtyOutsideTheStack = true;
        if (documentChanged)
            Changed?.Invoke();
    }

    /// <summary>
    /// Applies a command and pushes it, coalescing into the open group when it belongs to one.
    /// </summary>
    public void Do(IProjectCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsReadOnly
            && (command is not ICoalescingCommand edit
                || !string.Equals(edit.Key.Property, "note", StringComparison.Ordinal)))
            return;

        command.Apply(Project);

        if (_scope is { } scope)
        {
            // Inside a composite the individual steps are not undo entries of their own; the scope
            // pushes exactly one when it closes. Coalescing applies inside a scope too, so a drag that
            // emits a step per motion event stays a handful of commands rather than hundreds.
            scope.Add(command);

            // A QUIET scope reports once, when it closes. An ordinary one reports each step, because a
            // gesture inside a composite wants the views following the pointer.
            if (!scope.Quiet)
                Changed?.Invoke();

            return;
        }

        _redo.Clear();

        if (command is ICoalescingCommand coalescing
            && _openGroup == coalescing.Key
            && _undo.Count > 0
            && _undo[^1] is ICoalescingCommand open
            && open.Key == coalescing.Key)
        {
            open.MergeFrom(coalescing);
            Changed?.Invoke();
            return;
        }

        _undo.Add(command);
        _openGroup = (command as ICoalescingCommand)?.Key;
        Changed?.Invoke();
    }

    /// <summary>
    /// Ends the current coalescing group - the idle or blur boundary.
    /// </summary>
    /// <remarks>
    /// Called by the UI when a drag ends, a field loses focus, or an idle timer elapses. Without an
    /// explicit boundary, two separate drags of the same fader would merge into one undo step.
    /// </remarks>
    public void CloseGroup() => _openGroup = null;

    /// <summary>
    /// Groups everything done inside into ONE undo step.
    /// </summary>
    /// <remarks>
    /// A multi-selection edit is one thing the operator did, so it is one thing to undo - matching
    /// what they meant rather than what the code looped over. Also how a delete-with-cleanup stays a
    /// single reversible action (register item 11).
    /// </remarks>
    /// <param name="quiet">
    /// Withholds <see cref="Changed"/> until the scope closes, so observers see the finished edit and
    /// not each step of it.
    /// </param>
    /// <remarks>
    /// <para>
    /// Opt-in, and deliberately so. An observer of this journal can be expensive - the shell re-runs
    /// the whole project status pass on every change - and a bulk edit that raises one change per cue
    /// pays that cost per cue. Importing a hundred files ran a hundred validation passes over a project
    /// that was growing with each one.
    /// </para>
    /// <para>
    /// NOT the default, because a continuous gesture can be wrapped in a composite too - a patch-gain
    /// drag, a layer move - and those want the views following the pointer. Only a caller that knows
    /// its scope is a batch rather than a gesture asks for silence.
    /// </para>
    /// </remarks>
    public IDisposable Composite(string description, string domain, bool quiet = false)
    {
        if (IsReadOnly)
            return NestedScope.Instance;

        // A nested composite JOINS the open one rather than throwing. Callers compose: a group-linked
        // patch nudge inside a drag, a delete-with-cleanup inside a multi-selection edit. The outer
        // scope is what the operator did, so it is what one undo should take back.
        if (_scope is not null)
            return NestedScope.Instance;

        CloseGroup();
        var scope = new CompositeScope(this, description, domain, quiet);
        _scope = scope;
        return scope;
    }

    public bool Undo()
    {
        if (IsReadOnly)
            return false;
        if (_scope is not null)
            throw new InvalidOperationException("Cannot undo while a composite edit is open.");
        if (_undo.Count == 0)
            return false;

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Revert(Project);
        _redo.Add(command);
        CloseGroup();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (IsReadOnly)
            return false;
        if (_scope is not null)
            throw new InvalidOperationException("Cannot redo while a composite edit is open.");
        if (_redo.Count == 0)
            return false;

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Apply(Project);
        _undo.Add(command);
        CloseGroup();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Marks the current state as saved. The hash and the dirty flag move together.</summary>
    /// <remarks>
    /// Closes the open coalescing group first. Without that, the next edit to the same property merges
    /// into the command that was on top when the save happened - leaving the project reading CLEAN
    /// while it differs from the file on disk, which is the one lie a dirty flag must never tell.
    /// </remarks>
    public string MarkSaved()
    {
        CloseGroup();
        _savedAt = NextUndo;
        _dirtyOutsideTheStack = false;
        var hash = HaCueProjectFile.ComputeHash(Project);
        Changed?.Invoke();
        return hash;
    }

    /// <summary>Adopts a freshly loaded project and drops the history that belonged to the old one.</summary>
    public void Reset(HaCueProject project)
    {
        Project = project;
        _undo.Clear();
        _redo.Clear();
        _savedAt = null;
        _dirtyOutsideTheStack = false;
        _openGroup = null;
        _scope = null;
        Changed?.Invoke();
    }

    private void CloseScope(CompositeScope scope)
    {
        if (!ReferenceEquals(_scope, scope))
            return;

        _scope = null;

        // An empty composite is not an undo step. A multi-select edit where nothing actually differed
        // would otherwise leave a step that undoes nothing, which reads as a broken undo.
        if (scope.Commands.Count == 0)
        {
            Changed?.Invoke();
            return;
        }

        _redo.Clear();
        _undo.Add(new CompositeCommand(scope.Description, scope.Domain, [.. scope.Commands]));
        _openGroup = null;
        Changed?.Invoke();
    }

    /// <summary>A composite opened inside another: it owns nothing and closes nothing.</summary>
    private sealed class NestedScope : IDisposable
    {
        public static NestedScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class CompositeScope(
        ProjectJournal journal, string description, string domain, bool quiet)
        : IDisposable
    {
        public string Description { get; } = description;
        public string Domain { get; } = domain;

        /// <summary>Whether the journal withholds change notification until this scope closes.</summary>
        public bool Quiet { get; } = quiet;

        public List<IProjectCommand> Commands { get; } = [];

        public void Add(IProjectCommand command)
        {
            // Same rule as the top-level stack: consecutive edits to one property of one subject are
            // one edit. Without it a drag inside a composite keeps every intermediate value alive.
            if (command is ICoalescingCommand incoming
                && Commands.Count > 0
                && Commands[^1] is ICoalescingCommand open
                && open.Key == incoming.Key)
            {
                open.MergeFrom(incoming);
                return;
            }

            Commands.Add(command);
        }

        public void Dispose() => journal.CloseScope(this);
    }
}

/// <summary>Several edits the operator made as one action.</summary>
public sealed class CompositeCommand(
    string description,
    string domain,
    IReadOnlyList<IProjectCommand> commands) : IProjectCommand
{
    public string Description { get; } = description;
    public string Domain { get; } = domain;

    /// <summary>The steps, in the order they were applied.</summary>
    public IReadOnlyList<IProjectCommand> Commands { get; } = commands;

    public void Apply(HaCueProject project)
    {
        foreach (var command in Commands)
            command.Apply(project);
    }

    /// <summary>
    /// Reverts in REVERSE order. Forward order would be wrong the moment two steps touch the same
    /// thing - undoing "delete channel" before "strip the sends that referenced it" puts the channel
    /// back into a document that still has no sends.
    /// </summary>
    public void Revert(HaCueProject project)
    {
        for (var i = Commands.Count - 1; i >= 0; i--)
            Commands[i].Revert(project);
    }
}
