namespace NDILib;

/// <summary>Error codes specific to NDILib creation and initialisation failures.</summary>
public enum NDIErrorCode
{
    /// <summary>NDI runtime initialisation failed — CPU may not support SSE4.2, or the NDI runtime is not installed.</summary>
    NDIRuntimeInitFailed = 1,

    /// <summary>Failed to create an <see cref="NDIFinder"/> instance.</summary>
    NDIFinderCreateFailed = 2,

    /// <summary>Failed to create an <see cref="NDIReceiver"/> instance.</summary>
    NDIReceiverCreateFailed = 3,

    /// <summary>Failed to create an <see cref="NDISender"/> instance.</summary>
    NDISenderCreateFailed = 4,

    /// <summary>Failed to create an <see cref="NDIFrameSync"/> instance.</summary>
    NDIFrameSyncCreateFailed = 5,

    /// <summary>Failed to create an <see cref="NDIRouter"/> instance.</summary>
    NDIRouterCreateFailed = 6,
}

