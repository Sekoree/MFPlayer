using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HaCue2.Core.Model;

namespace HaCue2.Session;

/// <summary>
/// Draws a text cue's words into a picture the engine can play.
/// </summary>
/// <remarks>
/// <para>
/// The seam a text cue needs. The document stores WORDS — portable, diffable, translatable — and the
/// engine plays PICTURES; something has to turn one into the other, and it has to be the app, because
/// rasterising text needs a font stack and a font stack belongs to an application rather than to a
/// show file or to a headless compiler.
/// </para>
/// <para>
/// Rendered to the machine's media cache and keyed by CONTENT, so two cues that would draw the same
/// card share one file and editing a word invalidates it without anything tracking what changed. The
/// cache is derived: deleting it costs a re-render and nothing else.
/// </para>
/// <para>
/// PNG rather than a frame handed straight to the session: a clip is opened from a path, every stage
/// after that — pre-roll, placement, mapping, recording — already works on one, and a file on disk can
/// be looked at when a card comes out wrong.
/// </para>
/// </remarks>
public sealed class TextCards(string cacheRoot)
{
    /// <summary>
    /// The canvas a card is drawn at.
    /// </summary>
    /// <remarks>
    /// Fixed at 1080p rather than the composition's size, and the placement scales it from there. A
    /// card re-rendered every time somebody resized a canvas would be a file rewritten on a keystroke,
    /// and text scaled from 1080p is indistinguishable at the sizes a card is actually shown at.
    /// </remarks>
    private const int Width = 1920;

    private const int Height = 1080;

    private readonly Dictionary<Guid, string> _rendered = [];
    private readonly Dictionary<Guid, string> _keys = [];

    /// <summary>Where each text cue's picture is, for the compiler.</summary>
    public IReadOnlyDictionary<Guid, string> Paths => _rendered;

    /// <summary>What could not be drawn, by cue id — reported rather than silently blank.</summary>
    public IReadOnlyDictionary<Guid, string> Problems => _problems;

    private readonly Dictionary<Guid, string> _problems = [];

    /// <summary>
    /// Renders every text cue whose words or style have changed, and forgets deleted ones.
    /// </summary>
    /// <remarks>
    /// Called before each compile. Cheap when nothing changed — the key comparison is a string compare
    /// per text cue, and a document with none does no work at all.
    /// </remarks>
    /// <returns>True when anything was drawn, so the caller knows the document is worth recompiling.</returns>
    public bool Refresh(HaCueProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var cards = project.AllCues().OfType<TextCueNode>().ToList();
        var wanted = cards.Select(card => card.Id).ToHashSet();
        var drawn = false;

        foreach (var stale in _rendered.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            _rendered.Remove(stale);
            _keys.Remove(stale);
            _problems.Remove(stale);
        }

        foreach (var card in cards)
        {
            // Nothing to draw is not a failure: a cue somebody has just added has no words yet, and a
            // card of nothing would be a black rectangle over the show.
            if (card.Text.Trim().Length == 0)
            {
                _rendered.Remove(card.Id);
                _keys.Remove(card.Id);
                continue;
            }

            if (_keys.TryGetValue(card.Id, out var previous)
                && previous == card.RenderKey
                && _rendered.TryGetValue(card.Id, out var path)
                && File.Exists(path))
                continue;

            if (Draw(card) is { } file)
            {
                _rendered[card.Id] = file;
                _keys[card.Id] = card.RenderKey;
                _problems.Remove(card.Id);
                drawn = true;
                continue;
            }

            _rendered.Remove(card.Id);
            _keys.Remove(card.Id);
        }

        return drawn;
    }

    /// <summary>Draws one card, or reports why it could not. Never throws out of a compile.</summary>
    private string? Draw(TextCueNode card)
    {
        var path = Path.Combine(cacheRoot, "text", $"{Hash(card.RenderKey)}.png");

        try
        {
            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));

            using (var context = bitmap.CreateDrawingContext())
            {
                if (Colour(card.Background) is { } ground)
                    context.FillRectangle(new SolidColorBrush(ground), new Rect(0, 0, Width, Height));

                var text = new FormattedText(
                    card.Text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(
                        card.FontFamily.Trim().Length > 0
                            ? new FontFamily(card.FontFamily)
                            : FontFamily.Default,
                        card.Italic ? FontStyle.Italic : FontStyle.Normal,
                        card.Bold ? FontWeight.Bold : FontWeight.Normal),
                    Math.Clamp(card.FontScale, 0.01, 1) * Height,
                    new SolidColorBrush(Colour(card.Foreground) ?? Colors.White))
                {
                    // Wrapped inside a safe margin rather than at the frame edge: a card whose words
                    // touch the bleed is a card that loses a letter on an overscanning projector.
                    MaxTextWidth = Width * 0.9,
                    TextAlignment = card.Align switch
                    {
                        TextAlign.Left => TextAlignment.Left,
                        TextAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Center,
                    },
                };

                var top = card.Anchor switch
                {
                    TextAnchor.Top => Height * 0.05,
                    TextAnchor.Bottom => Height * 0.95 - text.Height,
                    _ => (Height - text.Height) / 2,
                };

                context.DrawText(text, new Point(Width * 0.05, top));
            }

            bitmap.Save(path, new PngBitmapEncoderOptions());
            return path;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException or ArgumentException)
        {
            // A card that cannot be drawn leaves its cue clipless and says why. Throwing would take
            // the whole compile — and with it every other cue in the show — down with it.
            _problems[card.Id] = failure.Message;
            return null;
        }
    }

    /// <summary>"#RRGGBB" as a colour; null for empty, which means "no fill".</summary>
    private static Color? Colour(string text)
    {
        var hex = (text ?? "").Trim().TrimStart('#');

        return hex.Length == 6
               && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)
            ? Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : null;
    }

    /// <summary>A short, stable file name for a card's content.</summary>
    private static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
}
