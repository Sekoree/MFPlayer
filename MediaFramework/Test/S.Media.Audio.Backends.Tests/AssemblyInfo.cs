using Xunit;

// PortAudio and miniaudio both own process-global native backend state. Running their device
// enumeration/open/dispose tests concurrently lets one test tear down a host API while another is
// still probing it; on headless ALSA this has escaped managed code as a testhost crash. Keep the
// behavioural suite in one process, but serialize its native lifecycle operations.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
