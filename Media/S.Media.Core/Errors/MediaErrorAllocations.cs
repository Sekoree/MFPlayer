using System.Collections.ObjectModel;

namespace S.Media.Core.Errors;

public static class MediaErrorAllocations
{
    public static ErrorCodeAllocationRange GenericCommon { get; } = new(0, 999, nameof(GenericCommon));
    public static ErrorCodeAllocationRange Playback { get; } = new(1000, 1999, nameof(Playback));
    public static ErrorCodeAllocationRange Decoding { get; } = new(2000, 2999, nameof(Decoding));
    public static ErrorCodeAllocationRange Mixing { get; } = new(3000, 3999, nameof(Mixing));
    public static ErrorCodeAllocationRange OutputRender { get; } = new(4000, 4999, nameof(OutputRender));
    public static ErrorCodeAllocationRange NDI { get; } = new(5000, 5199, nameof(NDI));

    public static ErrorCodeAllocationRange FFmpegActive { get; } = new(2000, 2099, nameof(FFmpegActive));
    public static ErrorCodeAllocationRange FFmpegRuntimeReserve { get; } = new(2100, 2199, nameof(FFmpegRuntimeReserve));
    public static ErrorCodeAllocationRange FFmpegMappingReserve { get; } = new(2200, 2299, nameof(FFmpegMappingReserve));
    public static ErrorCodeAllocationRange MixingActive { get; } = new(3000, 3099, nameof(MixingActive));
    public static ErrorCodeAllocationRange OutputBackpressureActive { get; } = new(4000, 4099, nameof(OutputBackpressureActive));
    public static ErrorCodeAllocationRange PortAudioActive { get; } = new(4300, 4399, nameof(PortAudioActive));
    public static ErrorCodeAllocationRange OpenGLActive { get; } = new(4400, 4499, nameof(OpenGLActive));
    public static ErrorCodeAllocationRange NDIActiveNearTerm { get; } = new(5000, 5079, nameof(NDIActiveNearTerm));
    public static ErrorCodeAllocationRange NDIFutureReserve { get; } = new(5080, 5199, nameof(NDIFutureReserve));
    public static ErrorCodeAllocationRange MIDIReserve { get; } = new(900, 949, nameof(MIDIReserve));

