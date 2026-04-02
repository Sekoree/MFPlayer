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
            PmError.InvalidDeviceId => (int)MediaErrorCode.MIDIDeviceNotFound_V2,
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected_V2,
            _ => (int)MediaErrorCode.MIDIInputOpenFailed_V2,
        };
    }

    public static int MapOpenOutput(PmError error)
    {
        return error switch
        {
            PmError.NoError => MediaResult.Success,
            PmError.InvalidDeviceId => (int)MediaErrorCode.MIDIDeviceNotFound_V2,
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected_V2,
            _ => (int)MediaErrorCode.MIDIOutputOpenFailed_V2,
        };
    }

    public static int MapCloseInput(PmError error)
    {
        return error == PmError.NoError || error == PmError.BadPtr ? MediaResult.Success : (int)MediaErrorCode.MIDIInputCloseFailed_V2;
    }

    public static int MapCloseOutput(PmError error)
    {
        return error == PmError.NoError || error == PmError.BadPtr ? MediaResult.Success : (int)MediaErrorCode.MIDIOutputCloseFailed_V2;
    }

    public static int MapSend(PmError error)
    {
        return error switch
        {
            PmError.NoError => MediaResult.Success,
            PmError.BadData => (int)MediaErrorCode.MIDIInvalidMessage_V2,
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected_V2,
            _ => (int)MediaErrorCode.MIDIOutputSendFailed_V2,
        };
    }

    public static int MapRead(PmError error)
    {
        return error switch
        {
            PmError.DeviceRemoved => (int)MediaErrorCode.MIDIDeviceDisconnected_V2,
            PmError.BufferOverflow => (int)MediaErrorCode.MIDIDeviceDisconnected_V2,
            _ => (int)MediaErrorCode.MIDIInputNotOpen_V2,
        };
    }
}
