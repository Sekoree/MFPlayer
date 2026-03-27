using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib;

public static unsafe partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static readonly ILogger Logger = PALibLogging.GetLogger("PALib.Core");

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetVersion")]
    private static partial int Pa_GetVersion_Import();
    public static int Pa_GetVersion()
    {
        PALibLogging.TraceCall(Logger, nameof(Pa_GetVersion));
        return Pa_GetVersion_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetVersionInfo")]
    private static partial nint Pa_GetVersionInfo_Import();
    public static PaVersionInfo? Pa_GetVersionInfo()
    {
        PALibLogging.TraceCall(Logger, nameof(Pa_GetVersionInfo));
        var ptr = Pa_GetVersionInfo_Import();
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PaVersionInfo>(ptr);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_Initialize")]
    private static partial PaError Pa_Initialize_Import();
    public static PaError Pa_Initialize()
    {
        PALibLogging.TraceCall(Logger, nameof(Pa_Initialize));
        return Pa_Initialize_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_Terminate")]
    private static partial PaError Pa_Terminate_Import();
    public static PaError Pa_Terminate()
    {
        PALibLogging.TraceCall(Logger, nameof(Pa_Terminate));
        return Pa_Terminate_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetErrorText")]
    private static partial nint Pa_GetErrorText_Import(PaError errorCode);
    public static string? Pa_GetErrorText(PaError errorCode)
    {
        PALibLogging.TraceCall(Logger, nameof(Pa_GetErrorText), (nameof(errorCode), errorCode));
        return Marshal.PtrToStringUTF8(Pa_GetErrorText_Import(errorCode));
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetHostApiCount")]
    private static partial int Pa_GetHostApiCount_Import();
    public static int Pa_GetHostApiCount() => Pa_GetHostApiCount_Import();

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetDefaultHostApi")]
    private static partial int Pa_GetDefaultHostApi_Import();
    public static int Pa_GetDefaultHostApi() => Pa_GetDefaultHostApi_Import();

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetHostApiInfo")]
    private static partial nint Pa_GetHostApiInfo_Import(int hostApi);
    public static PaHostApiInfo? Pa_GetHostApiInfo(int hostApi)
    {
        PALibLogging.TraceCall(Logger, nameof(Pa_GetHostApiInfo), (nameof(hostApi), hostApi));
        var ptr = Pa_GetHostApiInfo_Import(hostApi);
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PaHostApiInfo>(ptr);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_HostApiTypeIdToHostApiIndex")]
    private static partial int Pa_HostApiTypeIdToHostApiIndex_Import(PaHostApiTypeId type);
    public static int Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId type)
        => Pa_HostApiTypeIdToHostApiIndex_Import(type);

    [LibraryImport(LibraryName, EntryPoint = "Pa_HostApiDeviceIndexToDeviceIndex")]
    private static partial int Pa_HostApiDeviceIndexToDeviceIndex_Import(int hostApi, int hostApiDeviceIndex);
    public static int Pa_HostApiDeviceIndexToDeviceIndex(int hostApi, int hostApiDeviceIndex)
        => Pa_HostApiDeviceIndexToDeviceIndex_Import(hostApi, hostApiDeviceIndex);

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetLastHostErrorInfo")]
    private static partial nint Pa_GetLastHostErrorInfo_Import();
    public static PaHostErrorInfo? Pa_GetLastHostErrorInfo()
    {
        var ptr = Pa_GetLastHostErrorInfo_Import();
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PaHostErrorInfo>(ptr);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetDeviceCount")]
    private static partial int Pa_GetDeviceCount_Import();
    public static int Pa_GetDeviceCount() => Pa_GetDeviceCount_Import();

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetDefaultInputDevice")]
    private static partial int Pa_GetDefaultInputDevice_Import();
    public static int Pa_GetDefaultInputDevice() => Pa_GetDefaultInputDevice_Import();

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetDefaultOutputDevice")]
    private static partial int Pa_GetDefaultOutputDevice_Import();
    public static int Pa_GetDefaultOutputDevice() => Pa_GetDefaultOutputDevice_Import();

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetDeviceInfo")]
    private static partial nint Pa_GetDeviceInfo_Import(int device);
    public static PaDeviceInfo? Pa_GetDeviceInfo(int device)
    {
        var ptr = Pa_GetDeviceInfo_Import(device);
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PaDeviceInfo>(ptr);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_IsFormatSupported")]
    private static partial PaError Pa_IsFormatSupported_Import(nint inputParameters, nint outputParameters, double sampleRate);

    public static PaError Pa_IsFormatSupported(PaStreamParameters? inputParameters, PaStreamParameters? outputParameters, double sampleRate)
    {
        var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaStreamParameters>());
        var outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaStreamParameters>());
        try
        {
            nint pIn = nint.Zero;
            nint pOut = nint.Zero;
            if (inputParameters.HasValue)
            {
                Marshal.StructureToPtr(inputParameters.Value, inPtr, false);
                pIn = inPtr;
            }
            if (outputParameters.HasValue)
            {
                Marshal.StructureToPtr(outputParameters.Value, outPtr, false);
                pOut = outPtr;
            }
            return Pa_IsFormatSupported_Import(pIn, pOut, sampleRate);
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_OpenStream")]
    private static partial PaError Pa_OpenStream_Import(
        out nint stream,
        nint inputParameters,
        nint outputParameters,
        double sampleRate,
        nuint framesPerBuffer,
        PaStreamFlags streamFlags,
        delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int> streamCallback,
        nint userData);

    public static PaError Pa_OpenStream(
        out nint stream,
        PaStreamParameters? inputParameters,
        PaStreamParameters? outputParameters,
        double sampleRate,
        nuint framesPerBuffer,
        PaStreamFlags streamFlags,
        delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int> streamCallback,
        nint userData)
    {
        var inPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaStreamParameters>());
        var outPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PaStreamParameters>());
        try
        {
            nint pIn = nint.Zero;
            nint pOut = nint.Zero;
            if (inputParameters.HasValue)
            {
                Marshal.StructureToPtr(inputParameters.Value, inPtr, false);
                pIn = inPtr;
            }
            if (outputParameters.HasValue)
            {
                Marshal.StructureToPtr(outputParameters.Value, outPtr, false);
                pOut = outPtr;
            }
            return Pa_OpenStream_Import(out stream, pIn, pOut, sampleRate, framesPerBuffer, streamFlags, streamCallback, userData);
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_OpenDefaultStream")]
    private static partial PaError Pa_OpenDefaultStream_Import(
        out nint stream,
        int numInputChannels,
        int numOutputChannels,
        PaSampleFormat sampleFormat,
        double sampleRate,
        nuint framesPerBuffer,
        delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int> streamCallback,
        nint userData);

    public static PaError Pa_OpenDefaultStream(
        out nint stream,
        int numInputChannels,
        int numOutputChannels,
        PaSampleFormat sampleFormat,
        double sampleRate,
        nuint framesPerBuffer,
        delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int> streamCallback,
        nint userData)
        => Pa_OpenDefaultStream_Import(out stream, numInputChannels, numOutputChannels, sampleFormat, sampleRate, framesPerBuffer, streamCallback, userData);

    [LibraryImport(LibraryName, EntryPoint = "Pa_CloseStream")]
    private static partial PaError Pa_CloseStream_Import(nint stream);
    public static PaError Pa_CloseStream(nint stream) => Pa_CloseStream_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "Pa_SetStreamFinishedCallback")]
    private static partial PaError Pa_SetStreamFinishedCallback_Import(nint stream, delegate* unmanaged[Cdecl]<nint, void> streamFinishedCallback);
    public static PaError Pa_SetStreamFinishedCallback(nint stream, delegate* unmanaged[Cdecl]<nint, void> streamFinishedCallback)
        => Pa_SetStreamFinishedCallback_Import(stream, streamFinishedCallback);

    [LibraryImport(LibraryName, EntryPoint = "Pa_StartStream")] private static partial PaError Pa_StartStream_Import(nint stream);
    public static PaError Pa_StartStream(nint stream) => Pa_StartStream_Import(stream);
    [LibraryImport(LibraryName, EntryPoint = "Pa_StopStream")] private static partial PaError Pa_StopStream_Import(nint stream);
    public static PaError Pa_StopStream(nint stream) => Pa_StopStream_Import(stream);
    [LibraryImport(LibraryName, EntryPoint = "Pa_AbortStream")] private static partial PaError Pa_AbortStream_Import(nint stream);
    public static PaError Pa_AbortStream(nint stream) => Pa_AbortStream_Import(stream);
    [LibraryImport(LibraryName, EntryPoint = "Pa_IsStreamStopped")] private static partial PaError Pa_IsStreamStopped_Import(nint stream);
    public static PaError Pa_IsStreamStopped(nint stream) => Pa_IsStreamStopped_Import(stream);
    [LibraryImport(LibraryName, EntryPoint = "Pa_IsStreamActive")] private static partial PaError Pa_IsStreamActive_Import(nint stream);
    public static PaError Pa_IsStreamActive(nint stream) => Pa_IsStreamActive_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetStreamInfo")]
    private static partial nint Pa_GetStreamInfo_Import(nint stream);
    public static PaStreamInfo? Pa_GetStreamInfo(nint stream)
    {
        var ptr = Pa_GetStreamInfo_Import(stream);
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PaStreamInfo>(ptr);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetStreamTime")] private static partial double Pa_GetStreamTime_Import(nint stream);
    public static double Pa_GetStreamTime(nint stream) => Pa_GetStreamTime_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetStreamCpuLoad")] private static partial double Pa_GetStreamCpuLoad_Import(nint stream);
    public static double Pa_GetStreamCpuLoad(nint stream) => Pa_GetStreamCpuLoad_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "Pa_ReadStream")]
    private static partial PaError Pa_ReadStream_Import(nint stream, nint buffer, nuint frames);
    public static PaError Pa_ReadStream(nint stream, nint buffer, nuint frames)
    {
        var err = Pa_ReadStream_Import(stream, buffer, frames);
        if (err < 0 && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Pa_ReadStream failed: {Error} ({ErrorText})", err, Pa_GetErrorText(err));
        return err;
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_WriteStream")]
    private static partial PaError Pa_WriteStream_Import(nint stream, nint buffer, nuint frames);
    public static PaError Pa_WriteStream(nint stream, nint buffer, nuint frames)
    {
        var err = Pa_WriteStream_Import(stream, buffer, frames);
        if (err < 0 && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("Pa_WriteStream failed: {Error} ({ErrorText})", err, Pa_GetErrorText(err));
        return err;
    }

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetStreamReadAvailable")] private static partial nint Pa_GetStreamReadAvailable_Import(nint stream);
    public static nint Pa_GetStreamReadAvailable(nint stream) => Pa_GetStreamReadAvailable_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetStreamWriteAvailable")] private static partial nint Pa_GetStreamWriteAvailable_Import(nint stream);
    public static nint Pa_GetStreamWriteAvailable(nint stream) => Pa_GetStreamWriteAvailable_Import(stream);

    [LibraryImport(LibraryName, EntryPoint = "Pa_GetSampleSize")] private static partial PaError Pa_GetSampleSize_Import(PaSampleFormat format);
    public static PaError Pa_GetSampleSize(PaSampleFormat format) => Pa_GetSampleSize_Import(format);

    [LibraryImport(LibraryName, EntryPoint = "Pa_Sleep")] private static partial void Pa_Sleep_Import(nint msec);
    public static void Pa_Sleep(nint msec) => Pa_Sleep_Import(msec);
}
