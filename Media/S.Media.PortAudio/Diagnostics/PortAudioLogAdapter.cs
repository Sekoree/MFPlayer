using Microsoft.Extensions.Logging;

namespace S.Media.PortAudio.Diagnostics;

public sealed class PortAudioLogAdapter
{
    private readonly ILogger _logger;

    public PortAudioLogAdapter(ILogger logger)
    {
        _logger = logger;
    }

    public void LogInfo(string message)
    {
        _logger.LogInformation("{Message}", message);
    }

    public void LogWarning(string message)
    {
        _logger.LogWarning("{Message}", message);
    }

    public void LogError(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            _logger.LogError("{Message}", message);
            return;
        }

        _logger.LogError(exception, "{Message}", message);
    }
}

