using S.Media.Core.Errors;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class ErrorCodeRangesTests
{
    [Fact]
    public void ResolveSharedSemantic_MapsModuleConcurrencyCodes_ToGenericSemantic()
    {
        // FFmpeg and MIDI concurrent-read codes are remapped to the generic semantic.
        Assert.Equal(
            (int)MediaErrorCode.MediaConcurrentOperationViolation,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.FFmpegConcurrentReadViolation));

        Assert.Equal(
            (int)MediaErrorCode.MediaConcurrentOperationViolation,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.MIDIConcurrentOperationRejected_V2));
    }

    [Fact]
    public void ResolveSharedSemantic_NDIReadRejected_PassesThroughUnchanged()
    {
        // §5.4: NDI*ReadRejected codes are NOT remapped — callers must inspect source.State
        // to distinguish MediaSourceNotRunning from a genuine concurrent-read attempt.
        Assert.Equal(
            (int)MediaErrorCode.NDIAudioReadRejected,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.NDIAudioReadRejected));

        Assert.Equal(
            (int)MediaErrorCode.NDIVideoReadRejected,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.NDIVideoReadRejected));
    }

    [Fact]
    public void ResolveSharedSemantic_LeavesNonMappedCodeUnchanged()
    {
        var code = (int)MediaErrorCode.MIDIOutputNotOpen_V2;

        Assert.Equal(code, ErrorCodeRanges.ResolveSharedSemantic(code));
    }
}
