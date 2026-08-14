using CommunityToolkit.Mvvm.ComponentModel;
using HaCue2.Core.Model;

namespace HaCue2.ViewModels;

/// <summary>
/// The TEXT pane: everything in a text card's render spec (the compiler packs it into the cue's
/// <c>text:</c> URI, so an edit changes the URI and the next fire opens the new card - there is no
/// cache in the app to invalidate). A per-kind editor over the shared
/// <see cref="CueEditPlumbing"/> (review F-11).
/// </summary>
public sealed partial class TextPaneEditor(CueEditPlumbing plumbing, IInspectorEditorContext context)
    : ObservableObject
{
    private TextCueNode? Card => context.Cue as TextCueNode;

    /// <summary>Raises every projection off the current selection - called from the inspector's
    /// <c>Reload</c>.</summary>
    public void RaiseChanged()
    {
        OnPropertyChanged(nameof(CardText));
        OnPropertyChanged(nameof(CardFont));
        OnPropertyChanged(nameof(CardSize));
        OnPropertyChanged(nameof(CardBold));
        OnPropertyChanged(nameof(CardItalic));
        OnPropertyChanged(nameof(CardInk));
        OnPropertyChanged(nameof(CardGround));
        OnPropertyChanged(nameof(CardAlignIndex));
        OnPropertyChanged(nameof(CardAnchorIndex));
        OnPropertyChanged(nameof(CardOutline));
        OnPropertyChanged(nameof(CardOutlineInk));
        OnPropertyChanged(nameof(CardDuration));
        OnPropertyChanged(nameof(CardFadeIn));
        OnPropertyChanged(nameof(CardFadeOut));
        OnPropertyChanged(nameof(CardHint));
    }

    public string CardText
    {
        get => Card?.Text ?? "";
        set => EditCard("text", card => card.Text, (card, text) => card.Text = text, value ?? "");
    }

    /// <summary>
    /// The face, or empty for the app's own.
    /// </summary>
    /// <remarks>
    /// A hint, matched the way an audio line's device name is: a booth machine may not have the face a
    /// show was authored with, and falling back to something readable beats refusing to draw.
    /// </remarks>
    public string CardFont
    {
        get => Card?.FontFamily ?? "";
        set => EditCard("font",
            card => card.FontFamily, (card, name) => card.FontFamily = name, (value ?? "").Trim());
    }

    /// <summary>Cap height as a fraction of the frame, so the card survives a canvas resize.</summary>
    public double CardSize
    {
        get => Card?.FontScale ?? 0.12;
        set => EditCard("fontScale",
            card => card.FontScale, (card, scale) => card.FontScale = scale,
            Math.Clamp(value, 0.01, 1));
    }

    public bool CardBold
    {
        get => Card?.Bold == true;
        set => EditCard("bold", card => card.Bold, (card, on) => card.Bold = on, value);
    }

    public bool CardItalic
    {
        get => Card?.Italic == true;
        set => EditCard("italic", card => card.Italic, (card, on) => card.Italic = on, value);
    }

    public string CardInk
    {
        get => Card?.Foreground ?? "#FFFFFF";
        set => EditCard("foreground",
            card => card.Foreground, (card, hex) => card.Foreground = hex, Hex(value, "#FFFFFF"));
    }

    /// <summary>The ground, or empty for a transparent card that sits over whatever is underneath.</summary>
    public string CardGround
    {
        get => Card?.Background ?? "";
        set => EditCard("background",
            card => card.Background, (card, hex) => card.Background = hex, Hex(value, ""));
    }

    public IReadOnlyList<string> CardAligns { get; } = ["left", "centre", "right"];

    public int CardAlignIndex
    {
        get => Card is { } card ? (int)card.Align : -1;
        set
        {
            if (value >= 0)
                EditCard("align", card => card.Align, (card, align) => card.Align = align,
                    (TextAlign)value);
        }
    }

    public IReadOnlyList<string> CardAnchors { get; } = ["top", "middle", "bottom"];

    public int CardAnchorIndex
    {
        get => Card is { } card ? (int)card.Anchor : -1;
        set
        {
            if (value >= 0)
                EditCard("anchor", card => card.Anchor, (card, anchor) => card.Anchor = anchor,
                    (TextAnchor)value);
        }
    }

    /// <summary>An outline behind the ink - what makes a caption readable over picture.</summary>
    public double CardOutline
    {
        get => Card?.OutlineWidth ?? 0;
        set => EditCard("outlineWidth",
            card => card.OutlineWidth, (card, width) => card.OutlineWidth = width,
            Math.Clamp(value, 0, 0.1));
    }

    public string CardOutlineInk
    {
        get => Card?.Outline ?? "#000000";
        set => EditCard("outline",
            card => card.Outline, (card, hex) => card.Outline = hex, Hex(value, "#000000"));
    }

    /// <summary>How long the card is held. Zero holds it until something stops it.</summary>
    public int CardDuration
    {
        get => Card?.DurationMs ?? 0;
        set => EditCard("duration",
            card => card.DurationMs, (card, ms) => card.DurationMs = ms,
            Math.Clamp(value, 0, 3_600_000));
    }

    public int CardFadeIn
    {
        get => Card?.FadeInMs ?? 0;
        set => EditCard("fadeIn", card => card.FadeInMs, (card, ms) => card.FadeInMs = ms,
            Math.Clamp(value, 0, 60_000));
    }

    public int CardFadeOut
    {
        get => Card?.FadeOutMs ?? 0;
        set => EditCard("fadeOut", card => card.FadeOutMs, (card, ms) => card.FadeOutMs = ms,
            Math.Clamp(value, 0, 60_000));
    }

    /// <summary>What the card will do, said on the pane rather than discovered on stage.</summary>
    public string CardHint => Card is not { } card
        ? ""
        : card.Text.Trim().Length == 0
            ? "no words yet - the cue will fire and show nothing"
            : card.Placements.Count == 0
                ? "not on any canvas yet - add a placement on the Video tab"
                : card.DurationMs > 0
                    ? $"held for {card.DurationMs / 1000d:0.##} s, then ends on its own"
                    : "held on screen until something stops it";

    /// <summary>
    /// A typed colour as "#RRGGBB", or the fallback when it is not one.
    /// </summary>
    /// <remarks>
    /// The DIGITS are checked, not just the length. Anything seven characters long used to be accepted,
    /// so "#ZZZZZZ" was stored and shown back as though it were a colour - while the compiler quietly
    /// fell back to white, and the card came up a colour the inspector never displayed.
    /// </remarks>
    private static string Hex(string? value, string fallback)
    {
        var text = (value ?? "").Trim().TrimStart('#');

        return text.Length == 6 && text.All(char.IsAsciiHexDigit)
            ? '#' + text.ToUpperInvariant()
            : fallback;
    }

    // Every control on the TEXT pane goes through here, so making this one method selection-aware is
    // what makes "select three cards, set the font" work - it used to change the first one only.
    private void EditCard<T>(
        string property, Func<TextCueNode, T> read, Action<TextCueNode, T> write, T value)
    {
        if (Card is not { } card)
            return;

        plumbing.EditEach(card, property, "cues", read, write, value, "edit text cue");
    }
}
