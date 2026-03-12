using FFmpeg.AutoGen;
using ManagedBass;

internal class Program
{
    // Pointers can't be captured by lambdas — store as static fields
    static nint s_swrCtx;
    static nint s_codecCtx;
    static nint s_fCtx;
    static nint s_packet;
    static nint s_frame;
    static int  s_streamIndex;
    static int  s_outSampleRate;
    static int  s_outChannels;
    static readonly List<float> s_overflow = new();
    static bool s_decoderEof;

    // AVERROR(EAGAIN) = -11 on Linux (EAGAIN = 11)
    const int AVERROR_EAGAIN = -11;

    public static unsafe void Main(string[] args)
    {
        Console.WriteLine("Hello, World!");

        var b = Bass.Init(Frequency: 48000);
        Console.WriteLine($"Bass.Init: {b}");

        var testFile = "/home/seko/Music/テレパシ/01_01_テレパシ.flac";

        ffmpeg.RootPath = "/lib/";
        DynamicallyLoadedBindings.Initialize();

        var v = ffmpeg.av_version_info();
        Console.WriteLine($"ffmpeg.av_version_info: {v}");

        var fContext = ffmpeg.avformat_alloc_context();
        var openInputResult = ffmpeg.avformat_open_input(&fContext, testFile, null, null);
        Console.WriteLine($"avformat_open_input: {openInputResult}");
        var findStreamInfoResult = ffmpeg.avformat_find_stream_info(fContext, null);
        Console.WriteLine($"avformat_find_stream_info: {findStreamInfoResult}");

        AVCodec* codec = null;
        var streamIndex = ffmpeg.av_find_best_stream(fContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
        Console.WriteLine($"av_find_best_stream: {streamIndex}");
        Console.WriteLine($"Codec: {codec->name->ToString()}");

        var stream = fContext->streams[streamIndex];
        var codecContext = ffmpeg.avcodec_alloc_context3(codec);
        var codecParamToContext = ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar);
        Console.WriteLine($"avcodec_parameters_to_context: {codecParamToContext}");
        var codecOpenResult = ffmpeg.avcodec_open2(codecContext, codec, null);
        Console.WriteLine($"avcodec_open2: {codecOpenResult}");

        var outSampleRate = 48000;
        var outChannels   = 2;
        var sampleFormat  = AVSampleFormat.AV_SAMPLE_FMT_FLT;

        var stereoLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_from_string(&stereoLayout, "stereo");

        SwrContext* swrContext = null;
        ffmpeg.swr_alloc_set_opts2(
            &swrContext,
            &stereoLayout,
            sampleFormat, outSampleRate,
            &codecContext->ch_layout,
            codecContext->sample_fmt,
            codecContext->sample_rate,
            0, null);
        ffmpeg.swr_init(swrContext);

        // Store state for the callback
        s_swrCtx        = (nint)swrContext;
        s_codecCtx      = (nint)codecContext;
        s_fCtx          = (nint)fContext;
        s_packet        = (nint)ffmpeg.av_packet_alloc();
        s_frame         = (nint)ffmpeg.av_frame_alloc();
        s_streamIndex   = streamIndex;
        s_outSampleRate = outSampleRate;
        s_outChannels   = outChannels;

        var ch = Bass.CreateStream(48000, 2, BassFlags.Float, StreamCallback);
        Console.WriteLine($"Bass.CreateStream: {ch}");

        var playResult = Bass.ChannelPlay(ch);
        Console.WriteLine($"Bass.ChannelPlay: {playResult}");

        while (Bass.ChannelIsActive(ch) == PlaybackState.Playing)
            Thread.Sleep(10);

        // Cleanup
        var pktPtr   = (AVPacket*)s_packet;
        var framePtr = (AVFrame*)s_frame;
        ffmpeg.swr_free(&swrContext);
        ffmpeg.av_packet_free(&pktPtr);
        ffmpeg.av_frame_free(&framePtr);
        ffmpeg.avcodec_free_context(&codecContext);
        ffmpeg.avformat_close_input(&fContext);

        Bass.StreamFree(ch);
        Bass.Free();
    }

    static unsafe int StreamCallback(int handle, nint buffer, int length, nint user)
    {
        var swrCtx   = (SwrContext*)s_swrCtx;
        var codecCtx = (AVCodecContext*)s_codecCtx;
        var fCtx     = (AVFormatContext*)s_fCtx;
        var pkt      = (AVPacket*)s_packet;
        var frm      = (AVFrame*)s_frame;
        var output   = (float*)buffer;
        var needed   = length / sizeof(float);
        var written  = 0;

        // 1. Drain leftover samples from the previous callback
        var fromOverflow = Math.Min(s_overflow.Count, needed);
        for (int i = 0; i < fromOverflow; i++)
            output[written++] = s_overflow[i];
        s_overflow.RemoveRange(0, fromOverflow);

        // 2. Decode until BASS's buffer is filled
        while (written < needed && !s_decoderEof)
        {
            int receiveResult = ffmpeg.avcodec_receive_frame(codecCtx, frm);

            if (receiveResult == AVERROR_EAGAIN)
            {
                // Decoder needs more packets
                if (ffmpeg.av_read_frame(fCtx, pkt) < 0)
                {
                    // EOF: flush the decoder
                    ffmpeg.avcodec_send_packet(codecCtx, null);
                    s_decoderEof = true;
                    continue;
                }

                if (pkt->stream_index == s_streamIndex)
                    ffmpeg.avcodec_send_packet(codecCtx, pkt);
                ffmpeg.av_packet_unref(pkt);
                continue;
            }

            if (receiveResult < 0) { s_decoderEof = true; break; }

            // 3. Resample the decoded frame
            var outSamples = (int)ffmpeg.av_rescale_rnd(
                ffmpeg.swr_get_delay(swrCtx, codecCtx->sample_rate) + frm->nb_samples,
                s_outSampleRate, codecCtx->sample_rate, AVRounding.AV_ROUND_UP);

            var outBuffer = (byte*)ffmpeg.av_malloc((ulong)(outSamples * s_outChannels * sizeof(float)));
            var converted = ffmpeg.swr_convert(swrCtx, &outBuffer, outSamples, frm->extended_data, frm->nb_samples);

            var convertedFloats = (float*)outBuffer;
            var totalFloats     = converted * s_outChannels;

            // 4. Write what fits into BASS's buffer
            var canWrite = Math.Min(totalFloats, needed - written);
            for (int i = 0; i < canWrite; i++)
                output[written++] = convertedFloats[i];

            // 5. Store any leftover for the next callback
            for (int i = canWrite; i < totalFloats; i++)
                s_overflow.Add(convertedFloats[i]);

            ffmpeg.av_free(outBuffer);
            ffmpeg.av_frame_unref(frm);
        }

        // Signal end of stream to BASS
        if (s_decoderEof && written == 0 && s_overflow.Count == 0)
            return (int)StreamProcedureType.End;

        return written * sizeof(float);
    }
}
