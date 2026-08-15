using CommunityToolkit.Mvvm.ComponentModel;

namespace HaCue2.ViewModels;

/// <summary>What a prompt field looks like.</summary>
public enum PromptFieldKind
{
    Text,
    Number,
    Choice,
    Toggle,

    /// <summary>
    /// A folder on this machine, with a browse button beside it.
    /// </summary>
    /// <remarks>
    /// Typing a path is how a media root ends up off by one directory and nothing resolves at the
    /// venue. Still typable, because pasting one is faster than clicking through to it and because a
    /// path that does not exist yet cannot be picked.
    /// </remarks>
    Folder,

    /// <summary>A file on this machine, with a browse button beside it.</summary>
    File,

    /// <summary>
    /// A button that runs a caller-supplied action - a probe, a test, a scan. The field's Value is
    /// the button's label; results land in the Hint (and in other fields the caller fills).
    /// </summary>
    Action,

    /// <summary>
    /// Free text with a list of suggestions beside it - an editable combo box.
    /// </summary>
    /// <remarks>
    /// For a value that is nearly always one of a handful and occasionally none of them. A window size
    /// is the case it was added for: 1280×720 and 1920×1080 cover almost every use, and the operator
    /// who needs 1024×768 for an old projector must still be able to say so.
    /// </remarks>
    Suggestion,
}

/// <summary>One line of a prompt: a label, a value, and how to edit it.</summary>
public sealed partial class PromptField : ObservableObject
{
    public required string Label { get; init; }

    public PromptFieldKind Kind { get; init; } = PromptFieldKind.Text;

    /// <summary>
    /// Options for <see cref="PromptFieldKind.Choice"/>.
    /// </summary>
    /// <remarks>
    /// Settable rather than init-only so one field can NARROW another: picking a host API refills the
    /// device list, which is the difference between choosing from four devices and reading fifteen
    /// near-identical names to find the one that is actually the interface.
    /// </remarks>
    [ObservableProperty]
    private IReadOnlyList<string> _options = [];

    /// <summary>Raised when the operator picks a different option - how a dependent field is refilled.</summary>
    public event Action<PromptField>? Picked;

    partial void OnSelectedIndexChanged(int value) => Picked?.Invoke(this);

    /// <summary>
    /// A line under the field, for the thing that is not obvious from its name.
    /// </summary>
    /// <remarks>
    /// Settable and observed, not init-only: a hint that depends on another field has to be able to
    /// change with it. The audio line's rate field says what the CHOSEN device reports against what the
    /// show mixes at, and a fixed sentence could not name either.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    private string _hint = "";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private bool _isOn;

    /// <summary>
    /// Whether this field can be edited right now.
    /// </summary>
    /// <remarks>
    /// One field can make another meaningless: a fullscreen output has no window size to set, and a
    /// windowed one does not need a screen chosen for it. Greying the field says so where a hidden one
    /// would just look like a dialog that changes shape, and the value is kept either way.
    /// </remarks>
    [ObservableProperty]
    private bool _isEnabled = true;

    public bool IsText => Kind is PromptFieldKind.Text or PromptFieldKind.Number;

    /// <summary>A button field - see <see cref="PromptFieldKind.Action"/>.</summary>
    public bool IsAction => Kind == PromptFieldKind.Action;

    /// <summary>Raised when an <see cref="PromptFieldKind.Action"/> field's button is pressed.</summary>
    public event Action<PromptField>? Invoked;

    public void Invoke() => Invoked?.Invoke(this);

    /// <summary>Free text with a preset list beside it.</summary>
    public bool IsSuggestion => Kind == PromptFieldKind.Suggestion;
    public bool IsChoice => Kind == PromptFieldKind.Choice;
    public bool IsToggle => Kind == PromptFieldKind.Toggle;

    /// <summary>Whether this field takes a path - a text box plus a browse button.</summary>
    public bool IsPath => Kind is PromptFieldKind.Folder or PromptFieldKind.File;

    public bool IsFolder => Kind == PromptFieldKind.Folder;
    public bool HasHint => Hint.Length > 0;

    /// <summary>The value as a number, or <paramref name="fallback"/> when it is not one.</summary>
    public int Number(int fallback = 0) =>
        int.TryParse(Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : fallback;

    public double Decimal(double fallback = 0) =>
        double.TryParse(Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : fallback;

    /// <summary>The chosen option, or empty when there is none.</summary>
    public string Choice =>
        SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : "";
}

/// <summary>
/// A small modal form: some fields, Cancel, and a confirm button that does one journaled thing.
/// </summary>
/// <remarks>
/// <para>
/// One shell for every "name a new thing" dialog in the app rather than a window each. The mockup has
/// two dozen buttons ending in "…" and almost all of them are the same shape - a couple of fields and
/// a verb - so the alternative is two dozen near-identical windows that drift apart in spacing, tab
/// order and button placement. Anything genuinely different (the curve editor, the subtitle picker)
/// still gets its own window.
/// </para>
/// <para>
/// The dialog does not know what it edits. It collects values and calls <see cref="Apply"/>, which the
/// caller supplies and which is where the journal command lives - so every dialog in the app is
/// undoable by construction rather than by each author remembering.
/// </para>
/// </remarks>
public sealed partial class PromptViewModel : ObservableObject
{
    /// <summary>The preview shape, for a dialog opened with nothing behind it.</summary>
    public PromptViewModel()
    {
        Title = "Prompt";
        Fields = [];
        Apply = _ => { };
    }

    public PromptViewModel(
        string title,
        string hint,
        IReadOnlyList<PromptField> fields,
        Action<PromptViewModel> apply,
        string confirm = "ADD",
        string alternative = "",
        Action? applyAlternative = null)
    {
        Title = title;
        Hint = hint;
        Fields = fields;
        Apply = apply;
        Confirm = confirm;
        Alternative = alternative;
        ApplyAlternative = applyAlternative;
    }

    public string Title { get; }
    public string Hint { get; } = "";
    public string Confirm { get; } = "ADD";
    public IReadOnlyList<PromptField> Fields { get; }

    /// <summary>
    /// A THIRD answer, beside confirm and cancel. Empty when the question only has two.
    /// </summary>
    /// <remarks>
    /// Added for the one question in the app that genuinely has three answers: closing a project with
    /// unsaved edits is save / discard / go back, and collapsing it to two makes one of those three
    /// unreachable - which for "discard" means an operator who wants to abandon a bad edit has to save
    /// it first.
    /// </remarks>
    public string Alternative { get; } = "";

    public bool HasAlternative => Alternative.Length > 0;

    private Action? ApplyAlternative { get; }

    /// <summary>Runs the third answer. Cancel still never runs anything.</summary>
    public void CommitAlternative() => ApplyAlternative?.Invoke();

    private Action<PromptViewModel> Apply { get; }

    /// <summary>Field by label - how a caller reads back what was typed.</summary>
    public PromptField this[string label] =>
        Fields.FirstOrDefault(candidate => candidate.Label == label)
        ?? throw new KeyNotFoundException($"no prompt field labelled “{label}”");

    /// <summary>
    /// Whether the confirm button does anything.
    /// </summary>
    /// <remarks>
    /// A name is required wherever there is one, because a thing called "" is unfindable in every list
    /// that will show it afterwards.
    /// </remarks>
    public bool CanConfirm =>
        Fields.FirstOrDefault(candidate => candidate.Label == "Name") is not { } name
        || name.Value.Trim().Length > 0;

    /// <summary>Runs the caller's edit. Cancel simply never calls this, so nothing was an edit.</summary>
    public void Commit() => Apply(this);
}
