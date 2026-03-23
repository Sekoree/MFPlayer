using PALib.Errors;
using PALib.Runtime;
using PALib.Types.Core;
using Xunit;

namespace PALib.Tests;

public class CoreInteropTests
{
    [Fact]
    public void InitializeAndEnumerate_ShouldSucceed()
    {
        PortAudioLibraryResolver.Install();

        var init = Native.Pa_Initialize();
        Assert.True(init == PaError.paNoError, $"Pa_Initialize failed: {(int)init} {PaErrorHelpers.Describe(init)}");

        try
        {
            Assert.True(Native.Pa_GetVersion() > 0);
            var versionInfo = Native.Pa_GetVersionInfo();
            Assert.NotNull(versionInfo);
            Assert.False(string.IsNullOrWhiteSpace(versionInfo?.VersionText));

            var hostApiCount = Native.Pa_GetHostApiCount();
            Assert.True(hostApiCount >= 0, $"Unexpected host api count: {hostApiCount}");

            var deviceCount = Native.Pa_GetDeviceCount();
            Assert.True(deviceCount >= 0, $"Unexpected device count: {deviceCount}");
        }
        finally
        {
            var term = Native.Pa_Terminate();
            Assert.True(term == PaError.paNoError, $"Pa_Terminate failed: {(int)term} {PaErrorHelpers.Describe(term)}");
        }
    }

    [Fact]
    public void UnsupportedPlatformExtensions_ReturnExpectedErrorCode()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var asioErr = PALib.ASIO.Native.PaAsio_ShowControlPanel(0, nint.Zero);
        Assert.Equal(PaError.paIncompatibleStreamHostApi, asioErr);

        var wasapiErr = PALib.WASAPI.Native.PaWasapi_UpdateDeviceList();
        Assert.Equal(PaError.paIncompatibleStreamHostApi, wasapiErr);

        var mmeCount = PALib.WMME.Native.PaWinMME_GetStreamInputHandleCount(nint.Zero);
        Assert.Equal((int)PaError.paIncompatibleStreamHostApi, mmeCount);

        var dsErr = PALib.DirectSound.Native.TraceStreamInfo(default);
        Assert.Equal(PaError.paIncompatibleStreamHostApi, dsErr);

        var wdmksErr = PALib.WDMKS.Native.TraceInfo(default);
        Assert.Equal(PaError.paIncompatibleStreamHostApi, wdmksErr);
    }
}
