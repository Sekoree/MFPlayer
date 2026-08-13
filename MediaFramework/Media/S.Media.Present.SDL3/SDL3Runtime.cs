using S.Media.Core.Diagnostics;

namespace S.Media.Present.SDL3;

/// <summary>
/// Reference-counted lifetime for the SDL3 video subsystem. Each
/// <see cref="SDL3VideoOutput"/> calls <see cref="Acquire"/> when its render
/// thread starts and <see cref="Release"/> on dispose; <c>SDL_QuitSubSystem</c>
/// only runs when the last holder lets go.
/// </summary>
/// <remarks>
/// Threading: SDL's docs recommend initializing video on the main thread on
/// macOS (window/event handling there is pinned to it). On Linux/Windows the
/// init thread is flexible - the SDL3VideoOutput runs all of its calls on its
/// own dedicated render thread, which is fine outside of macOS. macOS support
/// will require an external pump-on-main-thread harness; not implemented yet.
/// </remarks>
public static class SDL3Runtime
{
    private static readonly Lock Gate = new();
    private static int _refCount;
    private static int _autoThreadOutputCount;

    /// <summary>
    /// Serializes every mutation of SDL's process-global video state: the GL attribute block, window
    /// creation and destruction, GL context creation, and subsystem init/quit. Hold it around a whole
    /// "set attributes → create window → create context → make current" sequence, and around the
    /// matching teardown. Do NOT hold it while rendering, swapping, or joining a thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not defensive; it fixes a reproducible SIGSEGV.</b> Four call sites build SDL GL
    /// windows on four different threads - the compositor probe, the shared compositor context, the
    /// visualizer's offscreen context, and each video output's own render thread. Two of them
    /// overlapping crashes inside <c>SDL_CreateWindow</c> (SEGV_MAPERR), or fails with
    /// "EGL context already created" when the same global EGL state is read mid-mutation.
    /// </para>
    /// <para>
    /// <c>SDL_GL_SetAttribute</c> makes the ordering requirement stricter than "one call at a time":
    /// the attributes live in ONE process-global block that the following <c>SDL_CreateWindow</c> and
    /// <c>SDL_GL_CreateContext</c> consume. Two threads interleaving there silently give each other's
    /// windows the wrong pixel format even when nothing crashes - which is why the scope has to span
    /// the whole sequence rather than each call.
    /// </para>
    /// <para>
    /// Reentrant, so a scope may nest <see cref="Acquire"/> / <see cref="Release"/> or a teardown
    /// helper that takes it again.
    /// </para>
    /// </remarks>
    public static Lock.Scope EnterVideoDevice() => Gate.EnterScope();

    /// <summary>Initialise the SDL video subsystem (idempotent, ref-counted).</summary>
    public static void Acquire()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                // Ask the WM to activate the window when SDL_RaiseWindow is used (helps Linux/Wayland + smoke tools).
                SDL.SetHint(SDL.Hints.WindowActivateWhenRaised, "1");
                if (!SDL.Init(SDL.InitFlags.Video))
                    throw new InvalidOperationException(
                        $"SDL_Init(VIDEO) failed: {SDL.GetError()}");
            }
            _refCount++;
        }
    }

    /// <summary>
    /// Tracks an auto-thread SDL output that pumps events from a dedicated render thread.
    /// Logs when multiple outputs poll concurrently or when video is initialized off the main thread on macOS.
    /// </summary>
    public static void RegisterAutoThreadOutput(string ownerDescription)
    {
        lock (Gate)
        {
            _autoThreadOutputCount++;
            if (OperatingSystem.IsMacOS())
            {
                MediaDiagnostics.LogWarning(
                    "SDL3Runtime: {Owner} initializes SDL video on a background thread; macOS requires a main-thread harness.",
                    ownerDescription);
            }

            if (_autoThreadOutputCount > 1)
            {
                MediaDiagnostics.LogWarning(
                    "SDL3Runtime: {Count} auto-thread SDL outputs are active; concurrent event polling can be fragile.",
                    _autoThreadOutputCount);
            }
        }
    }

    /// <summary>Releases one auto-thread output registration from <see cref="RegisterAutoThreadOutput"/>.</summary>
    public static void UnregisterAutoThreadOutput()
    {
        lock (Gate)
        {
            if (_autoThreadOutputCount > 0)
                _autoThreadOutputCount--;
        }
    }

    /// <summary>Release one ref; tear down the video subsystem when the count hits zero.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (_refCount == 0) return;
            _refCount--;
            if (_refCount == 0)
                SDL.QuitSubSystem(SDL.InitFlags.Video);
        }
    }
}
