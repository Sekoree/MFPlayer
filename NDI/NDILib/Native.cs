using System.Runtime.InteropServices;

namespace NDILib;

internal static partial class Native
{
    // Linux-first v1 binding. The runtime package exposes libndi.so.6.
    private const string LibraryName = "libndi.so.6";

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_initialize();

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_destroy();

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_version();

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_is_supported_CPU();

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_find_create_v2(in NdiFindCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_find_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_find_get_current_sources(nint p_instance, out uint p_no_sources);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_find_wait_for_sources(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_create_v3(in NdiRecvCreateV3 p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_connect(nint p_instance, in NdiSource p_src);

    [LibraryImport(LibraryName)]
    internal static partial NdiFrameType NDIlib_recv_capture_v3(
        nint p_instance,
        out NdiVideoFrameV2 p_video_data,
        out NdiAudioFrameV3 p_audio_data,
        out NdiMetadataFrame p_metadata,
        uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_video_v2(nint p_instance, in NdiVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_audio_v3(nint p_instance, in NdiAudioFrameV3 p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_recv_get_no_connections(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_create(in NdiSendCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_video_v2(nint p_instance, in NdiVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_audio_v3(nint p_instance, in NdiAudioFrameV3 p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_send_get_no_connections(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_framesync_create(nint p_receiver);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_capture_video(
        nint p_instance,
        out NdiVideoFrameV2 p_video_data,
        NdiFrameFormatType field_type);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_free_video(nint p_instance, in NdiVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_capture_audio_v2(
        nint p_instance,
        out NdiAudioFrameV3 p_audio_data,
        int sample_rate,
        int no_channels,
        int no_samples);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_free_audio_v2(nint p_instance, in NdiAudioFrameV3 p_audio_data);
}
