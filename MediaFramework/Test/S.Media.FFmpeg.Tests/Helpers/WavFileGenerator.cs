namespace S.Media.FFmpeg.Tests.Helpers;

/// <summary>
/// Generates minimal valid WAV files in-memory for integration tests.
/// No external dependencies required.
/// </summary>
internal static class WavFileGenerator
{
    /// <summary>
    /// Writes a PCM WAV file containing a sine-wave tone to a temp file,
    /// returning the path. The caller is responsible for deleting the file.
    /// </summary>
    public static string CreateTempSineWav(
        int   sampleRate = 48000,
        int   channels   = 2,
        float frequency  = 440f,
        float durationSeconds = 0.5f)
    {
        int totalFrames  = (int)(sampleRate * durationSeconds);
        int totalSamples = totalFrames * channels;

        // Build 16-bit PCM sample data.
        var samples = new short[totalSamples];
        for (int frame = 0; frame < totalFrames; frame++)
        {
            double t     = (double)frame / sampleRate;
            short  value = (short)(short.MaxValue * 0.5 * Math.Sin(2 * Math.PI * frequency * t));
            for (int ch = 0; ch < channels; ch++)
                samples[frame * channels + ch] = value;
        }

        string path = Path.GetTempFileName() + ".wav";
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int dataBytes   = totalSamples * sizeof(short);
        int bytesPerSec = sampleRate * channels * sizeof(short);
        short blockAlign = (short)(channels * sizeof(short));
        short bitsPerSample = 16;

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);           // ChunkSize
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        // fmt  sub-chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                       // Subchunk1Size (PCM)
        bw.Write((short)1);                 // AudioFormat  (1 = PCM)
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(bytesPerSec);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        // data sub-chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        foreach (var s in samples)
            bw.Write(s);

        return path;
    }

    /// <summary>Creates a silent mono WAV of the given length.</summary>
    public static string CreateTempSilenceWav(
        int sampleRate = 44100,
        int channels   = 1,
        float durationSeconds = 0.25f)
        => CreateTempSineWav(sampleRate, channels, 0f, durationSeconds);
}

