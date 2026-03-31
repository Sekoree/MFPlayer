using NDILib;
using S.Media.Core.Errors;
using S.Media.NDI.Config;
using S.Media.NDI.Diagnostics;
using S.Media.NDI.Input;
using S.Media.NDI.Media;
using S.Media.NDI.Output;

namespace S.Media.NDI.Runtime;

public sealed class NDIEngine : IDisposable
{
	private readonly Lock _gate = new();
	private readonly AutoResetEvent _diagnosticsStopSignal = new(false);
	private readonly List<NDIAudioSource> _audioSources = [];
	private readonly List<NDIVideoSource> _videoSources = [];
	private readonly List<NDIVideoOutput> _outputs = [];
	private readonly Dictionary<NDIReceiver, NDICaptureCoordinator> _captureCoordinators = new();
	private Thread? _diagnosticsThread;
	private bool _diagnosticsRunning;
	private bool _disposed;

	private NDIIntegrationOptions _integrationOptions = new();
	private NDILimitsOptions _limitsOptions = new();
	private NDIDiagnosticsOptions _diagnosticsOptions = new();

	public bool IsInitialized { get; private set; }

	public event EventHandler<NDIEngineDiagnostics>? DiagnosticsUpdated;

	/// <summary>
	/// Initializes the NDI engine with default options (Balanced limits, Default diagnostics).
	/// </summary>
	public int Initialize() => Initialize(new NDIIntegrationOptions(), NDILimitsOptions.Balanced, NDIDiagnosticsOptions.Default);

	public int Initialize(NDIIntegrationOptions integrationOptions, NDILimitsOptions limitsOptions, NDIDiagnosticsOptions diagnosticsOptions)
	{
		ArgumentNullException.ThrowIfNull(integrationOptions);
		ArgumentNullException.ThrowIfNull(limitsOptions);
		ArgumentNullException.ThrowIfNull(diagnosticsOptions);

		var diagnosticsThreadToJoin = default(Thread);

		lock (_gate)
		{
			if (_disposed)
			{
				return (int)MediaErrorCode.NDIInitializeFailed;
			}

			diagnosticsThreadToJoin = _diagnosticsThread;
			_diagnosticsRunning = false;
			_diagnosticsThread = null;
			_diagnosticsStopSignal.Set();

			_integrationOptions = integrationOptions;
			_limitsOptions = limitsOptions.Normalize();
			_diagnosticsOptions = diagnosticsOptions.Normalize();
			IsInitialized = true;
		}

		diagnosticsThreadToJoin?.Join(TimeSpan.FromSeconds(1));

		var startCode = TryStartDiagnosticsThread();
		if (startCode != MediaResult.Success)
		{
			lock (_gate)
			{
				IsInitialized = false;
			}
		}

		return startCode;
	}

	public int Terminate()
	{
		Thread? diagnosticsThread;

		lock (_gate)
		{
			if (_disposed)
			{
				return MediaResult.Success;
			}

			diagnosticsThread = _diagnosticsThread;
			_diagnosticsRunning = false;
			_diagnosticsThread = null;
			_diagnosticsStopSignal.Set();

			foreach (var source in _audioSources)
			{
				_ = source.Stop();
				source.Dispose();
			}

			foreach (var source in _videoSources)
			{
				_ = source.Stop();
				source.Dispose();
			}

			foreach (var output in _outputs)
			{
				_ = output.Stop();
				output.Dispose();
			}

			_audioSources.Clear();
			_videoSources.Clear();
			_outputs.Clear();
			_captureCoordinators.Clear();
			IsInitialized = false;
		}

		diagnosticsThread?.Join(TimeSpan.FromSeconds(1));
		return MediaResult.Success;
	}

	public int CreateAudioSource(NDIReceiver receiver, in NDISourceOptions sourceOptions, out NDIAudioSource? source)
	{
		source = null;

		lock (_gate)
		{
			if (_disposed || !IsInitialized)
			{
				return (int)MediaErrorCode.NDIInitializeFailed;
			}

			var normalized = sourceOptions.Normalize();
			var optionsValidation = normalized.Validate();
			if (optionsValidation != MediaResult.Success)
			{
				return optionsValidation;
			}

			var effective = normalized;

			var coordinator = GetOrCreateCaptureCoordinatorLocked(receiver);
			var item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
			var code = item.CreateAudioSource(effective, out source);
			if (code != MediaResult.Success || source is null)
			{
				return (int)MediaErrorCode.NDIReceiverCreateFailed;
			}

			_audioSources.Add(source);
			return MediaResult.Success;
		}
	}

