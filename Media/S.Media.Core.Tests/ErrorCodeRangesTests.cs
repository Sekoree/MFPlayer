using S.Media.Core.Errors;
using Xunit;

namespace S.Media.Core.Tests;

public sealed class ErrorCodeRangesTests
{
    [Fact]
    public void ResolveSharedSemantic_MapsModuleConcurrencyCodes_ToGenericSemantic()
    {
        Assert.Equal(
            (int)MediaErrorCode.MediaConcurrentOperationViolation,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.FFmpegConcurrentReadViolation));

        Assert.Equal(
            (int)MediaErrorCode.MediaConcurrentOperationViolation,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.MIDIConcurrentOperationRejected));

        Assert.Equal(
            (int)MediaErrorCode.MediaConcurrentOperationViolation,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.NDIAudioReadRejected));

        Assert.Equal(
            (int)MediaErrorCode.MediaConcurrentOperationViolation,
            ErrorCodeRanges.ResolveSharedSemantic((int)MediaErrorCode.NDIVideoReadRejected));
    }

    [Fact]
    public void ResolveSharedSemantic_LeavesNonMappedCodeUnchanged()
    {
        var code = (int)MediaErrorCode.MIDIOutputNotOpen;

        Assert.Equal(code, ErrorCodeRanges.ResolveSharedSemantic(code));
    }
}
