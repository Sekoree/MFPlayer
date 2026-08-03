using HaCue2.ViewModels;

namespace HaCue2.Presentation;

/// <summary>
/// The built-in fade laws every curve picker offers, and how each one is drawn.
/// </summary>
/// <remarks>
/// <para>
/// This is product vocabulary, not sample data — it lived in <c>SampleShow</c>, which meant that file
/// could never be deleted however much of the real app landed, and its size stopped meaning anything.
/// A project's OWN custom curves are saved as presets on its document and are not here.
/// </para>
/// <para>
/// The order is load-bearing: the index into this list is the <c>FadeCurve</c> enum value, and
/// "custom" is one past the last law rather than a fifth member of it. A custom curve is never a new
/// enum member — enums round-trip numerically and the sidecar's other reader is the C ABI host, which
/// would decode an unknown member as a different valid law and quietly play the wrong shape.
/// </para>
/// </remarks>
public static class CurveLibrary
{
    /// <summary>
    /// Thumbnail geometries in a 44 × 26 box, drawn top-left (unity) to bottom-right (silence) so a
    /// fade-out reads as a descent.
    /// </summary>
    public static IReadOnlyList<CurveOption> Curves { get; } =
    [
        new("linear", "M2,2 L42,24"),
        new("eq-power", "M2,2 Q30,4 42,24"),
        new("expo", "M2,2 Q10,22 42,24"),
        new("s-curve", "M2,2 C18,2 26,24 42,24"),
        new("custom ✎", "M2,2 L14,6 L22,18 L32,12 L42,24"),
    ];
}
