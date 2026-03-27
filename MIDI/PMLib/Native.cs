using System.Runtime.InteropServices;
using PMLib.Types;

namespace PMLib;

/// <summary>
/// P/Invoke bindings for the PortMidi library, generated via <see cref="LibraryImportAttribute"/>.
/// The native library must be loadable as <c>portmidi</c>
/// (i.e. <c>libportmidi.so</c> on Linux, <c>portmidi.dll</c> on Windows,
/// <c>libportmidi.dylib</c> on macOS).
/// </summary>
public static partial class Native
{
    private const string LibraryName = "portmidi";

    // ── Initialisation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the PortMidi library and scans for available devices.
    /// Must be called before any other <c>Pm_*</c> function.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Initialize();

    /// <summary>Terminates the PortMidi library. Call when you are finished with PortMidi.</summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Terminate();

    // ── Error handling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns non-zero if <paramref name="stream"/> has a pending asynchronous host error.
    /// Retrieve the error text with <see cref="Pm_GetHostErrorText"/> or <see cref="GetHostErrorText"/>.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int Pm_HasHostError(nint stream);

    /// <summary>
    /// Returns a pointer to a static, library-owned string describing <paramref name="errnum"/>.
    /// For a managed <see cref="string"/> use <see cref="GetErrorText"/> instead.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial nint Pm_GetErrorText(PmError errnum);

    /// <summary>
    /// Writes a human-readable host-error description into <paramref name="msg"/> and clears
    /// the pending host error. The buffer should be at least 256 bytes (<c>PM_HOST_ERROR_MSG_LEN</c>).
    /// For a managed <see cref="string"/> use <see cref="GetHostErrorText"/> instead.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial void Pm_GetHostErrorText(Span<byte> msg, uint len);

    // ── Device enumeration ──────────────────────────────────────────────────────

    /// <summary>Returns the total number of MIDI devices. Valid device IDs range from 0 to Count − 1.</summary>
    [LibraryImport(LibraryName)]
    public static partial int Pm_CountDevices();

    /// <summary>Returns the default input device ID, or <c>-1</c> (<c>pmNoDevice</c>) if none exists.</summary>
    [LibraryImport(LibraryName)]
    public static partial int Pm_GetDefaultInputDeviceID();

    /// <summary>Returns the default output device ID, or <c>-1</c> (<c>pmNoDevice</c>) if none exists.</summary>
    [LibraryImport(LibraryName)]
    public static partial int Pm_GetDefaultOutputDeviceID();

    /// <summary>
    /// Finds the first device whose name contains <paramref name="pattern"/> and whose direction
    /// matches <paramref name="isInput"/>.
    /// The pattern may include a <c>", "</c> separator to also match against the interface name.
    /// </summary>
    /// <returns>The matching device ID, or <c>-1</c> (<c>pmNoDevice</c>) if not found.</returns>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Pm_FindDevice(string pattern, int isInput);

    /// <summary>
    /// Returns a native pointer to a <see cref="PmDeviceInfo"/> structure for the given device,
    /// or <see cref="nint.Zero"/> if <paramref name="id"/> is out of range or the device was deleted.
    /// The structure is owned by PortMidi and valid until <see cref="Pm_Terminate"/> is called.
    /// Use <see cref="GetDeviceInfo"/> for a safe managed copy.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial nint Pm_GetDeviceInfo(int id);

    // ── Opening and closing streams ─────────────────────────────────────────────

