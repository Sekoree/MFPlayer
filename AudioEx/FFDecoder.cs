using FFmpeg.AutoGen;
using Ownaudio;
using Ownaudio.Decoders;

namespace AudioEx;

public unsafe class FFDecoder : IAudioDecoder
{
    private readonly int _outSampleReate;
    private readonly int _outChannels;
    public AudioStreamInfo StreamInfo { get; private set; }

    private nint formatCtx;
    private nint swrCtx;
    private nint codecCtx;
    private AVCodec* inCodec;
    private int streamIndex;

    private nint packet;
    private nint frame;

    public FFDecoder(string file, int outSampleReate = 44100, int outChannels = 2)
    {
        _outSampleReate = outSampleReate;
        _outChannels = outChannels;
        formatCtx = (nint)ffmpeg.avformat_alloc_context();
        var fCtx = (AVFormatContext*)formatCtx;
        var openInputResult = ffmpeg.avformat_open_input(&fCtx, file, null, null);
        if (openInputResult != 0)
            throw new Exception($"avformat_open_input: {openInputResult}");
        var findStreamInfoResult = ffmpeg.avformat_find_stream_info(fCtx, null);
        if (findStreamInfoResult != 0)
            throw new Exception($"avformat_find_stream_info: {findStreamInfoResult}");

        AVCodec* localInCodec = null;
        streamIndex = ffmpeg.av_find_best_stream(fCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &localInCodec, 0);
        if (streamIndex < 0) 
            throw new Exception($"av_find_best_stream: {streamIndex}");
        inCodec = localInCodec;

        var stream = fCtx->streams[streamIndex];
        var codecContext = ffmpeg.avcodec_alloc_context3(inCodec);
        var codecParamToContext = ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar);
        if (codecParamToContext != 0)
            throw new Exception($"avcodec_parameters_to_context: {codecParamToContext}");
        var codecOpenResult = ffmpeg.avcodec_open2(codecContext, inCodec, null);
        if (codecOpenResult != 0)
            throw new Exception($"avcodec_open2: {codecOpenResult}");
        
        codecCtx = (nint)codecContext;

        var outChannelLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_from_string(&outChannelLayout, _outChannels == 1 ? "mono" : "stereo");

        SwrContext* localSwrCtx = null;
        ffmpeg.swr_alloc_set_opts2(
            &localSwrCtx,
            &outChannelLayout,
            AVSampleFormat.AV_SAMPLE_FMT_FLT, _outSampleReate,
            &codecContext->ch_layout,
            codecContext->sample_fmt,
            codecContext->sample_rate,
            0, null);
        if (ffmpeg.swr_init(localSwrCtx) != 0 || localSwrCtx == null)
            throw new Exception($"swr_init failed");

        swrCtx = (nint)localSwrCtx;

        packet = (nint)ffmpeg.av_packet_alloc();
        frame = (nint)ffmpeg.av_frame_alloc();

        StreamInfo = new AudioStreamInfo(
            channels: stream->codecpar->ch_layout.nb_channels,
            sampleRate: stream->codecpar->sample_rate,
            duration: TimeSpan.FromSeconds(fCtx->duration / (double)ffmpeg.AV_TIME_BASE),
            bitDepth:  ffmpeg.av_get_bytes_per_sample((AVSampleFormat)stream->codecpar->format) * 8
        );
    }

    public AudioDecoderResult DecodeNextFrame()
    {
        throw new NotImplementedException();
    }

    public AudioDecoderResult ReadFrames(byte[] buffer)
    {
        throw new NotImplementedException();
    }

    public bool TrySeek(TimeSpan position, out string error)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        // TODO release managed resources here
    }
}