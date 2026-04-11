using System.Runtime.InteropServices;

namespace NDILib;

// ------------------------------------------------------------------
// Constants
// ------------------------------------------------------------------

/// <summary>Named constants mirroring the NDI SDK's compile-time defines.</summary>
public static class NDIConstants
{
    /// <summary>
    /// Pass as a frame's <see cref="NDIVideoFrameV2.Timecode"/>,
    /// <see cref="NDIAudioFrameV3.Timecode"/>, or <see cref="NDIMetadataFrame.Timecode"/>
    /// to have the NDI runtime synthesize the timecode automatically.
    /// Corresponds to <c>NDIlib_send_timecode_synthesize</c> (INT64_MAX).
    /// </summary>
    public const long TimecodeSynthesize = long.MaxValue;

    /// <summary>
    /// A <see cref="NDIVideoFrameV2.Timestamp"/> / <see cref="NDIAudioFrameV3.Timestamp"/>
    /// value that indicates the sender did not supply a timestamp (pre-SDK v2.5).
    /// Corresponds to <c>NDIlib_recv_timestamp_undefined</c> (INT64_MAX).
    /// </summary>
    public const long TimestampUndefined = long.MaxValue;
}

// ------------------------------------------------------------------
// Enumerations
// ------------------------------------------------------------------

public enum NDIFrameType : int
{
    None = 0,
    Video = 1,
    Audio = 2,
    Metadata = 3,
    Error = 4,
    StatusChange = 100,
    SourceChange = 101,
    Max = 0x7fffffff
}

public enum NDIRecvBandwidth : int
{
    MetadataOnly = -10,
    AudioOnly = 10,
    Lowest = 0,
    Highest = 100,
    Max = 0x7fffffff
}

public enum NDIRecvColorFormat : int
{
    BgrxBgra = 0,
    UyvyBgra = 1,
    RgbxRgba = 2,
    UyvyRgba = 3,
    Fastest = 100,
    Best = 101,
    Max = 0x7fffffff
}

public enum NDIFrameFormatType : int
{
    Interleaved = 0,
    Progressive = 1,
    Field0 = 2,
    Field1 = 3,
    Max = 0x7fffffff
}

public enum NDIFourCCVideoType : uint
{
    Uyvy = ((uint)'U') | ((uint)'Y' << 8) | ((uint)'V' << 16) | ((uint)'Y' << 24),
    Uyva = ((uint)'U') | ((uint)'Y' << 8) | ((uint)'V' << 16) | ((uint)'A' << 24),
    P216 = ((uint)'P') | ((uint)'2' << 8) | ((uint)'1' << 16) | ((uint)'6' << 24),
    Pa16 = ((uint)'P') | ((uint)'A' << 8) | ((uint)'1' << 16) | ((uint)'6' << 24),
    Yv12 = ((uint)'Y') | ((uint)'V' << 8) | ((uint)'1' << 16) | ((uint)'2' << 24),
    I420 = ((uint)'I') | ((uint)'4' << 8) | ((uint)'2' << 16) | ((uint)'0' << 24),
    Nv12 = ((uint)'N') | ((uint)'V' << 8) | ((uint)'1' << 16) | ((uint)'2' << 24),
    Bgra = ((uint)'B') | ((uint)'G' << 8) | ((uint)'R' << 16) | ((uint)'A' << 24),
    Bgrx = ((uint)'B') | ((uint)'G' << 8) | ((uint)'R' << 16) | ((uint)'X' << 24),
    Rgba = ((uint)'R') | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'A' << 24),
    Rgbx = ((uint)'R') | ((uint)'G' << 8) | ((uint)'B' << 16) | ((uint)'X' << 24)
}

public enum NDIFourCCAudioType : int
{
    /// <summary>Planar 32-bit floating point (native NDI format). Specify channel stride in bytes.</summary>
    Fltp = ((int)'F') | ((int)'L' << 8) | ((int)'T' << 16) | ((int)'p' << 24),
    Max = 0x7fffffff
}

// ------------------------------------------------------------------
// Core structs
// ------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
public struct NDIFindCreate
{
    public byte ShowLocalSources;
    public nint PGroups;
    public nint PExtraIps;

    public static NDIFindCreate CreateDefault() => new()
    {
        ShowLocalSources = 1,
        PGroups = nint.Zero,
        PExtraIps = nint.Zero
    };
}

[StructLayout(LayoutKind.Sequential)]
public struct NDISourceRef
{
    public nint PNDIName;
    public nint PUrlAddress;

    public readonly string? NDIName    => Marshal.PtrToStringUTF8(PNDIName);
    public readonly string? UrlAddress => Marshal.PtrToStringUTF8(PUrlAddress);
}

