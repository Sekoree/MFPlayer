using System.Runtime.InteropServices;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.CoreAudio;

internal static partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static bool IsSupportedPlatform => OperatingSystem.IsMacOS();

    [LibraryImport(LibraryName, EntryPoint = "PaMacCore_SetupStreamInfo")]
    private static partial void PaMacCore_SetupStreamInfo_Import(ref PaMacCoreStreamInfo data, nuint flags);

    public static void PaMacCore_SetupStreamInfo(ref PaMacCoreStreamInfo data, nuint flags)
    {
        if (!IsSupportedPlatform)
            return;

        PaMacCore_SetupStreamInfo_Import(ref data, flags);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaMacCore_SetupChannelMap")]
    private static partial void PaMacCore_SetupChannelMap_Import(ref PaMacCoreStreamInfo data, nint channelMap, nuint channelMapSize);

    public static void PaMacCore_SetupChannelMap(ref PaMacCoreStreamInfo data, nint channelMap, nuint channelMapSize)
    {
        if (!IsSupportedPlatform)
            return;

        PaMacCore_SetupChannelMap_Import(ref data, channelMap, channelMapSize);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaMacCore_GetStreamInputDevice")]
    private static partial uint PaMacCore_GetStreamInputDevice_Import(nint stream);

    public static uint PaMacCore_GetStreamInputDevice(nint stream)
        => !IsSupportedPlatform ? 0u : PaMacCore_GetStreamInputDevice_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "PaMacCore_GetStreamOutputDevice")]
    private static partial uint PaMacCore_GetStreamOutputDevice_Import(nint stream);

    public static uint PaMacCore_GetStreamOutputDevice(nint stream)
        => !IsSupportedPlatform ? 0u : PaMacCore_GetStreamOutputDevice_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "PaMacCore_GetChannelName")]
    private static partial nint PaMacCore_GetChannelName_Import(int device, int channelIndex, [MarshalAs(UnmanagedType.I1)] bool input);

    public static string? PaMacCore_GetChannelName(int device, int channelIndex, bool input)
        => !IsSupportedPlatform ? null : Marshal.PtrToStringUTF8(PaMacCore_GetChannelName_Import(device, channelIndex, input));

    [LibraryImport(LibraryName, EntryPoint = "PaMacCore_GetBufferSizeRange")]
    private static partial PaError PaMacCore_GetBufferSizeRange_Import(int device, out nint minBufferSizeFrames, out nint maxBufferSizeFrames);

    public static PaError PaMacCore_GetBufferSizeRange(int device, out nint minBufferSizeFrames, out nint maxBufferSizeFrames)
    {
        if (!IsSupportedPlatform)
        {
            minBufferSizeFrames = 0;
            maxBufferSizeFrames = 0;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaMacCore_GetBufferSizeRange_Import(device, out minBufferSizeFrames, out maxBufferSizeFrames);
    }
}
