using System;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using S.Media.Core.Media;
using S.Media.Core.Media.Endpoints;
using S.Media.SDL3;
using SDL = global::SDL3.SDL;
using MediaPixelFormat = S.Media.Core.Media.PixelFormat;
using AvScreen = Avalonia.Platform.Screen;

namespace SPlayer.Core.Models;

/// <summary>
/// Hosts an <see cref="SDL3VideoEndpoint"/> in its own native SDL window. The
/// SDL endpoint owns its window and dedicated render thread, so this host's
/// only job is to (a) call <see cref="SDL3VideoEndpoint.Open"/> with sane
/// defaults, (b) translate the endpoint's <see cref="SDL3VideoEndpoint.WindowClosed"/>
/// callback into the <see cref="IVideoOutputHost.Closed"/> event, and (c)
/// dispose the endpoint after the close completes.
///
/// <para>
/// SDL window placement is best-effort — we call
/// <see cref="SDL.SetWindowPosition(System.IntPtr,int,int)"/> after creation
/// to land on the user-selected screen, then ask SDL to flip to fullscreen on
/// the same display. SDL doesn't expose a "create on monitor N" primitive,
/// but the post-create position + fullscreen flip is reliable enough for the
/// outputs panel use-case.
/// </para>
/// </summary>
public sealed class Sdl3VideoOutputHost : IVideoOutputHost, IDisposable
{
    // SPlayer.Core does not currently configure a global ILoggerFactory for
    // its own diagnostics — the SDL3 endpoint and the framework use their
    // own logging plumbing. Using NullLogger here keeps the host quiet
    // while still letting us swap to a real logger later (just hand a
    // factory to a new constructor overload).
    private static readonly ILogger Log = NullLogger<Sdl3VideoOutputHost>.Instance;

    private readonly SDL3VideoEndpoint _endpoint;
    private int _disposed;
    private int _closeNotified;

    public Sdl3VideoOutputHost(string title, AvScreen? targetScreen)
    {
        _endpoint = new SDL3VideoEndpoint();

        // Default size hint matches what the Avalonia host uses — the real
        // size is whatever the screen reports. The SDL endpoint internally
        // re-derives the GL viewport from the actual window pixel size.
        int hintWidth  = targetScreen?.Bounds.Width  ?? 1920;
        int hintHeight = targetScreen?.Bounds.Height ?? 1080;

        // §sdl3-output — subscribe BEFORE Open so a fast-failing GL context
        // creation that immediately calls RaiseWindowClosedAsync (see the
        // GLMakeCurrent failure branch in SDL3VideoEndpoint.RenderLoop) is
        // still observable. Open itself doesn't raise the event, but
        // StartAsync can fire it during the first render-loop tick if the
        // GL context can't be claimed.
        _endpoint.WindowClosed += OnWindowClosed;

        try
        {
            _endpoint.Open(
                title:  title,
                width:  hintWidth,
                height: hintHeight,
                format: VideoFormat.Create(hintWidth, hintHeight, MediaPixelFormat.Bgra32, 30));
        }
        catch
        {
            // Open failed — undo the subscription and dispose the endpoint
            // so the SDL ref-count is balanced and the caller doesn't leak
            // a half-initialised host.
            _endpoint.WindowClosed -= OnWindowClosed;
            try { _endpoint.Dispose(); } catch { }
            throw;
        }

        if (targetScreen is not null)
        {
            try { ApplyTargetScreen(targetScreen); }
            catch (Exception ex)
            {
                Log.LogDebug(ex, "Sdl3VideoOutputHost: target-screen placement failed (ignored)");
            }
        }
    }

    public IVideoEndpoint VideoEndpoint => _endpoint;

    public bool ShowHud
    {
        get => _endpoint.ShowHud;
        set => _endpoint.ShowHud = value;
    }

    public bool LimitRenderToInputFps
    {
        get => _endpoint.LimitRenderToInputFps;
        set => _endpoint.LimitRenderToInputFps = value;
    }

    public string BackendName => "SDL3";

    /// <summary>
    /// Closes the SDL window and tears down the endpoint. Idempotent and
    /// safe to call from any thread. The teardown is fire-and-forget on a
    /// thread-pool worker because <see cref="SDL3VideoEndpoint.Dispose"/>
    /// joins the render thread for up to 3 s and we don't want the UI
    /// thread to block on it.
    /// </summary>
    public void Close()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(static state => ((Sdl3VideoOutputHost)state!).DisposeInternal(), this);
    }

    public event EventHandler? Closed;

    public void Dispose() => Close();

    private void DisposeInternal()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _endpoint.WindowClosed -= OnWindowClosed; } catch { }
        try { _endpoint.Dispose(); }
        catch (Exception ex) { Log.LogWarning(ex, "Sdl3VideoOutputHost: endpoint Dispose threw"); }
        // Always raise Closed at the end so subscribers see exactly one
        // notification per host lifetime, regardless of whether the user
        // clicked the X button (WindowClosed → DisposeInternal) or the
        // app called Close() directly.
        RaiseClosedOnce();
    }

    private void OnWindowClosed()
    {
        // §sdl3-output — user clicked the SDL window's × (or driver killed
        // the context). Tear down the endpoint here so the SDL ref-count
        // and GL resources are released; without this, the model would
        // remove itself from the outputs collection and the endpoint
        // would leak until the process exits.
        if (Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(static state => ((Sdl3VideoOutputHost)state!).DisposeInternal(), this);
    }

    private void RaiseClosedOnce()
    {
        if (Interlocked.Exchange(ref _closeNotified, 1) != 0) return;
        // The Closed handlers in OutputViewModel mutate
        // ObservableCollection<VideoEndpointModel>, which Avalonia's
        // ItemsControl is bound to. Marshal to the UI thread so the
        // collection mutation never races a layout pass.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Closed?.Invoke(this, EventArgs.Empty));
        }
    }

    private void ApplyTargetScreen(AvScreen screen)
    {
        // The endpoint doesn't expose its SDL window handle. Best-effort:
        // SDL.GetWindows returns every open window in the process; the
        // host only opens one SDL window at a time, but other
        // Sdl3VideoOutputHost instances may exist concurrently. We pick
        // the *last* element on the assumption it's the most recent (=
        // ours, since we just called Open). A wrong guess just means the
        // user has to drag the window to the desired screen — no fatal
        // misuse.
        var arr = SDL.GetWindows(out int count);
        if (arr is null || count <= 0) return;
        nint window = arr[count - 1];
        if (window == nint.Zero) return;

        SDL.SetWindowPosition(window, screen.Bounds.X, screen.Bounds.Y);
        SDL.SetWindowSize(window, screen.Bounds.Width, screen.Bounds.Height);
        SDL.SetWindowFullscreen(window, true);
    }
}
