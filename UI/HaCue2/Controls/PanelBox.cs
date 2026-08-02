using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace HaCue2.Controls;

/// <summary>
/// The mockup's <c>.pnl</c> — a bordered surface with a header strip. Every framed region in HaCue2
/// is one of these, so the header's typography, height and rule are decided once.
/// </summary>
/// <remarks>
/// It carries three header slots rather than one because the mockup's header consistently reads
/// left-to-right as <i>what this is · what it currently holds · a fact about it</i>: "Q13.1 · Storm
/// bed" + "media cue", "Act 1 › all cues" + "84 cues · undo: …". Folding those into a single string
/// would force every view-model to do its own formatting and lose the ink-3 hint styling.
/// <list type="bullet">
///   <item><see cref="HeaderedContentControl.Header"/> — the name, in ink-2 mono caps.</item>
///   <item><see cref="Subhead"/> — an optional qualifier beside it, brighter.</item>
///   <item><see cref="Hint"/> — right-aligned, ink-3, never uppercase (it is prose, not a label).</item>
/// </list>
/// </remarks>
public class PanelBox : HeaderedContentControl
{
    /// <summary>Optional qualifier rendered beside the header at full ink.</summary>
    public static readonly StyledProperty<object?> SubheadProperty =
        AvaloniaProperty.Register<PanelBox, object?>(nameof(Subhead));

    /// <summary>Right-aligned counter or note; sentence case, never a label.</summary>
    public static readonly StyledProperty<object?> HintProperty =
        AvaloniaProperty.Register<PanelBox, object?>(nameof(Hint));

    /// <summary>Content docked below the body, outside the scroll region (action rows, chains).</summary>
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<PanelBox, object?>(nameof(Footer));

    public object? Subhead
    {
        get => GetValue(SubheadProperty);
        set => SetValue(SubheadProperty, value);
    }

    public object? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
