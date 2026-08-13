using HaCue2.Controls;
using HaCue2.ViewModels;
using Xunit;

namespace HaCue2.Tests;

/// <summary>
/// The patch matrix's row-label column.
/// </summary>
/// <remarks>
/// The patch labels rows "&lt;line name&gt; · Out &lt;n&gt;", and a real interface's name does not
/// fit any fixed width - at the pane's 118 px, "Ryzen HD Audio Controller Analog Stereo · Out 1"
/// clipped to its first few glyphs, on exactly the row an operator reads to tell one physical
/// output from another. The styled width is a minimum; the column widens to the longest header.
/// </remarks>
public class MatrixHeaderWidthTests
{
    private static MatrixRow Row(string header) => new(header, []);

    [Fact]
    public Task ARealDeviceNameWidensTheRowHeaderColumnPastTheMinimum() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var view = new MatrixView
            {
                RowHeaderWidth = 118,
                Rows = [Row("Ryzen HD Audio Controller Analog Stereo · Out 1")],
            };

            var width = Assert.IsType<double>(view.Resources[MatrixView.RowHeaderWidthKey]);
            Assert.True(width > 200, $"a 46-glyph header measured only {width} px");
        });

    [Fact]
    public Task ShortHeadersKeepTheAuthoredMinimumSoTheSendMatrixStaysCompact() =>
        ShellFixture.Session.DispatchGuarded(() =>
        {
            var view = new MatrixView
            {
                RowHeaderWidth = 44,
                Rows = [Row("Src L"), Row("Src R")],
            };

            var width = Assert.IsType<double>(view.Resources[MatrixView.RowHeaderWidthKey]);
            Assert.Equal(44, width);
        });
}
