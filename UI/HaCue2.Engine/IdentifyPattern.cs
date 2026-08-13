using S.Media.Core.Video;
using HaCue2.Core.Model;

namespace HaCue2.Engine;

/// <summary>
/// The frame IDENTIFY flashes: an output's own name, in letters big enough to read from the room.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own five-by-seven font, and that is the point.</b> This runs in the engine, which has no
/// window, no Skia and no font stack - and a booth machine's font set is exactly the kind of thing
/// that differs from the laptop a show was authored on. Glyphs drawn as pixels cannot fail to
/// resolve, cannot fall back to something unreadable, and look identical on every box.
/// </para>
/// <para>
/// The glyph table is written as pictures of the letters rather than as hex, because the one thing
/// anybody will ever do to this file is fix a letter that looks wrong.
/// </para>
/// </remarks>
public static class IdentifyPattern
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;

    /// <summary>One blank column between letters, so words do not run together.</summary>
    private const int Tracking = 1;

    private static readonly Dictionary<char, string[]> Glyphs = Build();

    /// <summary>
    /// The output's name on a flat field, sized to the composition.
    /// </summary>
    /// <remarks>
    /// BGRA32 with a plain managed buffer: this is one frame shown for a couple of seconds, so there is
    /// nothing to gain from a hardware surface and a great deal to lose from needing one.
    /// </remarks>
    public static VideoFrame Render(
        string text, int width, int height, IReadOnlyList<MappingSection>? sections = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var pixels = new byte[width * height * 4];
        var stride = width * 4;

        // A saturated blue nobody's content is likely to be, so an operator glancing across a rack of
        // screens can tell at once which one is being identified.
        Fill(pixels, 0x8C, 0x2B, 0x11);
        Grid(pixels, width, height, stride);
        SectionBorders(pixels, width, height, stride, sections ?? []);
        Corners(pixels, width, height, stride);
        Border(pixels, width, height, stride);
        Draw(pixels, width, height, stride, (text ?? "").Trim());

        return new VideoFrame(
            TimeSpan.Zero,
            new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(25, 1)),
            pixels,
            stride,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
    }

    private static void Grid(byte[] pixels, int width, int height, int stride)
    {
        var thickness = Math.Max(1, Math.Min(width, height) / 500);
        for (var division = 1; division < 10; division++)
        {
            var x = width * division / 10;
            var y = height * division / 10;
            for (var offset = -thickness; offset <= thickness; offset++)
            {
                for (var row = 0; row < height; row++)
                    SetColor(pixels, row * stride + Math.Clamp(x + offset, 0, width - 1) * 4,
                        0x70, 0x70, 0x70);
                for (var column = 0; column < width; column++)
                    SetColor(pixels, Math.Clamp(y + offset, 0, height - 1) * stride + column * 4,
                        0x70, 0x70, 0x70);
            }
        }
    }

    private static void SectionBorders(
        byte[] pixels, int width, int height, int stride, IReadOnlyList<MappingSection> sections)
    {
        foreach (var section in sections.Where(section => section.Enabled))
        {
            var left = Math.Clamp((int)Math.Round(section.SourceX * width), 0, width - 1);
            var top = Math.Clamp((int)Math.Round(section.SourceY * height), 0, height - 1);
            var right = Math.Clamp((int)Math.Round((section.SourceX + section.SourceWidth) * width), 0, width - 1);
            var bottom = Math.Clamp((int)Math.Round((section.SourceY + section.SourceHeight) * height), 0, height - 1);
            for (var x = left; x <= right; x++)
            {
                SetColor(pixels, top * stride + x * 4, 0x00, 0xE6, 0xFF);
                SetColor(pixels, bottom * stride + x * 4, 0x00, 0xE6, 0xFF);
            }
            for (var y = top; y <= bottom; y++)
            {
                SetColor(pixels, y * stride + left * 4, 0x00, 0xE6, 0xFF);
                SetColor(pixels, y * stride + right * 4, 0x00, 0xE6, 0xFF);
            }
        }
    }

    private static void Corners(byte[] pixels, int width, int height, int stride)
    {
        var size = Math.Max(8, Math.Min(width, height) / 12);
        Corner(pixels, width, height, stride, 0, 0, size, 0x20, 0x20, 0xFF);
        Corner(pixels, width, height, stride, width - size, 0, size, 0x20, 0xFF, 0x20);
        Corner(pixels, width, height, stride, 0, height - size, size, 0xFF, 0x40, 0x20);
        Corner(pixels, width, height, stride, width - size, height - size, size, 0x20, 0xE6, 0xFF);
    }

    private static void Corner(
        byte[] pixels, int width, int height, int stride, int left, int top, int size,
        byte blue, byte green, byte red)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + size); y++)
        for (var x = Math.Max(0, left); x < Math.Min(width, left + size); x++)
            SetColor(pixels, y * stride + x * 4, blue, green, red);
    }

    private static void Fill(byte[] pixels, byte blue, byte green, byte red)
    {
        for (var at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = blue;
            pixels[at + 1] = green;
            pixels[at + 2] = red;
            pixels[at + 3] = 0xFF;
        }
    }

    /// <summary>
    /// A frame around the edge.
    /// </summary>
    /// <remarks>
    /// Not decoration: it is how an operator sees whether the projector is showing the WHOLE canvas.
    /// A feed cropped by an overscanning display looks perfectly normal until something is drawn at
    /// the edge of it.
    /// </remarks>
    private static void Border(byte[] pixels, int width, int height, int stride)
    {
        var thickness = Math.Max(2, Math.Min(width, height) / 90);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (x >= thickness && x < width - thickness
                    && y >= thickness && y < height - thickness)
                    continue;

                Set(pixels, (y * stride) + (x * 4));
            }
        }
    }

    private static void Draw(byte[] pixels, int width, int height, int stride, string text)
    {
        if (text.Length == 0)
            text = "?";

        var cells = (text.Length * (GlyphWidth + Tracking)) - Tracking;

        // Scaled to fill about two thirds of the canvas, and never smaller than one pixel per cell -
        // a name too long for the screen shrinks rather than being cut off, because the half of a name
        // that fitted would identify the wrong output.
        var scale = Math.Max(1, Math.Min(width * 2 / (3 * cells), height * 2 / (3 * GlyphHeight)));

        var originX = (width - (cells * scale)) / 2;
        var originY = (height - (GlyphHeight * scale)) / 2;

        for (var index = 0; index < text.Length; index++)
        {
            var glyph = Glyphs.GetValueOrDefault(char.ToUpperInvariant(text[index]), Glyphs['?']);
            var left = originX + (index * (GlyphWidth + Tracking) * scale);

            for (var row = 0; row < GlyphHeight; row++)
            {
                for (var column = 0; column < GlyphWidth; column++)
                {
                    if (glyph[row][column] != '#')
                        continue;

                    Block(pixels, width, height, stride,
                        left + (column * scale), originY + (row * scale), scale);
                }
            }
        }
    }

    private static void Block(byte[] pixels, int width, int height, int stride, int x, int y, int scale)
    {
        for (var down = 0; down < scale; down++)
        {
            var row = y + down;

            if (row < 0 || row >= height)
                continue;

            for (var across = 0; across < scale; across++)
            {
                var column = x + across;

                if (column >= 0 && column < width)
                    Set(pixels, (row * stride) + (column * 4));
            }
        }
    }

    /// <summary>White, opaque - the ink both the border and the letters are drawn in.</summary>
    private static void Set(byte[] pixels, int at)
    {
        pixels[at] = 0xFF;
        pixels[at + 1] = 0xFF;
        pixels[at + 2] = 0xFF;
        pixels[at + 3] = 0xFF;
    }

    private static void SetColor(byte[] pixels, int at, byte blue, byte green, byte red)
    {
        pixels[at] = blue;
        pixels[at + 1] = green;
        pixels[at + 2] = red;
        pixels[at + 3] = 0xFF;
    }

    /// <summary>
    /// The glyph table, drawn.
    /// </summary>
    /// <remarks>
    /// Written as pictures of the letters, because the one thing anybody will ever do to this table is
    /// fix a letter that looks wrong - and a run of hex is the worst possible way to find which one.
    /// </remarks>
    private static Dictionary<char, string[]> Build()
    {
        var table = new Dictionary<char, string[]>
        {
            ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
            ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
            ['C'] = [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."],
            ['D'] = ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
            ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
            ['F'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#...."],
            ['G'] = [".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###."],
            ['H'] = ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
            ['I'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
            ['J'] = ["..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.."],
            ['K'] = ["#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"],
            ['L'] = ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
            ['M'] = ["#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#", "#...#"],
            ['N'] = ["#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#"],
            ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
            ['P'] = ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
            ['Q'] = [".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"],
            ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
            ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
            ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
            ['U'] = ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
            ['V'] = ["#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."],
            ['W'] = ["#...#", "#...#", "#...#", "#...#", "#.#.#", "##.##", "#...#"],
            ['X'] = ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
            ['Y'] = ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
            ['Z'] = ["#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"],
            ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
            ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
            ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
            ['3'] = ["#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###."],
            ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
            ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
            ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
            ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
            ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
            ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
            [' '] = [".....", ".....", ".....", ".....", ".....", ".....", "....."],
            ['-'] = [".....", ".....", ".....", "#####", ".....", ".....", "....."],
            ['.'] = [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
            [':'] = [".....", ".##..", ".##..", ".....", ".##..", ".##..", "....."],
            ['/'] = ["....#", "....#", "...#.", "..#..", ".#...", "#....", "#...."],
            ['?'] = [".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."],
        };

        // Seven rows of five, every one of them. A row of the wrong width would shift the rest of the
        // glyph sideways and render as garbage on a projector, at the moment somebody is relying on it.
        foreach (var (character, glyph) in table)
        {
            if (glyph.Length != GlyphHeight || glyph.Any(row => row.Length != GlyphWidth))
                throw new InvalidOperationException(
                    $"the glyph for '{character}' is not {GlyphWidth}×{GlyphHeight}");
        }

        return table;
    }
}
