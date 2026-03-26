namespace S.Media.Core.Mixing;

internal sealed class AudioVideoMixerRuntimeOptions
{
    public int AudioReadFrames { get; set; } = 480;

    public int SourceChannelCount { get; set; } = 2;

    public int OutputSampleRate { get; set; } = 48_000;

    public int[] RouteMap { get; set; } = [0, 1];

    public int VideoQueueCapacity { get; set; } = 3;

    // Some outputs (e.g., windowed GL views) require presentation on the creating thread.
    public bool PresentOnCallerThread { get; set; }

    public TimeSpan PresenterMinSleep { get; set; } = TimeSpan.FromMilliseconds(1);

    public VideoPresenterSyncPolicyOptions SyncPolicyOptions { get; set; } = VideoPresenterSyncPolicyOptions.Default;

    public bool AutoDriftCorrection { get; set; } = true;

    public int DriftDeadbandMs { get; set; } = 12;

    public double DriftGain { get; set; } = 0.12;

    public int DriftMaxStepMs { get; set; } = 4;

    public int DriftMaxOffsetMs { get; set; } = 250;

    public int DriftHardResyncMs { get; set; } = 140;
}
