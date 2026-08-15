using HaCue2.Core.Model;
using HaCue2.Core.Serialization;
using HaCue2.Engine;
using Xunit;

namespace HaCue2.Core.Tests;

/// <summary>
/// The NDI output's wire-format options (2026-08-14): the raster rides the mapping stage, and the
/// options travel with the show.
/// </summary>
public sealed class NdiOutputFormatTests
{
    [Fact]
    public void ACleanNdiFeedWithARasterGetsAFullCanvasMappingAtThatSize()
    {
        var output = new VideoOutputDefinition
        {
            Name = "Program",
            Kind = VideoOutputKind.Ndi,
            NdiWidth = 1280,
            NdiHeight = 720,
        };

        var spec = OutputMapping.Spec(output, 1920, 1080);

        Assert.NotNull(spec);
        Assert.Equal((1280, 720), (spec.OutputWidth, spec.OutputHeight));
        var section = Assert.Single(spec.Sections);
        Assert.Equal((0d, 0d, 1d, 1d), (section.SrcX, section.SrcY, section.SrcWidth, section.SrcHeight));
        Assert.Equal((0d, 0d, 1280d, 720d),
            (section.DestX, section.DestY, section.DestWidth, section.DestHeight));
    }

    [Fact]
    public void ACleanNdiFeedWithoutARasterStaysACleanFeed()
    {
        var output = new VideoOutputDefinition { Name = "Program", Kind = VideoOutputKind.Ndi };

        Assert.Null(OutputMapping.Spec(output, 1920, 1080));
    }

    [Fact]
    public void AnAuthoredNdiSliceResolvesAgainstTheWireRaster()
    {
        var output = new VideoOutputDefinition
        {
            Name = "Half",
            Kind = VideoOutputKind.Ndi,
            NdiWidth = 960,
            NdiHeight = 1080,
        };
        output.Mapping.Add(new MappingSection
        {
            SourceX = 0, SourceY = 0, SourceWidth = 0.5, SourceHeight = 1,
            TargetX = 0, TargetY = 0, TargetWidth = 1, TargetHeight = 1,
        });

        var spec = OutputMapping.Spec(output, 1920, 1080);

        Assert.NotNull(spec);
        // Destination fractions resolve against the FEED's raster, not the composition's.
        Assert.Equal((960, 1080), (spec.OutputWidth, spec.OutputHeight));
        var section = Assert.Single(spec.Sections);
        Assert.Equal(960, section.DestWidth);
    }

    [Fact]
    public void TheOptionsAndTheAudioLinkRoundTrip()
    {
        var project = new HaCueProject { Title = "NDI" };
        var line = new AudioLineDefinition { Name = "Feed", Kind = AudioLineKind.Ndi, Channels = 8 };
        var output = new VideoOutputDefinition
        {
            Name = "Feed",
            Kind = VideoOutputKind.Ndi,
            NdiWidth = 1280,
            NdiHeight = 720,
            NdiFrameRate = 59.94,
            NdiPixelFormat = NdiWireFormat.Uyvy,
            NdiCarriesAudio = true,
            NdiAudioChannels = 8,
            LinkedAudioLineId = line.Id,
        };
        line.LinkedVideoOutputId = output.Id;
        project.VideoOutputs.Add(output);
        project.AudioLines.Add(line);

        var restored = HaCueProjectFile.Deserialize(HaCueProjectFile.Serialize(project));
        var restoredOutput = Assert.Single(restored.VideoOutputs);
        var restoredLine = Assert.Single(restored.AudioLines);

        Assert.Equal((1280, 720, 59.94), (restoredOutput.NdiWidth, restoredOutput.NdiHeight, restoredOutput.NdiFrameRate));
        Assert.Equal(NdiWireFormat.Uyvy, restoredOutput.NdiPixelFormat);
        Assert.True(restoredOutput.NdiCarriesAudio);
        Assert.Equal(restoredLine.Id, restoredOutput.LinkedAudioLineId);
        Assert.Equal(restoredOutput.Id, restoredLine.LinkedVideoOutputId);
    }
}
