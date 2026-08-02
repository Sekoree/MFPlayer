using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaCue2.Themes;

/// <summary>
/// The Light palette, as a compiled type.
/// </summary>
/// <remarks>
/// A class rather than a bare .axaml loaded by URI: <c>AvaloniaXamlLoader.Load(Uri)</c> resolves the
/// assembly dynamically and is not trim-safe, which this repo builds as an error
/// (<c>IsAotCompatible</c>). Loading <c>this</c> is compiled ahead of time and costs nothing.
/// </remarks>
public partial class LightPalette : ResourceDictionary
{
    public LightPalette() => AvaloniaXamlLoader.Load(this);
}