[StructLayout(LayoutKind.Sequential)]
public struct NDIRecvCreateV3
{
    public NDISourceRef SourceToConnectTo;
    public NDIRecvColorFormat ColorFormat;
    public NDIRecvBandwidth Bandwidth;
    public byte AllowVideoFields;
    public nint PNDIRecvName;
}

[StructLayout(LayoutKind.Sequential)]
public struct NDISendCreate
{
    public nint PNDIName;
    public nint PGroups;
    public byte ClockVideo;
    public byte ClockAudio;
}

[StructLayout(LayoutKind.Sequential)]
public struct NDIVideoFrameV2
{
    public int Xres;
    public int Yres;
    public NDIFourCCVideoType FourCC;
    public int FrameRateN;
    public int FrameRateD;
    public float PictureAspectRatio;
    public NDIFrameFormatType FrameFormatType;
    public long Timecode;
    public nint PData;

    /// <summary>
    /// For non-compressed formats: inter-line stride in bytes
    /// (0 defaults to <c>sizeof(pixel) × <see cref="Xres"/></c>).
    /// For compressed formats: total size of the <see cref="PData"/> buffer in bytes
    /// (mirrors the native SDK's <c>data_size_in_bytes</c> union member).
    /// </summary>
    public int LineStrideInBytes;

    public nint PMetadata;
    public long Timestamp;

    public readonly string? Metadata => Marshal.PtrToStringUTF8(PMetadata);
}

[StructLayout(LayoutKind.Sequential)]
public struct NDIAudioFrameV3
{
    public int SampleRate;
    public int NoChannels;
    public int NoSamples;
    public long Timecode;
    public NDIFourCCAudioType FourCC;
    public nint PData;

    /// <summary>
    /// For planar (FLTP) formats: stride in bytes for a single channel plane.
    /// For compressed formats: total size of the <see cref="PData"/> buffer in bytes
    /// (mirrors the native SDK's <c>data_size_in_bytes</c> union member).
    /// </summary>
    public int ChannelStrideInBytes;

    public nint PMetadata;
    public long Timestamp;

    public readonly string? Metadata => Marshal.PtrToStringUTF8(PMetadata);
}

[StructLayout(LayoutKind.Sequential)]
public struct NDIMetadataFrame
{
    /// <summary>
    /// Length of the UTF-8 XML string in bytes including the null terminator.
    /// 0 means the length is determined by the null terminator.
    /// </summary>
    public int Length;
    public long Timecode;
    public nint PData;

    public readonly string? Data => Marshal.PtrToStringUTF8(PData);
}

// ------------------------------------------------------------------
// Tally
// ------------------------------------------------------------------

/// <summary>
/// NDI tally state. Sent from receiver to sender to indicate on-program / on-preview status.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDITally
{
    /// <summary>Non-zero if this output is currently on program.</summary>
    public byte OnProgram;
    /// <summary>Non-zero if this output is currently on preview.</summary>
    public byte OnPreview;
}

// ------------------------------------------------------------------
// Receiver diagnostics
// ------------------------------------------------------------------

/// <summary>Frame counts returned by <c>NDIlib_recv_get_performance</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIRecvPerformance
{
    public long VideoFrames;
    public long AudioFrames;
    public long MetadataFrames;
}

/// <summary>Current queue depths returned by <c>NDIlib_recv_get_queue</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIRecvQueue
{
    public int VideoFrames;
    public int AudioFrames;
    public int MetadataFrames;
}

// ------------------------------------------------------------------
// Routing
// ------------------------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
public struct NDIRoutingCreate
{
    public nint PNDIName;
    public nint PGroups;
}

// ------------------------------------------------------------------
// Audio interleaved utility structs
// ------------------------------------------------------------------

/// <summary>Interleaved 16-bit signed integer audio frame, for use with the NDI utility conversion API.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIAudioInterleaved16s
{
    public int SampleRate;
    public int NoChannels;
    public int NoSamples;
    public long Timecode;
    /// <summary>
    /// Audio reference level in dB above professional reference (+4 dBU).
    /// Use 0 when sending, 20 when receiving for 20 dB of headroom.
    /// </summary>
    public int ReferenceLevel;
    public nint PData;
}

/// <summary>Interleaved 32-bit signed integer audio frame, for use with the NDI utility conversion API.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIAudioInterleaved32s
{
    public int SampleRate;
    public int NoChannels;
    public int NoSamples;
    public long Timecode;
    /// <inheritdoc cref="NDIAudioInterleaved16s.ReferenceLevel"/>
    public int ReferenceLevel;
    public nint PData;
}

/// <summary>Interleaved 32-bit floating-point audio frame, for use with the NDI utility conversion API.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIAudioInterleaved32f
{
    public int SampleRate;
    public int NoChannels;
    public int NoSamples;
    public long Timecode;
    public nint PData;
}

// ------------------------------------------------------------------
// Discovery
// ------------------------------------------------------------------

public readonly record struct NDIDiscoveredSource(string Name, string? UrlAddress);