	public int CreateVideoSource(NDIReceiver receiver, in NDISourceOptions sourceOptions, out NDIVideoSource? source)
	{
		source = null;

		lock (_gate)
		{
			if (_disposed || !IsInitialized)
			{
				return (int)MediaErrorCode.NDIInitializeFailed;
			}

			var normalized = sourceOptions.Normalize();
			var optionsValidation = normalized.Validate();
			if (optionsValidation != MediaResult.Success)
			{
				return optionsValidation;
			}

			var effective = normalized;

			var coordinator = GetOrCreateCaptureCoordinatorLocked(receiver);
			var item = new NDIMediaItem(receiver, _integrationOptions, coordinator);
			var code = item.CreateVideoSource(effective, out source);
			if (code != MediaResult.Success || source is null)
			{
				return (int)MediaErrorCode.NDIReceiverCreateFailed;
			}

			_videoSources.Add(source);
			return MediaResult.Success;
		}
	}

	public int CreateOutput(string outputName, in NDIOutputOptions outputOptions, out NDIVideoOutput? output)
	{
		output = null;

		lock (_gate)
		{
			if (_disposed || !IsInitialized)
			{
				return (int)MediaErrorCode.NDIInitializeFailed;
			}

                        var effective = outputOptions with
                        {
                                SendFormatOverride = outputOptions.SendFormatOverride ?? _integrationOptions.SendFormat,
                                RequireAudioPathOnStart = _integrationOptions.RequireAudioPathOnStart || outputOptions.RequireAudioPathOnStart,
                        };

			var validate = effective.Validate();
			if (validate != MediaResult.Success)
			{
				return validate;
			}

			output = new NDIVideoOutput(outputName, effective);
			_outputs.Add(output);
			return MediaResult.Success;
		}
	}

	public int GetDiagnosticsSnapshot(out NDIEngineDiagnostics snapshot)
	{
		lock (_gate)
		{
			if (_disposed || !IsInitialized)
			{
				snapshot = default;
				return (int)MediaErrorCode.NDIDiagnosticsSnapshotUnavailable;
			}

			snapshot = BuildDiagnosticsSnapshotLocked();

			return MediaResult.Success;
		}
	}

	public void Dispose()
	{
		Thread? diagnosticsThread;

		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			diagnosticsThread = _diagnosticsThread;
			_diagnosticsRunning = false;
			_diagnosticsThread = null;
			_diagnosticsStopSignal.Set();
		}

		diagnosticsThread?.Join(TimeSpan.FromSeconds(1));

		_ = Terminate();

		lock (_gate)
		{
			_disposed = true;
			DiagnosticsUpdated = null;
		}

