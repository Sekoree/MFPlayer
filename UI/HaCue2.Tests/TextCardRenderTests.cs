using HaCue2.Core.Model;
using HaCue2.Session;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// A text cue actually drawn.
/// </summary>
/// <remarks>
/// The compiler tests pin what the document says; this pins that words become a PICTURE. It needs a
/// real rendering platform — the headless harness supplies one — because the whole reason this half
/// lives in the app is that rasterising text needs a font stack.
/// </remarks>
public class TextCardRenderTests
{
    private static string Scratch() =>
        Directory.CreateTempSubdirectory("hacue2-cards").FullName;

    [Fact]
    public Task WordsBecomeAPicture() => ShellFixture.WithShell(_ =>
    {
        var root = Scratch();

        try
        {
            var cards = new TextCards(root);
            var card = new TextCueNode { Number = "1", Label = "Title", Text = "ACT ONE" };
            var project = new HaCueProject
            {
                CueLists = [new CueList { Name = "Main", Cues = [card] }],
            };

            Assert.True(cards.Refresh(project));

            var path = Assert.Contains(card.Id, (IDictionary<Guid, string>)cards.Paths);

            Assert.True(File.Exists(path), "a text cue was rendered but no file was written");
            // A 1920×1080 PNG of a title is not a handful of bytes. Anything this small is an empty
            // canvas, which is the failure mode a "did it write a file" check would miss.
            Assert.True(new FileInfo(path).Length > 1_000);
            Assert.Empty(cards.Problems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    /// <summary>
    /// A card with no words is not an error — and not a black rectangle over the show either.
    /// </summary>
    /// <remarks>
    /// This is the state every text cue is in for the seconds between adding it and typing into it.
    /// </remarks>
    [Fact]
    public Task ACardWithNoWordsDrawsNothing() => ShellFixture.WithShell(_ =>
    {
        var root = Scratch();

        try
        {
            var cards = new TextCards(root);
            var card = new TextCueNode { Number = "1", Label = "Title", Text = "   " };
            var project = new HaCueProject
            {
                CueLists = [new CueList { Name = "Main", Cues = [card] }],
            };

            Assert.False(cards.Refresh(project));
            Assert.Empty(cards.Paths);
            Assert.Empty(cards.Problems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    /// <summary>Re-rendering is content-keyed: unchanged words cost nothing, changed words redraw.</summary>
    [Fact]
    public Task ACardIsRedrawnOnlyWhenItsContentChanges() => ShellFixture.WithShell(_ =>
    {
        var root = Scratch();

        try
        {
            var cards = new TextCards(root);
            var card = new TextCueNode { Number = "1", Label = "Title", Text = "ACT ONE" };
            var project = new HaCueProject
            {
                CueLists = [new CueList { Name = "Main", Cues = [card] }],
            };

            Assert.True(cards.Refresh(project));
            var first = cards.Paths[card.Id];

            // Nothing the drawing is made from changed — the label is not on the card.
            card.Label = "Renamed";
            Assert.False(cards.Refresh(project));
            Assert.Equal(first, cards.Paths[card.Id]);

            card.Text = "ACT TWO";
            Assert.True(cards.Refresh(project));
            Assert.NotEqual(first, cards.Paths[card.Id]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });

    /// <summary>A deleted cue's entry goes, so the compiler cannot emit a clip for a cue that is gone.</summary>
    [Fact]
    public Task ADeletedCardIsForgotten() => ShellFixture.WithShell(_ =>
    {
        var root = Scratch();

        try
        {
            var cards = new TextCards(root);
            var card = new TextCueNode { Number = "1", Label = "Title", Text = "ACT ONE" };
            var list = new CueList { Name = "Main", Cues = [card] };
            var project = new HaCueProject { CueLists = [list] };

            cards.Refresh(project);
            Assert.Single(cards.Paths);

            list.Cues.Clear();
            cards.Refresh(project);

            Assert.Empty(cards.Paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    });
}
