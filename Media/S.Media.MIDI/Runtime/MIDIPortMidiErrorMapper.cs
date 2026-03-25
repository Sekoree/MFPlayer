using PMLib.Types;
using S.Media.Core.Errors;

namespace S.Media.MIDI.Runtime;

internal static class MIDIPortMidiErrorMapper
{
    public static int MapOpenInput(PmError error)
    {
        return error switch
        {
            PmError.NoError => MediaResult.Success,
            PmError.InvalidDeviceId => (int)MediaErrorCode.MIDIDeviceNotFound,
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected,
            _ => (int)MediaErrorCode.MIDIInputOpenFailed,
        };
    }

    public static int MapOpenOutput(PmError error)
    {
        return error switch
        {
            PmError.NoError => MediaResult.Success,
            PmError.InvalidDeviceId => (int)MediaErrorCode.MIDIDeviceNotFound,
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected,
            _ => (int)MediaErrorCode.MIDIOutputOpenFailed,
        };
    }

    public static int MapCloseInput(PmError error)
    {
        return error == PmError.NoError || error == PmError.BadPtr ? MediaResult.Success : (int)MediaErrorCode.MIDIInputCloseFailed;
    }

    public static int MapCloseOutput(PmError error)
    {
        return error == PmError.NoError || error == PmError.BadPtr ? MediaResult.Success : (int)MediaErrorCode.MIDIOutputCloseFailed;
    }

    public static int MapSend(PmError error)
    {
        return error switch
        {
            PmError.NoError => MediaResult.Success,
            PmError.BadData => (int)MediaErrorCode.MIDIInvalidMessage,
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected,
            _ => (int)MediaErrorCode.MIDIOutputSendFailed,
        };
    }

    public static int MapRead(PmError error)
    {
        return error switch
        {
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected,
            PmError.BufferOverflow => (int)MediaErrorCode.MIDIDeviceDisconnected,
            _ => (int)MediaErrorCode.MIDIInputNotOpen,
        };
    }
}

