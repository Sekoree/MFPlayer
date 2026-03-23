using Microsoft.Extensions.Logging;
using PALib;
using PALib.Errors;
using PALib.Runtime;
using PALib.Types.Core;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddSimpleConsole(o =>
        {
            o.TimestampFormat = "HH:mm:ss ";
            o.SingleLine = true;
        })
        .SetMinimumLevel(LogLevel.Information);
});

PALibLogging.Configure(loggerFactory);
PortAudioLibraryResolver.Install(loggerFactory);

var init = Native.Pa_Initialize();
if (init != PaError.paNoError)
{
    Console.WriteLine($"Pa_Initialize failed: {(int)init} {PaErrorHelpers.Describe(init)}");
    Environment.Exit(1);
}

try
{
    Console.WriteLine($"Version int: {Native.Pa_GetVersion()}");
    var info = Native.Pa_GetVersionInfo();
    Console.WriteLine($"Version text: {info?.VersionText ?? "n/a"}");
    Console.WriteLine($"Host APIs: {Native.Pa_GetHostApiCount()}");
    Console.WriteLine($"Devices: {Native.Pa_GetDeviceCount()}");
}
finally
{
    var term = Native.Pa_Terminate();
    Console.WriteLine($"Pa_Terminate: {term} ({PaErrorHelpers.Describe(term)})");
}
