using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.SDL3;
using SDL3;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class SDL3AdapterTests
{
    [Fact]
    public void InitializeEmbedded_ValidatesParentHandle()
    {
        using var view = new SDL3VideoView();

        var code = view.InitializeEmbedded(nint.Zero, width: 10, height: 10);

        Assert.Equal((int)MediaErrorCode.SDL3EmbedInvalidParentHandle, code);
    }

    [Fact]
    public void HandleAccess_IsStateBound()
    {
        using var view = new SDL3VideoView();
        Assert.Equal((int)MediaErrorCode.SDL3EmbedHandleUnavailable, view.TryGetPlatformWindowHandle(out _));

        Assert.Equal(MediaResult.Success, view.Initialize(new SDL3VideoViewOptions()));
        Assert.Equal(MediaResult.Success, view.TryGetPlatformWindowHandle(out var handle));
        Assert.NotEqual(nint.Zero, handle);
    }

    [Fact]
    public void Initialize_RejectsUnsupportedDescriptorToken()
    {
        using var view = new SDL3VideoView();

        var code = view.Initialize(new SDL3VideoViewOptions { PreferredDescriptor = "unknown-descriptor" });

        Assert.Equal((int)MediaErrorCode.SDL3EmbedUnsupportedDescriptor, code);
    }

    [Fact]
    public void Initialize_NormalizesDescriptorTokenCase()
    {
        using var view = new SDL3VideoView();

        Assert.Equal(MediaResult.Success, view.Initialize(new SDL3VideoViewOptions { PreferredDescriptor = "X11-Window" }));
        Assert.Equal(MediaResult.Success, view.TryGetPlatformHandleDescriptor(out var descriptor));
        Assert.Equal("x11-window", descriptor);
    }

    [Fact]
    public void Initialize_AppliesConfiguredWindowFlags()
    {
        using var view = new SDL3VideoView();

        var init = view.Initialize(new SDL3VideoViewOptions
        {
            WindowFlags = SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden,
        });

        Assert.Equal(MediaResult.Success, init);
        Assert.Equal(MediaResult.Success, view.TryGetWindowFlags(out var flags));
        Assert.Equal(SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden, flags);
    }

    [Fact]
    public void ShowAndBringToFront_IsStateBound()
    {
        using var standaloneView = new SDL3VideoView();
        Assert.Equal((int)MediaErrorCode.SDL3EmbedNotInitialized, standaloneView.ShowAndBringToFront());
        Assert.Equal(MediaResult.Success, standaloneView.Initialize(new SDL3VideoViewOptions()));
        Assert.Equal(MediaResult.Success, standaloneView.ShowAndBringToFront());

        using var embeddedView = new SDL3VideoView();
        Assert.Equal(MediaResult.Success, embeddedView.InitializeEmbedded(new nint(123), width: 10, height: 10));
        embeddedView.SimulateEmbeddedParentLost();
        Assert.Equal((int)MediaErrorCode.SDL3EmbedParentLost, embeddedView.ShowAndBringToFront());
    }

    [Fact]
    public void PushAudio_FailsWhenAudioDisabled()
    {
        using var view = new SDL3VideoView();
        Assert.Equal(MediaResult.Success, view.Initialize(new SDL3VideoViewOptions()));
        Assert.Equal(MediaResult.Success, view.Start(new VideoOutputConfig()));

        var frame = new AudioFrame(new ReadOnlyMemory<float>(new float[4]), 2, 2, AudioFrameLayout.Interleaved, 48000, TimeSpan.Zero);
        var code = view.PushAudio(frame, TimeSpan.Zero);

        Assert.Equal((int)MediaErrorCode.MediaInvalidArgument, code);
    }

    [Fact]
    public void ParentLoss_ProducesDeterministicEmbedError()
    {
        using var view = new SDL3VideoView();
        Assert.Equal(MediaResult.Success, view.InitializeEmbedded(new nint(123), 10, 10));
        view.SimulateEmbeddedParentLost();

        using var frame = CreateFrame();
        var code = view.PushFrame(frame, TimeSpan.Zero);

        Assert.Equal((int)MediaErrorCode.SDL3EmbedParentLost, code);
        Assert.Equal((int)MediaErrorCode.SDL3EmbedParentLost, view.Start(new VideoOutputConfig()));
        Assert.Equal((int)MediaErrorCode.SDL3EmbedHandleUnavailable, view.TryGetPlatformWindowHandle(out _));
        Assert.Equal((int)MediaErrorCode.SDL3EmbedDescriptorUnavailable, view.TryGetPlatformHandleDescriptor(out _));
    }

    [Fact]
    public void HudRenderer_TracksDebugInfoSnapshot()
    {
        using var view = new SDL3VideoView
        {
            EnableHudOverlay = true,
        };

        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("render.fps", DebugValueKind.Scalar, 60.0, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("video.fps", DebugValueKind.Scalar, 59.9, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("pixel.format", DebugValueKind.Scalar, "nv12->rgba", DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("queue.depth", DebugValueKind.Scalar, 2, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("upload.ms", DebugValueKind.Scalar, 0.25, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("av.drift.ms", DebugValueKind.Scalar, 0.6, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("gpu.decode", DebugValueKind.Scalar, true, DateTimeOffset.UtcNow)));
        Assert.Equal(MediaResult.Success, view.UpdateHud(new DebugInfo("drop.frames", DebugValueKind.Scalar, 3, DateTimeOffset.UtcNow)));

        var text = view.HudRenderer.BuildHudTextSnapshot();
        Assert.Equal("RENDER:60.0 VIDEO:59.9 NV12/RGBA\nQ:2 UP:0.25 AV:0.6 GPU:1 DROP:3", text);
    }

    private static VideoFrame CreateFrame()
    {
        var rgba = new byte[16];
        return new VideoFrame(2, 2, VideoPixelFormat.Rgba32, new Rgba32PixelFormatData(), TimeSpan.Zero, true, rgba, 8);
    }
}
