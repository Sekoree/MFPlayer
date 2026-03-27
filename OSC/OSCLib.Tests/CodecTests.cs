using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public class CodecTests
{
    [Fact]
    public void Message_RoundTrip_Preserves_Required_And_Recommended_Types()
    {
        var message = new OSCMessage(
            "/demo/types",
            [
                OSCArgs.I32(42),
                OSCArgs.F32(3.5f),
                OSCArgs.Str("hello"),
                OSCArgs.Blob(new byte[] { 1, 2, 3 }),
                OSCArgs.I64(1234567890123L),
                OSCArgs.Time(new OSCTimeTag(10)),
                OSCArgs.F64(Math.PI),
                OSCArgs.Symbol("sym"),
                OSCArgs.Char('A'),
                OSCArgs.Color(0xFF00FF00),
                OSCArgs.MIDI(1, 0x90, 60, 100),
                OSCArgs.True(),
                OSCArgs.False(),
                OSCArgs.Nil(),
                OSCArgs.Impulse(),
                OSCArgs.Array(OSCArgs.I32(7), OSCArgs.Str("inner"))
            ]);

        var packet = OSCPacket.FromMessage(message);
        using var encoded = OSCPacketCodec.EncodeToRented(packet);

        var decodedOk = OSCPacketCodec.TryDecode(encoded.Memory.Span, new OSCDecodeOptions(), out var decoded, out var error);

        Assert.True(decodedOk, error);
        Assert.NotNull(decoded);
        Assert.Equal(OSCPacketKind.Message, decoded!.Kind);
        Assert.Equal(message.Address, decoded.Message!.Address);
        Assert.Equal(message.Arguments.Count, decoded.Message.Arguments.Count);
    }

    [Fact]
    public void UnknownTag_DefaultStrict_Rejects()
    {
        var payload = new byte[]
        {
            0x2F, 0x78, 0x00, 0x00, // "/x"
            0x2C, 0x7A, 0x00, 0x00, // ",z"
            0x00, 0x00, 0x00, 0x01
        };

        var ok = OSCPacketCodec.TryDecode(payload, new OSCDecodeOptions(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("Unknown OSC type tag", error);
    }

    [Fact]
    public void UnknownTag_NonStrict_CanPreserveRaw()
    {
        var payload = new byte[]
        {
            0x2F, 0x78, 0x00, 0x00, // "/x"
            0x2C, 0x7A, 0x00, 0x00, // ",z"
            0xDE, 0xAD, 0xBE, 0xEF
        };

        var options = new OSCDecodeOptions
        {
            StrictMode = false,
            PreserveUnknownArguments = true,
            UnknownTagByteLengthResolver = static (_, _) => 4
        };

        var ok = OSCPacketCodec.TryDecode(payload, options, out var packet, out var error);

        Assert.True(ok, error);
        var unknown = packet!.Message!.Arguments.Single().AsUnknown();
        Assert.Equal('z', unknown.Tag);
        Assert.Equal("DEADBEEF", Convert.ToHexString(unknown.RawData.Span));
    }
}
