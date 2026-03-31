using S.Media.Core.Errors;
using S.Media.Core.Video;
using S.Media.OpenGL.Output;

namespace S.Media.OpenGL;

/// <summary>
/// Abstract base for output adapters that wrap <see cref="OpenGLVideoOutput"/> and
/// <see cref="OpenGLVideoEngine"/>.
/// Handles common <see cref="Id"/>, <see cref="State"/>, <see cref="IsClone"/>,
/// <see cref="CloneParentOutputId"/>, <see cref="Start"/>, <see cref="Stop"/>,
/// <see cref="PushFrame(VideoFrame)"/>, and <see cref="Dispose"/> delegation so
/// that derived backends (SDL3, Avalonia) only implement their rendering-specific code.
/// </summary>
public abstract class OpenGLWrapperVideoOutput : IVideoOutput
{
    /// <summary>
    /// Initialises the wrapper and registers <paramref name="output"/> with
    /// <paramref name="engine"/>. If the output is already registered the call is
    /// treated as a no-op (idempotent).
    /// </summary>
    protected OpenGLWrapperVideoOutput(OpenGLVideoOutput output, OpenGLVideoEngine engine, bool isClone)
    {
        Output = output;
        Engine = engine;
        IsClone = isClone;

        var add = engine.AddOutput(output);
        if (add is not MediaResult.Success and not (int)MediaErrorCode.OpenGLCloneAlreadyAttached)
        {
            throw new InvalidOperationException(
                $"Failed to register output in OpenGL engine. Code={add}.");
        }
    }

    /// <summary>The underlying <see cref="OpenGLVideoOutput"/> managed by this wrapper.</summary>
    protected OpenGLVideoOutput Output { get; }

    /// <summary>The <see cref="OpenGLVideoEngine"/> this wrapper belongs to.</summary>
    protected OpenGLVideoEngine Engine { get; }

    /// <inheritdoc/>
    public Guid Id => Output.Id;

    /// <inheritdoc/>
    public VideoOutputState State => Output.State;

    /// <summary>
    /// <see langword="true"/> if this output was created or attached as a clone of another output.
    /// Set at construction time; updated by <c>AttachClone</c> on the parent.
    /// </summary>
    public bool IsClone { get; protected set; }

    /// <summary>
    /// The ID of the parent output this wrapper is cloned from, or
    /// <see langword="null"/> if it is not a clone.
    /// </summary>
    public Guid? CloneParentOutputId => Output.CloneParentOutputId;

    /// <inheritdoc/>
    public virtual int Start(VideoOutputConfig config) => Output.Start(config);

    /// <inheritdoc/>
    public virtual int Stop() => Output.Stop();

    /// <inheritdoc/>
    public virtual int PushFrame(VideoFrame frame) => Output.PushFrame(frame);

    /// <inheritdoc/>
    public virtual int PushFrame(VideoFrame frame, TimeSpan presentationTime)
        => Output.PushFrame(frame, presentationTime);

    /// <summary>
    /// Helper for derived <c>CreateClone</c> implementations: allocates a new
    /// <see cref="OpenGLVideoOutput"/>, attaches it to the engine with the supplied
    /// <paramref name="options"/>, and returns the underlying object.
    /// </summary>
    protected int CreateCloneCore(OpenGLCloneOptions options, out OpenGLVideoOutput? cloneOutput)
    {
        cloneOutput = null;
        var code = Engine.CreateCloneOutput(Id, options, out var glOutput);
        if (code != MediaResult.Success || glOutput is not OpenGLVideoOutput gl)
            return code != MediaResult.Success ? code : (int)MediaErrorCode.OpenGLCloneCreationFailed;
        cloneOutput = gl;
        return MediaResult.Success;
    }

    /// <summary>
    /// Called by <see cref="Dispose"/> immediately before common engine/output teardown.
    /// Override in derived classes to release backend-specific resources (render threads,
    /// GPU contexts, native windows, etc.).
    /// </summary>
    protected virtual void OnBeforeDispose() { }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        OnBeforeDispose();

        var wasClone = IsClone;
        _ = Engine.RemoveOutput(Id);
        if (!wasClone)
            Engine.Dispose();
        Output.Dispose();
    }
}

