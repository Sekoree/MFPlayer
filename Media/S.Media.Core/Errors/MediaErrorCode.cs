namespace S.Media.Core.Errors;

public enum MediaErrorCode
{
    Success = 0,

    MediaConcurrentOperationViolation = 950,

    MixerDetachStepFailed = 3000,
    MixerSourceIdCollision = 3001,
    MixerClockTypeInvalid = 3002,

    VideoOutputBackpressureQueueFull = 4000,
    VideoOutputBackpressureTimeout = 4001,
    VideoFrameDisposed = 4002,

    MediaSourceNonSeekable = 4208,
    MediaSourceReadTimeout = 4209,
    MediaInvalidArgument = 4210,
    MediaExternalClockUnavailable = 4211,

    FFmpegOpenFailed = 2000,
    FFmpegStreamNotFound = 2001,
    FFmpegDecoderInitFailed = 2002,
    FFmpegReadFailed = 2003,
    FFmpegSeekFailed = 2004,
    FFmpegAudioDecodeFailed = 2005,
    FFmpegVideoDecodeFailed = 2006,
    FFmpegResamplerInitFailed = 2007,
    FFmpegResampleFailed = 2008,
    FFmpegPixelConversionFailed = 2009,
    FFmpegInvalidConfig = 2010,
    FFmpegInvalidAudioChannelMap = 2011,
    FFmpegSharedContextOpenFailed = 2012,
    FFmpegSharedContextDisposed = 2013,
    FFmpegConcurrentReadViolation = 2014,
    MIDIConcurrentOperationRejected = 918,
}