    /// <summary>
    /// Opens a MIDI input stream.
    /// </summary>
    /// <param name="stream">Receives the stream handle on success.</param>
    /// <param name="inputDevice">ID of the device to open.</param>
    /// <param name="inputSysDepInfo">
    /// Pointer to a <c>PmSysDepInfo</c> struct for platform-specific options,
    /// or <see cref="nint.Zero"/>.
    /// </param>
    /// <param name="bufferSize">Number of input events to buffer before overflow occurs.</param>
    /// <param name="timeProc">
    /// Pointer to a time procedure (see <see cref="PmTimeProcDelegate"/>),
    /// or <see cref="nint.Zero"/> to use the default PortTime millisecond clock.
    /// Obtain a function pointer with
    /// <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}(TDelegate)"/>.
    /// </param>
    /// <param name="timeInfo">User-data pointer passed to <paramref name="timeProc"/>.</param>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_OpenInput(
        out nint stream,
        int inputDevice,
        nint inputSysDepInfo,
        int bufferSize,
        nint timeProc,
        nint timeInfo);

    /// <summary>
    /// Opens a MIDI output stream.
    /// </summary>
    /// <param name="stream">Receives the stream handle on success.</param>
    /// <param name="outputDevice">ID of the device to open.</param>
    /// <param name="outputSysDepInfo">
    /// Pointer to a <c>PmSysDepInfo</c> struct for platform-specific options,
    /// or <see cref="nint.Zero"/>.
    /// </param>
    /// <param name="bufferSize">Number of output events to buffer.</param>
    /// <param name="timeProc">
    /// Pointer to a time procedure (see <see cref="PmTimeProcDelegate"/>),
    /// or <see cref="nint.Zero"/> to use the default PortTime millisecond clock.
    /// </param>
    /// <param name="timeInfo">User-data pointer passed to <paramref name="timeProc"/>.</param>
    /// <param name="latency">
    /// Output latency in milliseconds applied to message timestamps.
    /// <c>0</c> means timestamps are ignored and all output is delivered immediately.
    /// </param>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_OpenOutput(
        out nint stream,
        int outputDevice,
        nint outputSysDepInfo,
        int bufferSize,
        nint timeProc,
        nint timeInfo,
        int latency);

    // ── Virtual devices ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a named virtual input device that appears to other applications as an output device.
    /// Not supported on Windows (returns <see cref="PmError.NotImplemented"/>).
    /// </summary>
    /// <param name="name">The device name visible to other applications.</param>
    /// <param name="interf">
    /// Interface name, e.g. <c>"ALSA"</c> or <c>"CoreMIDI"</c>,
    /// or <see langword="null"/> to use the default.
    /// </param>
    /// <param name="sysDepInfo">
    /// Pointer to a <c>PmSysDepInfo</c> struct, or <see cref="nint.Zero"/>.
    /// </param>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial PmError Pm_CreateVirtualInput(string name, string? interf, nint sysDepInfo);

    /// <summary>
    /// Creates a named virtual output device that appears to other applications as an input device.
    /// Not supported on Windows (returns <see cref="PmError.NotImplemented"/>).
    /// </summary>
    /// <param name="name">The device name visible to other applications.</param>
    /// <param name="interf">
    /// Interface name, e.g. <c>"ALSA"</c> or <c>"CoreMIDI"</c>,
    /// or <see langword="null"/> to use the default.
    /// </param>
    /// <param name="sysDepInfo">
    /// Pointer to a <c>PmSysDepInfo</c> struct, or <see cref="nint.Zero"/>.
    /// </param>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial PmError Pm_CreateVirtualOutput(string name, string? interf, nint sysDepInfo);

    /// <summary>
    /// Removes a virtual device created by <see cref="Pm_CreateVirtualInput"/> or
    /// <see cref="Pm_CreateVirtualOutput"/>. The device must not be open.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_DeleteVirtualDevice(int device);

    // ── Stream configuration ────────────────────────────────────────────────────

    /// <summary>
    /// Sets message-type filters on an open input stream.
    /// Filtered message types are silently discarded.
    /// By default, only <see cref="PmFilter.Active"/> is filtered.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_SetFilter(nint stream, PmFilter filters);

    /// <summary>
    /// Sets a 16-bit channel mask on an input stream.
    /// Only messages on channels whose bit is set in <paramref name="mask"/> are received.
    /// Use <see cref="ChannelMask"/> to build a single-channel mask.
    /// All channels are enabled by default.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_SetChannelMask(nint stream, int mask);

    /// <summary>
    /// Immediately terminates outgoing messages on an output stream.
    /// Close the stream immediately after calling this.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Abort(nint stream);

    /// <summary>Closes a MIDI stream and flushes any pending output buffers where possible.</summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Close(nint stream);

    /// <summary>
    /// Re-synchronises the stream to the time procedure.
    /// Call this before sending the first non-zero-timestamp message after
    /// the time source starts advancing.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Synchronize(nint stream);

    // ── Reading ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads up to <paramref name="length"/> MIDI events from an input stream into
    /// <paramref name="buffer"/>.
    /// </summary>
    /// <returns>
    /// The number of events read (≥ 0), or a negative <see cref="PmError"/> value on error
    /// (e.g. <see cref="PmError.BufferOverflow"/>).
    /// </returns>
    [LibraryImport(LibraryName)]
    public static partial int Pm_Read(nint stream, Span<PmEvent> buffer, int length);

    /// <summary>
    /// Tests whether input data is available on <paramref name="stream"/>.
    /// </summary>
    /// <returns>
    /// <see cref="PmError.GotData"/> (1) if data is available,
    /// <see cref="PmError.NoData"/> (0) if not, or a negative error code.
    /// </returns>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Poll(nint stream);

    // ── Writing ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="length"/> MIDI events from <paramref name="buffer"/> to an output stream.
    /// Events must be in non-decreasing timestamp order.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Write(nint stream, ReadOnlySpan<PmEvent> buffer, int length);

    /// <summary>
    /// Writes a single timestamped short (non-SysEx) MIDI message.
    /// </summary>
    /// <param name="when">
    /// Timestamp in milliseconds. Ignored if the stream was opened with <c>latency = 0</c>.
    /// </param>
    /// <param name="msg">
    /// The packed MIDI message. Use <see cref="PmEvent.CreateMessage"/> to build one.
    /// </param>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_WriteShort(nint stream, int when, uint msg);

    /// <summary>
    /// Writes a timestamped SysEx message stored as a contiguous byte array.
    /// The message must be terminated with EOX (<c>0xF7</c>).
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_WriteSysEx(nint stream, int when, ReadOnlySpan<byte> msg);

    // ── Lock-free queue — pmutil ─────────────────────────────────────────────────

    /// <summary>
    /// Creates a lock-free, single-reader / single-writer queue suitable for use between
    /// a PortTime callback and the main thread.
    /// </summary>
    /// <param name="numMsgs">Maximum number of fixed-size messages the queue can hold.</param>
    /// <param name="bytesPerMsg">Fixed size in bytes of each message.</param>
    /// <returns>The queue handle, or <see cref="nint.Zero"/> on allocation failure.</returns>
    /// <remarks>
    /// <paramref name="numMsgs"/> maps to the C <c>long</c> type, which is 64-bit on Linux/macOS
    /// and 32-bit on Windows. On Windows, use values that fit in a 32-bit integer.
    /// </remarks>
    [LibraryImport(LibraryName)]
    public static partial nint Pm_QueueCreate(nint numMsgs, int bytesPerMsg);

    /// <summary>Destroys a queue and frees its memory.</summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_QueueDestroy(nint queue);

    /// <summary>
    /// Removes and copies the message at the head of <paramref name="queue"/> into the
    /// memory pointed to by <paramref name="msg"/>.
    /// </summary>
    /// <returns>
    /// <see cref="PmError.GotData"/> (1) on success, <see cref="PmError.NoData"/> (0) if empty,
    /// or <see cref="PmError.BufferOverflow"/> if a queue overflow was signalled.
    /// </returns>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Dequeue(nint queue, nint msg);

    /// <summary>
    /// Copies the message at <paramref name="msg"/> and appends it to <paramref name="queue"/>.
    /// </summary>
    /// <returns>
    /// <see cref="PmError.NoError"/> on success, or <see cref="PmError.BufferOverflow"/> if full.
    /// </returns>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_Enqueue(nint queue, nint msg);

    /// <summary>
    /// Returns non-zero if <paramref name="queue"/> is full,
    /// or <see cref="PmError.BadPtr"/> if <paramref name="queue"/> is <see cref="nint.Zero"/>.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial int Pm_QueueFull(nint queue);

    /// <summary>Returns non-zero if <paramref name="queue"/> is empty (or null).</summary>
    [LibraryImport(LibraryName)]
    public static partial int Pm_QueueEmpty(nint queue);

    /// <summary>
    /// Returns a pointer to the message at the head of <paramref name="queue"/> without
    /// removing it, or <see cref="nint.Zero"/> if the queue is empty.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial nint Pm_QueuePeek(nint queue);

    /// <summary>
    /// Signals an overflow condition to the reader so that the next
    /// <see cref="Pm_Dequeue"/> call returns <see cref="PmError.BufferOverflow"/>.
    /// </summary>
    [LibraryImport(LibraryName)]
    public static partial PmError Pm_SetOverflow(nint queue);
}