    public static int MediaConcurrentOperationViolation => (int)MediaErrorCode.MediaConcurrentOperationViolation;
    public static int MixerDetachStepFailed => (int)MediaErrorCode.MixerDetachStepFailed;
    public static int MixerSourceIdCollision => (int)MediaErrorCode.MixerSourceIdCollision;
    public static int MixerClockTypeInvalid => (int)MediaErrorCode.MixerClockTypeInvalid;
    public static int FFmpegInvalidConfig => (int)MediaErrorCode.FFmpegInvalidConfig;
    public static int FFmpegInvalidAudioChannelMap => (int)MediaErrorCode.FFmpegInvalidAudioChannelMap;
    public static int VideoOutputBackpressureQueueFull => (int)MediaErrorCode.VideoOutputBackpressureQueueFull;
    public static int VideoOutputBackpressureTimeout => (int)MediaErrorCode.VideoOutputBackpressureTimeout;
    public static int VideoFrameDisposed => (int)MediaErrorCode.VideoFrameDisposed;
    public static int AudioRouteMapMissing => (int)MediaErrorCode.AudioRouteMapMissing;
    public static int AudioRouteMapInvalid => (int)MediaErrorCode.AudioRouteMapInvalid;
    public static int AudioChannelCountMismatch => (int)MediaErrorCode.AudioChannelCountMismatch;
    public static int OpenGLCloneParentNotFound => (int)MediaErrorCode.OpenGLCloneParentNotFound;
    public static int OpenGLCloneAlreadyAttached => (int)MediaErrorCode.OpenGLCloneAlreadyAttached;
    public static int OpenGLCloneNotAttached => (int)MediaErrorCode.OpenGLCloneNotAttached;
    public static int OpenGLCloneContextShareUnavailable => (int)MediaErrorCode.OpenGLCloneContextShareUnavailable;
    public static int OpenGLCloneCreationFailed => (int)MediaErrorCode.OpenGLCloneCreationFailed;
    public static int OpenGLCloneAttachFailed => (int)MediaErrorCode.OpenGLCloneAttachFailed;
    public static int OpenGLCloneDetachFailed => (int)MediaErrorCode.OpenGLCloneDetachFailed;
    public static int OpenGLCloneParentDisposed => (int)MediaErrorCode.OpenGLCloneParentDisposed;
    public static int OpenGLCloneChildDestroyed => (int)MediaErrorCode.OpenGLCloneChildDestroyed;
    public static int OpenGLCloneCycleDetected => (int)MediaErrorCode.OpenGLCloneCycleDetected;
    public static int OpenGLCloneChildAlreadyAttached => (int)MediaErrorCode.OpenGLCloneChildAlreadyAttached;
    public static int OpenGLCloneSelfAttachRejected => (int)MediaErrorCode.OpenGLCloneSelfAttachRejected;
    public static int OpenGLCloneMaxDepthExceeded => (int)MediaErrorCode.OpenGLCloneMaxDepthExceeded;
    public static int OpenGLClonePixelFormatIncompatible => (int)MediaErrorCode.OpenGLClonePixelFormatIncompatible;
    public static int OpenGLCloneParentNotInitialized => (int)MediaErrorCode.OpenGLCloneParentNotInitialized;
    public static int SDL3EmbedNotInitialized => (int)MediaErrorCode.SDL3EmbedNotInitialized;
    public static int SDL3EmbedInvalidParentHandle => (int)MediaErrorCode.SDL3EmbedInvalidParentHandle;
    public static int SDL3EmbedParentLost => (int)MediaErrorCode.SDL3EmbedParentLost;
    public static int SDL3EmbedHandleUnavailable => (int)MediaErrorCode.SDL3EmbedHandleUnavailable;
    public static int SDL3EmbedDescriptorUnavailable => (int)MediaErrorCode.SDL3EmbedDescriptorUnavailable;
    public static int SDL3EmbedThreadAffinityViolation => (int)MediaErrorCode.SDL3EmbedThreadAffinityViolation;
    public static int SDL3EmbedUnsupportedDescriptor => (int)MediaErrorCode.SDL3EmbedUnsupportedDescriptor;
    public static int SDL3EmbedInitializeFailed => (int)MediaErrorCode.SDL3EmbedInitializeFailed;
    public static int SDL3EmbedTeardownFailed => (int)MediaErrorCode.SDL3EmbedTeardownFailed;
    public static int NDIInitializeFailed => (int)MediaErrorCode.NDIInitializeFailed;
    public static int NDITerminateFailed => (int)MediaErrorCode.NDITerminateFailed;
    public static int NDIReceiverCreateFailed => (int)MediaErrorCode.NDIReceiverCreateFailed;
    public static int NDISourceStartFailed => (int)MediaErrorCode.NDISourceStartFailed;
    public static int NDISourceStopFailed => (int)MediaErrorCode.NDISourceStopFailed;
    public static int NDIAudioReadRejected => (int)MediaErrorCode.NDIAudioReadRejected;
    public static int NDIVideoReadRejected => (int)MediaErrorCode.NDIVideoReadRejected;
    public static int NDIVideoFallbackUnavailable => (int)MediaErrorCode.NDIVideoFallbackUnavailable;
    public static int NDIVideoRepeatedTimestampPresented => (int)MediaErrorCode.NDIVideoRepeatedTimestampPresented;
    public static int NDIOutputPushVideoFailed => (int)MediaErrorCode.NDIOutputPushVideoFailed;
    public static int NDIOutputPushAudioFailed => (int)MediaErrorCode.NDIOutputPushAudioFailed;
    public static int NDIMaxChildrenPerParentExceeded => (int)MediaErrorCode.NDIMaxChildrenPerParentExceeded;
    public static int NDIDiagnosticsThreadStartFailed => (int)MediaErrorCode.NDIDiagnosticsThreadStartFailed;
    public static int NDIDiagnosticsSnapshotUnavailable => (int)MediaErrorCode.NDIDiagnosticsSnapshotUnavailable;
    public static int NDIOutputAudioStreamDisabled => (int)MediaErrorCode.NDIOutputAudioStreamDisabled;
    public static int NDIInvalidConfig => (int)MediaErrorCode.NDIInvalidConfig;
    public static int NDIInvalidSourceOptions => (int)MediaErrorCode.NDIInvalidSourceOptions;
    public static int NDIInvalidOutputOptions => (int)MediaErrorCode.NDIInvalidOutputOptions;
    public static int NDIInvalidDiagnosticsOptions => (int)MediaErrorCode.NDIInvalidDiagnosticsOptions;
    public static int NDIInvalidLimitsOptions => (int)MediaErrorCode.NDIInvalidLimitsOptions;
    public static int NDIInvalidQueueOverflowPolicyOverride => (int)MediaErrorCode.NDIInvalidQueueOverflowPolicyOverride;
    public static int NDIInvalidVideoFallbackOverride => (int)MediaErrorCode.NDIInvalidVideoFallbackOverride;
    public static int NDIInvalidDiagnosticsTickOverride => (int)MediaErrorCode.NDIInvalidDiagnosticsTickOverride;

    public static IReadOnlyList<ErrorCodeAllocationRange> All { get; } =
        new ReadOnlyCollection<ErrorCodeAllocationRange>(
        [
            GenericCommon,
            Playback,
            Decoding,
            Mixing,
            OutputRender,
            NDI,
            FFmpegActive,
            FFmpegRuntimeReserve,
            FFmpegMappingReserve,
            MixingActive,
            OutputBackpressureActive,
            PortAudioActive,
            OpenGLActive,
            NDIActiveNearTerm,
            NDIFutureReserve,
            MIDIReserve,
        ]);
}

