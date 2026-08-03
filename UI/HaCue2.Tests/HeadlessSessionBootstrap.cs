using Avalonia.Headless;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("HaCue2.Tests.HeadlessSessionFramework", "HaCue2.Tests")]

namespace HaCue2.Tests;

/// <summary>
/// Warms the headless session before any test runs. <b>Do not delete this file.</b>
/// </summary>
/// <remarks>
/// <para>
/// Copied wholesale from <c>HaPlay.Tests</c>, and the extraction plan says to copy it wholesale for a
/// reason: a test assembly that takes the csproj but not this file reproduces a whole-run hang whose
/// occurrence depends on TEST ORDER — so it passes until somebody adds or renames a test.
/// </para>
/// <para>
/// <b>The mechanism.</b> Avalonia binds <c>Dispatcher.UIThread</c> to the first thread that touches it,
/// process-wide. If a plain view-model test — one whose code calls <c>Dispatcher.UIThread.Post</c>, which
/// several HaCue2 view-models do — happens to run before any <see cref="HeadlessUnitTestSession"/>-based
/// test, UIThread binds to an xunit worker thread. The shared session's first dispatch then crashes its
/// own loop while initializing the isolated headless app ("The calling thread cannot access this object
/// because a different thread owns it", out of <c>DefaultRenderLoop.Add</c>). With the loop dead every
/// later dispatch waits forever, and the run hangs with no test code on any thread.
/// </para>
/// <para>
/// A custom test framework is the only place this can be fixed: it warms the session BEFORE any test, so
/// the headless app initializes ON the session thread and UIThread belongs to it from the start. A
/// <c>[ModuleInitializer]</c> cannot — the session thread has to call this module's
/// <c>BuildAvaloniaApp</c>, which the loader blocks until the initializer returns, which is a guaranteed
/// deadlock when the initializer waits on the session.
/// </para>
/// </remarks>
public sealed class HeadlessSessionFramework : XunitTestFramework
{
    public HeadlessSessionFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        // Machine-local state is redirected for the whole assembly, before anything can read it: these
        // tests construct view-models that load settings and scan for autosaves, and a suite that wrote
        // into the developer's real profile is one nobody could run twice.
        var sandbox = Path.Combine(
            Path.GetTempPath(), "hacue2-tests-" + Guid.NewGuid().ToString("N")[..8]);

        Environment.SetEnvironmentVariable(HaCue2.Machine.StoragePaths.RootVariable, sandbox);

        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSessionFramework).Assembly)
            .DispatchGuarded(static () => { }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
