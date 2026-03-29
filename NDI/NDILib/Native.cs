using System.Runtime.InteropServices;
using NDILib.Runtime;

namespace NDILib;

internal static partial class Native
{
    private const string LibraryName = NDILibraryNames.Default;

    // ------------------------------------------------------------------
    // Initialisation
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Find
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_find_create_v2(in NdiFindCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_find_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_find_get_current_sources(nint p_instance, out uint p_no_sources);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_find_wait_for_sources(nint p_instance, uint timeout_in_ms);

    // ------------------------------------------------------------------
    // Receive
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_create_v3(in NdiRecvCreateV3 p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_connect(nint p_instance, in NdiSource p_src);

    // Separate entry point used to pass NULL (disconnect)
    [LibraryImport(LibraryName, EntryPoint = "NDIlib_recv_connect")]
    internal static partial void NDIlib_recv_connect_null(nint p_instance, nint p_src);

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
    internal static partial void NDIlib_recv_free_string(nint p_instance, nint p_string);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_recv_get_no_connections(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_send_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_set_tally(nint p_instance, in NdiTally p_tally);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_get_performance(
        nint p_instance,
        out NdiRecvPerformance p_total,
        out NdiRecvPerformance p_dropped);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_get_queue(nint p_instance, out NdiRecvQueue p_total);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_clear_connection_metadata(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_add_connection_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_get_web_control(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_get_source_name(
        nint p_instance,
        out nint p_source_name,
        uint timeout_in_ms);

    // ------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_create(in NdiSendCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_video_v2(nint p_instance, in NdiVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_video_async_v2(nint p_instance, in NdiVideoFrameV2 p_video_data);

    // Used to flush a pending async frame — maps to the same entry point with a zeroed struct
    [LibraryImport(LibraryName, EntryPoint = "NDIlib_send_send_video_async_v2")]
    internal static partial void NDIlib_send_flush_async(nint p_instance, nint p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_audio_v3(nint p_instance, in NdiAudioFrameV3 p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial NdiFrameType NDIlib_send_capture(
        nint p_instance,
        out NdiMetadataFrame p_metadata,
        uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_free_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_send_get_no_connections(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_send_get_tally(nint p_instance, out NdiTally p_tally, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_set_failover(nint p_instance, in NdiSource p_failover_source);

    // Separate entry point used to clear failover (pass NULL)
    [LibraryImport(LibraryName, EntryPoint = "NDIlib_send_set_failover")]
    internal static partial void NDIlib_send_clear_failover(nint p_instance, nint p_failover_source);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_get_source_name(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_clear_connection_metadata(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_add_connection_metadata(nint p_instance, in NdiMetadataFrame p_metadata);

    // ------------------------------------------------------------------
    // FrameSync
    // ------------------------------------------------------------------

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

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_framesync_audio_queue_depth(nint p_instance);

    // ------------------------------------------------------------------
    // Routing
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_routing_create(in NdiRoutingCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_routing_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_routing_change(nint p_instance, in NdiSource p_source);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_routing_clear(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_routing_get_no_connections(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_routing_get_source_name(nint p_instance);

    // ------------------------------------------------------------------
    // Utility — interleaved audio send helpers
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_send_send_audio_interleaved_16s(
        nint p_instance, in NdiAudioInterleaved16s p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_send_send_audio_interleaved_32s(
        nint p_instance, in NdiAudioInterleaved32s p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_send_send_audio_interleaved_32f(
        nint p_instance, in NdiAudioInterleaved32f p_audio_data);

    // ------------------------------------------------------------------
    // Utility — audio format conversions (v3 / FLTP)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_to_interleaved_16s_v3(
        in NdiAudioFrameV3 p_src, ref NdiAudioInterleaved16s p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_from_interleaved_16s_v3(
        in NdiAudioInterleaved16s p_src, ref NdiAudioFrameV3 p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_to_interleaved_32s_v3(
        in NdiAudioFrameV3 p_src, ref NdiAudioInterleaved32s p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_from_interleaved_32s_v3(
        in NdiAudioInterleaved32s p_src, ref NdiAudioFrameV3 p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_to_interleaved_32f_v3(
        in NdiAudioFrameV3 p_src, ref NdiAudioInterleaved32f p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_from_interleaved_32f_v3(
        in NdiAudioInterleaved32f p_src, ref NdiAudioFrameV3 p_dst);

    // ------------------------------------------------------------------
    // Utility — video format conversions
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_V210_to_P216(
        in NdiVideoFrameV2 p_src_v210, ref NdiVideoFrameV2 p_dst_p216);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_P216_to_V210(
        in NdiVideoFrameV2 p_src_p216, ref NdiVideoFrameV2 p_dst_v210);
}
