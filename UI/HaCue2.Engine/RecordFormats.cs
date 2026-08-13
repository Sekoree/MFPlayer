using HaCue2.Core.Model;
using S.Media.Encode.FFmpeg;

namespace HaCue2.Engine;

/// <summary>
/// What a recording's file extension means.
/// </summary>
/// <remarks>
/// <para>
/// Register item 30's patterns are whole filenames, so the extension the operator typed is the only
/// statement of format in the document - there is no container picker beside it to disagree with. This
/// is the one table that reads it, so the file written and the name shown always match.
/// </para>
/// <para>
/// <b>Which extensions are legal is not decided here</b> - that is a rule about what a project may
/// say, so it lives with the document in <see cref="RecordFormatNames"/> where project status can
/// reach it without an encoder. This table is the other half: what each legal extension resolves to.
/// The two cover the same set, and a test holds them to it.
/// </para>
/// </remarks>
public static class RecordFormats
{
    /// <param name="Extension">Including the dot, lowercase.</param>
    /// <param name="CarriesVideo">False for the audio-only containers, which refuse a video leg.</param>
    public sealed record RecordFormat(
        string Extension,
        EncodeContainer Container,
        EncodeVideoCodec Video,
        EncodeAudioCodec Audio,
        bool CarriesVideo)
    {
        /// <summary>How the pickers and the status pane describe it, from the document's own table.</summary>
        public string Summary => RecordFormatNames.Summaries.GetValueOrDefault(Extension, Extension);
    }

    /// <summary>Every format a pattern may name, in the order the UI offers them.</summary>
    public static IReadOnlyList<RecordFormat> All { get; } =
    [
        new(".mkv", EncodeContainer.Matroska, EncodeVideoCodec.H264, EncodeAudioCodec.Flac, true),
        new(".mka", EncodeContainer.Matroska, EncodeVideoCodec.H264, EncodeAudioCodec.Flac, false),
        new(".mp4", EncodeContainer.Mp4, EncodeVideoCodec.H264, EncodeAudioCodec.Aac, true),
        new(".m4a", EncodeContainer.Mp4, EncodeVideoCodec.H264, EncodeAudioCodec.Aac, false),
        new(".mov", EncodeContainer.Mov, EncodeVideoCodec.H264, EncodeAudioCodec.Aac, true),
        new(".ts", EncodeContainer.MpegTs, EncodeVideoCodec.H264, EncodeAudioCodec.Aac, true),
        new(".flv", EncodeContainer.Flv, EncodeVideoCodec.H264, EncodeAudioCodec.Aac, true),
    ];

    /// <summary>The format a filename names, or null when this build cannot write it.</summary>
    public static RecordFormat? Find(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? "");

        return All.FirstOrDefault(
            format => string.Equals(format.Extension, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds the encode settings for a format, sized to what is actually being written.</summary>
    /// <param name="channels">Audio channel count, or 0 for a picture-only recording.</param>
    /// <param name="sampleRate">The rate audio is submitted at - the bay's mix rate.</param>
    /// <param name="width">Picture width, or 0 for an audio-only recording.</param>
    public static EncodeSessionOptions Options(
        RecordFormat format, int channels, int sampleRate, int width = 0, int height = 0, int fps = 0)
    {
        ArgumentNullException.ThrowIfNull(format);

        var video = width > 0 && height > 0 && format.CarriesVideo;

        return new EncodeSessionOptions
        {
            Container = format.Container,
            OutputMode = (video, channels > 0) switch
            {
                (true, true) => EncodeOutputMode.VideoAndAudio,
                (true, false) => EncodeOutputMode.VideoOnly,
                _ => EncodeOutputMode.AudioOnly,
            },
            Video = new VideoEncodeOptions
            {
                Codec = format.Video,
                // A show recording is watched back, not delivered, so quality-per-size beats a bitrate
                // ceiling nobody chose. CRF 20 is visually clean at the sizes a projector composition
                // runs at, and "veryfast" leaves the CPU to the show - a recording that steals frames
                // from the thing being recorded has defeated itself.
                Crf = format.Video.SupportsCrf() ? 20 : null,
                Preset = format.Video.SupportsNamedPreset() ? "veryfast" : null,
                ScaleWidth = width,
                ScaleHeight = height,
                Fps = fps,
            },
            AudioLegs = channels > 0
                ?
                [
                    new AudioLegOptions
                    {
                        Codec = format.Audio,
                        Channels = channels,
                        // FLAC and the other lossless codecs ignore a bitrate; AAC without one falls to
                        // a codec default that is thin for anything wider than stereo.
                        BitrateBps = format.Audio is EncodeAudioCodec.Aac ? 96_000L * Math.Max(1, channels) : 0,
                        SampleRate = sampleRate,
                    },
                ]
                : [],
        };
    }
}