		_diagnosticsStopSignal.Dispose();
	}

	private int TryStartDiagnosticsThread()
	{
		Thread? thread;

		lock (_gate)
		{
			if (_disposed || !IsInitialized)
			{
				return (int)MediaErrorCode.NDIDiagnosticsThreadStartFailed;
			}

			if (!_diagnosticsOptions.EnableDedicatedDiagnosticsThread || _diagnosticsOptions.PublishSnapshotsOnRequestOnly)
			{
				return MediaResult.Success;
			}

			if (_diagnosticsThread is { IsAlive: true })
			{
				return MediaResult.Success;
			}

			_diagnosticsRunning = true;
			thread = new Thread(DiagnosticsLoop)
			{
				IsBackground = true,
				Name = "S.Media.NDI.Diagnostics",
			};

			_diagnosticsThread = thread;
		}

		try
		{
			thread.Start();
			return MediaResult.Success;
		}
		catch
		{
			lock (_gate)
			{
				_diagnosticsRunning = false;
				_diagnosticsThread = null;
			}

			return (int)MediaErrorCode.NDIDiagnosticsThreadStartFailed;
		}
	}

	private void DiagnosticsLoop()
	{
		while (true)
		{
			TimeSpan tickInterval;
			EventHandler<NDIEngineDiagnostics>? handler;
			NDIEngineDiagnostics snapshot;

			lock (_gate)
			{
				if (!_diagnosticsRunning || _disposed || !IsInitialized)
				{
					return;
				}

				tickInterval = _diagnosticsOptions.DiagnosticsTickInterval;
				handler = DiagnosticsUpdated;
				snapshot = BuildDiagnosticsSnapshotLocked();
			}

			handler?.Invoke(this, snapshot);

			if (_diagnosticsStopSignal.WaitOne(tickInterval))
			{
				return;
			}
		}
	}

	private NDIEngineDiagnostics BuildDiagnosticsSnapshotLocked()
	{
		long audioCaptured = 0;
		long audioDropped = 0;
		double maxAudioReadMs = 0;
		foreach (var source in _audioSources)
		{
			var diagnostics = source.Diagnostics;
			audioCaptured += diagnostics.FramesCaptured;
			audioDropped += diagnostics.FramesDropped;
			maxAudioReadMs = Math.Max(maxAudioReadMs, diagnostics.LastReadMs);
		}

		long videoCaptured = 0;
		long videoDropped = 0;
		long repeatedFrames = 0;
		long fallbackFrames = 0;
		var queueDepth = 0;
		var jitterBufferFrames = 0;
		var incomingPixelFormat = "none";
		var outputPixelFormat = "none";
		var conversionPath = "none";
		double maxVideoReadMs = 0;
		long videoPushSuccesses = 0;
		long videoPushFailures = 0;
		long audioPushSuccesses = 0;
		long audioPushFailures = 0;
		double maxOutputPushMs = 0;
		foreach (var source in _videoSources)
		{
			var diagnostics = source.Diagnostics;
			videoCaptured += diagnostics.FramesCaptured;
			videoDropped += diagnostics.FramesDropped;
			repeatedFrames += diagnostics.RepeatedTimestampFramesPresented;
			fallbackFrames += diagnostics.FallbackFramesPresented;
			queueDepth = Math.Max(queueDepth, diagnostics.QueueDepth);
			jitterBufferFrames = Math.Max(jitterBufferFrames, diagnostics.JitterBufferFrames);
			if (!string.Equals(diagnostics.IncomingPixelFormat, "none", StringComparison.OrdinalIgnoreCase))
			{
				incomingPixelFormat = diagnostics.IncomingPixelFormat;
			}

			if (!string.Equals(diagnostics.OutputPixelFormat, "none", StringComparison.OrdinalIgnoreCase))
			{
				outputPixelFormat = diagnostics.OutputPixelFormat;
			}

			if (!string.Equals(diagnostics.ConversionPath, "none", StringComparison.OrdinalIgnoreCase))
			{
				conversionPath = diagnostics.ConversionPath;
			}
			maxVideoReadMs = Math.Max(maxVideoReadMs, diagnostics.LastReadMs);
		}

		foreach (var output in _outputs)
		{
			var diagnostics = output.Diagnostics;
			videoPushSuccesses += diagnostics.VideoPushSuccesses;
			videoPushFailures += diagnostics.VideoPushFailures;
			audioPushSuccesses += diagnostics.AudioPushSuccesses;
			audioPushFailures += diagnostics.AudioPushFailures;
			maxOutputPushMs = Math.Max(maxOutputPushMs, diagnostics.LastPushMs);
		}

		return new NDIEngineDiagnostics(
			Audio: new NDIAudioDiagnostics(FramesCaptured: audioCaptured, FramesDropped: audioDropped, LastReadMs: maxAudioReadMs),
			VideoSource: new NDIVideoSourceDebugInfo(
				FramesCaptured: videoCaptured,
				FramesDropped: videoDropped,
				RepeatedTimestampFramesPresented: repeatedFrames,
				FallbackFramesPresented: fallbackFrames,
				LastReadMs: maxVideoReadMs,
				JitterBufferFrames: jitterBufferFrames,
				QueueDepth: queueDepth,
				IncomingPixelFormat: incomingPixelFormat,
				OutputPixelFormat: outputPixelFormat,
				ConversionPath: conversionPath),
			VideoOutput: new NDIVideoOutputDebugInfo(
				VideoPushSuccesses: videoPushSuccesses,
				VideoPushFailures: videoPushFailures,
				AudioPushSuccesses: audioPushSuccesses,
				AudioPushFailures: audioPushFailures,
				LastPushMs: maxOutputPushMs),
			ClockDriftMs: _diagnosticsOptions.DiagnosticsTickInterval.TotalMilliseconds / _limitsOptions.MaxPendingVideoFrames,
			CapturedAtUtc: DateTimeOffset.UtcNow);
	}

	private NDICaptureCoordinator GetOrCreateCaptureCoordinatorLocked(NDIReceiver receiver)
	{
		if (_captureCoordinators.TryGetValue(receiver, out var existing))
		{
			return existing;
		}

		var created = new NDICaptureCoordinator(receiver);
		_captureCoordinators[receiver] = created;
		return created;
	}
}
