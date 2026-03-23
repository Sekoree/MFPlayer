using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace OSCLib;

public static class OSCPacketCodec
{
    private static readonly Encoding Ascii = Encoding.ASCII;

    public static bool TryDecode(
        ReadOnlySpan<byte> packetBytes,
        OSCDecodeOptions options,
        out OSCPacket? packet,
        out string? error)
    {
        try
        {
            var reader = new OSCSpanReader(packetBytes);
            packet = DecodePacket(ref reader, options);
            if (!reader.IsEnd)
                throw new FormatException("Packet contains trailing bytes.");

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            packet = null;
            error = ex.Message;
            return false;
        }
    }

    public static RentedBuffer EncodeToRented(OSCPacket packet)
    {
        var writer = new OSCBufferWriter(Math.Max(256, EstimatePacketSize(packet)));
        WritePacket(ref writer, packet);
        return writer.Detach();
    }

    private static OSCPacket DecodePacket(ref OSCSpanReader reader, OSCDecodeOptions options)
    {
        if (reader.Remaining < 4)
            throw new FormatException("Packet is too small.");

        return reader.PeekByte() == (byte)'#'
            ? OSCPacket.FromBundle(DecodeBundle(ref reader, options))
            : OSCPacket.FromMessage(DecodeMessage(ref reader, options));
    }

    private static OSCMessage DecodeMessage(ref OSCSpanReader reader, OSCDecodeOptions options)
    {
        var address = reader.ReadPaddedAsciiString();
        if (string.IsNullOrWhiteSpace(address) || address[0] != '/')
            throw new FormatException("Message address is invalid.");

        if (reader.IsEnd)
        {
            if (!options.AllowMissingTypeTagString)
                throw new FormatException("Missing OSC type tag string.");

            return new OSCMessage(address);
        }

        var typeTagStart = reader.PeekByte();
        if (typeTagStart != (byte)',')
        {
            if (!options.AllowMissingTypeTagString)
                throw new FormatException("Missing OSC type tag string.");

            return new OSCMessage(address);
        }

        var typeTagString = reader.ReadPaddedAsciiString();
        if (typeTagString.Length == 0 || typeTagString[0] != ',')
            throw new FormatException("Type tag string must start with ','.");

        var tags = typeTagString.AsSpan(1);
        var arguments = new List<OSCArgument>(tags.Length);
        var index = 0;

        while (index < tags.Length)
        {
            var tag = tags[index++];
            arguments.Add(ReadArgument(tag, ref reader, options, tags, ref index, 0));
        }

        return new OSCMessage(address, arguments);
    }

    private static OSCArgument ReadArgument(
        char tag,
        ref OSCSpanReader reader,
        OSCDecodeOptions options,
        ReadOnlySpan<char> allTags,
        ref int tagIndex,
        int arrayDepth)
    {
        return tag switch
        {
            'i' => OSCArgument.Int32(reader.ReadInt32BigEndian()),
            'f' => OSCArgument.Float32(reader.ReadFloat32BigEndian()),
            's' => OSCArgument.String(reader.ReadPaddedAsciiString()),
            'b' => OSCArgument.Blob(reader.ReadBlobPadded()),
            'h' => OSCArgument.Int64(reader.ReadInt64BigEndian()),
            't' => OSCArgument.TimeTag(new OSCTimeTag(reader.ReadUInt64BigEndian())),
            'd' => OSCArgument.Double64(reader.ReadDouble64BigEndian()),
            'S' => OSCArgument.Symbol(reader.ReadPaddedAsciiString()),
            'c' => OSCArgument.Char((char)reader.ReadInt32BigEndian()),
            'r' => OSCArgument.RgbaColor(reader.ReadUInt32BigEndian()),
            'm' => OSCArgument.Midi(OSCMidiMessage.FromUInt32(reader.ReadUInt32BigEndian())),
            'T' => OSCArgument.True(),
            'F' => OSCArgument.False(),
            'N' => OSCArgument.Nil(),
            'I' => OSCArgument.Impulse(),
            '[' => ReadArray(ref reader, options, allTags, ref tagIndex, arrayDepth + 1),
            ']' => throw new FormatException("Unexpected array terminator tag ']'."),
            _ => ReadUnknown(tag, ref reader, options, allTags, tagIndex)
        };
    }

    private static OSCArgument ReadArray(
        ref OSCSpanReader reader,
        OSCDecodeOptions options,
        ReadOnlySpan<char> allTags,
        ref int tagIndex,
        int arrayDepth)
    {
        if (arrayDepth > options.MaxArrayDepth)
            throw new FormatException($"Maximum OSC array depth {options.MaxArrayDepth} exceeded.");

        var items = new List<OSCArgument>();
        while (tagIndex < allTags.Length)
        {
            var tag = allTags[tagIndex++];
            if (tag == ']')
                return OSCArgument.Array(items);

            items.Add(ReadArgument(tag, ref reader, options, allTags, ref tagIndex, arrayDepth));
        }

        throw new FormatException("Unclosed OSC array type tags.");
    }

    private static OSCArgument ReadUnknown(
        char tag,
        ref OSCSpanReader reader,
        OSCDecodeOptions options,
        ReadOnlySpan<char> allTags,
        int tagIndex)
    {
        if (options.StrictMode)
            throw new FormatException($"Unknown OSC type tag '{tag}'.");

        var resolver = options.UnknownTagByteLengthResolver;
        if (resolver is null)
        {
            if (options.PreserveUnknownArguments)
                return OSCArgument.Unknown(tag, ReadOnlyMemory<byte>.Empty);

            throw new FormatException($"Unknown OSC type tag '{tag}' without length resolver.");
        }

        var bytesToRead = resolver(tag, allTags.ToString());
        if (bytesToRead < 0 || bytesToRead > reader.Remaining)
            throw new FormatException($"Unknown tag resolver returned invalid length for '{tag}'.");

        var raw = reader.ReadRaw(bytesToRead);
        return options.PreserveUnknownArguments
            ? OSCArgument.Unknown(tag, raw.ToArray())
            : OSCArgument.Nil();
    }

    private static OSCBundle DecodeBundle(ref OSCSpanReader reader, OSCDecodeOptions options)
    {
        var marker = reader.ReadPaddedAsciiString();
        if (!string.Equals(marker, "#bundle", StringComparison.Ordinal))
            throw new FormatException("Bundle marker '#bundle' is missing.");

        var timeTag = new OSCTimeTag(reader.ReadUInt64BigEndian());
        var elements = new List<OSCPacket>();

        while (!reader.IsEnd)
        {
            var elementSize = reader.ReadInt32BigEndian();
            if (elementSize < 0 || elementSize > reader.Remaining)
                throw new FormatException("Invalid OSC bundle element size.");

            var slice = reader.ReadRaw(elementSize);
            var nestedReader = new OSCSpanReader(slice);
            elements.Add(DecodePacket(ref nestedReader, options));
            if (!nestedReader.IsEnd)
                throw new FormatException("Bundle element has trailing bytes.");
        }

        return new OSCBundle(timeTag, elements);
    }

    private static void WritePacket(ref OSCBufferWriter writer, OSCPacket packet)
    {
        if (packet.Kind == OSCPacketKind.Message)
        {
            WriteMessage(ref writer, packet.Message!);
            return;
        }

        WriteBundle(ref writer, packet.Bundle!);
    }

    private static void WriteMessage(ref OSCBufferWriter writer, OSCMessage message)
    {
        writer.WritePaddedAsciiString(message.Address);

        var tags = BuildTypeTagString(message.Arguments);
        writer.WritePaddedAsciiString(tags);

        foreach (var argument in message.Arguments)
            WriteArgumentValue(ref writer, argument);
    }

    private static void WriteBundle(ref OSCBufferWriter writer, OSCBundle bundle)
    {
        writer.WritePaddedAsciiString("#bundle");
        writer.WriteUInt64BigEndian(bundle.TimeTag.Value);

        foreach (var element in bundle.Elements)
        {
            using var rented = EncodeToRented(element);
            writer.WriteInt32BigEndian(rented.Length);
            writer.WriteRaw(rented.Memory.Span);
        }
    }

    private static void WriteArgumentValue(ref OSCBufferWriter writer, OSCArgument argument)
    {
        switch (argument.Type)
        {
            case OSCArgumentType.Int32:
                writer.WriteInt32BigEndian(argument.AsInt32());
                break;
            case OSCArgumentType.Float32:
                writer.WriteFloat32BigEndian(argument.AsFloat32());
                break;
            case OSCArgumentType.String:
            case OSCArgumentType.Symbol:
                writer.WritePaddedAsciiString(argument.AsString());
                break;
            case OSCArgumentType.Blob:
                writer.WriteBlobPadded(argument.AsBlob().Span);
                break;
            case OSCArgumentType.Int64:
                writer.WriteInt64BigEndian(argument.AsInt64());
                break;
            case OSCArgumentType.TimeTag:
                writer.WriteUInt64BigEndian(argument.AsTimeTag().Value);
                break;
            case OSCArgumentType.Double64:
                writer.WriteDouble64BigEndian(argument.AsDouble64());
                break;
            case OSCArgumentType.Char:
                writer.WriteInt32BigEndian(argument.AsChar());
                break;
            case OSCArgumentType.RgbaColor:
                writer.WriteUInt32BigEndian(argument.AsRgbaColor());
                break;
            case OSCArgumentType.Midi:
                writer.WriteUInt32BigEndian(argument.AsMidi().ToUInt32());
                break;
            case OSCArgumentType.True:
            case OSCArgumentType.False:
            case OSCArgumentType.Nil:
            case OSCArgumentType.Impulse:
                break;
            case OSCArgumentType.Array:
                foreach (var nested in argument.AsArray())
                    WriteArgumentValue(ref writer, nested);
                break;
            case OSCArgumentType.Unknown:
                writer.WriteRaw(argument.AsUnknown().RawData.Span);
                break;
            default:
                throw new InvalidOperationException($"Unsupported argument type {argument.Type}.");
        }
    }

    private static string BuildTypeTagString(IReadOnlyList<OSCArgument> arguments)
    {
        var sb = new StringBuilder(arguments.Count + 2);
        sb.Append(',');
        foreach (var argument in arguments)
            AppendTypeTag(sb, argument);

        return sb.ToString();
    }

    private static void AppendTypeTag(StringBuilder sb, OSCArgument argument)
    {
        switch (argument.Type)
        {
            case OSCArgumentType.Int32:
                sb.Append('i');
                break;
            case OSCArgumentType.Float32:
                sb.Append('f');
                break;
            case OSCArgumentType.String:
                sb.Append('s');
                break;
            case OSCArgumentType.Blob:
                sb.Append('b');
                break;
            case OSCArgumentType.Int64:
                sb.Append('h');
                break;
            case OSCArgumentType.TimeTag:
                sb.Append('t');
                break;
            case OSCArgumentType.Double64:
                sb.Append('d');
                break;
            case OSCArgumentType.Symbol:
                sb.Append('S');
                break;
            case OSCArgumentType.Char:
                sb.Append('c');
                break;
            case OSCArgumentType.RgbaColor:
                sb.Append('r');
                break;
            case OSCArgumentType.Midi:
                sb.Append('m');
                break;
            case OSCArgumentType.True:
                sb.Append('T');
                break;
            case OSCArgumentType.False:
                sb.Append('F');
                break;
            case OSCArgumentType.Nil:
                sb.Append('N');
                break;
            case OSCArgumentType.Impulse:
                sb.Append('I');
                break;
            case OSCArgumentType.Array:
                sb.Append('[');
                foreach (var nested in argument.AsArray())
                    AppendTypeTag(sb, nested);
                sb.Append(']');
                break;
            case OSCArgumentType.Unknown:
                sb.Append(argument.AsUnknown().Tag);
                break;
            default:
                throw new InvalidOperationException($"Unsupported argument type {argument.Type}.");
        }
    }

    private static int EstimatePacketSize(OSCPacket packet)
    {
        if (packet.Kind == OSCPacketKind.Message)
        {
            var message = packet.Message!;
            var size = PaddedStringByteCount(message.Address) + PaddedStringByteCount(BuildTypeTagString(message.Arguments));
            foreach (var arg in message.Arguments)
                size += EstimateArgumentSize(arg);
            return size;
        }

        var bundleSize = 16; // #bundle + timetag
        foreach (var element in packet.Bundle!.Elements)
            bundleSize += 4 + EstimatePacketSize(element);
        return bundleSize;
    }

    private static int EstimateArgumentSize(OSCArgument argument)
    {
        return argument.Type switch
        {
            OSCArgumentType.Int32 or OSCArgumentType.Float32 or OSCArgumentType.Char or OSCArgumentType.RgbaColor or OSCArgumentType.Midi => 4,
            OSCArgumentType.Int64 or OSCArgumentType.TimeTag or OSCArgumentType.Double64 => 8,
            OSCArgumentType.String or OSCArgumentType.Symbol => PaddedStringByteCount(argument.AsString()),
            OSCArgumentType.Blob => 4 + Pad4(argument.AsBlob().Length),
            OSCArgumentType.True or OSCArgumentType.False or OSCArgumentType.Nil or OSCArgumentType.Impulse => 0,
            OSCArgumentType.Array => argument.AsArray().Sum(EstimateArgumentSize),
            OSCArgumentType.Unknown => argument.AsUnknown().RawData.Length,
            _ => 0
        };
    }

    private static int PaddedStringByteCount(string value)
    {
        var byteCount = Ascii.GetByteCount(value) + 1;
        return Pad4(byteCount);
    }

    private static int Pad4(int value) => (value + 3) & ~3;

    private ref struct OSCSpanReader
    {
        private readonly ReadOnlySpan<byte> _span;
        private int _offset;

        public OSCSpanReader(ReadOnlySpan<byte> span)
        {
            _span = span;
            _offset = 0;
        }

        public int Remaining => _span.Length - _offset;

        public bool IsEnd => _offset == _span.Length;

        public byte PeekByte()
        {
            if (Remaining < 1)
                throw new FormatException("Unexpected end of OSC packet.");
            return _span[_offset];
        }

        public int ReadInt32BigEndian()
        {
            var data = ReadRaw(4);
            return BinaryPrimitives.ReadInt32BigEndian(data);
        }

        public uint ReadUInt32BigEndian()
        {
            var data = ReadRaw(4);
            return BinaryPrimitives.ReadUInt32BigEndian(data);
        }

        public long ReadInt64BigEndian()
        {
            var data = ReadRaw(8);
            return BinaryPrimitives.ReadInt64BigEndian(data);
        }

        public ulong ReadUInt64BigEndian()
        {
            var data = ReadRaw(8);
            return BinaryPrimitives.ReadUInt64BigEndian(data);
        }

        public float ReadFloat32BigEndian()
        {
            var raw = ReadInt32BigEndian();
            return BitConverter.Int32BitsToSingle(raw);
        }

        public double ReadDouble64BigEndian()
        {
            var raw = ReadInt64BigEndian();
            return BitConverter.Int64BitsToDouble(raw);
        }

        public string ReadPaddedAsciiString()
        {
            var start = _offset;
            while (_offset < _span.Length && _span[_offset] != 0)
                _offset++;

            if (_offset >= _span.Length)
                throw new FormatException("Unterminated OSC string.");

            var value = Ascii.GetString(_span[start.._offset]);
            _offset++; // NUL

            while ((_offset & 3) != 0)
            {
                if (_offset >= _span.Length)
                    throw new FormatException("Malformed OSC string padding.");
                _offset++;
            }

            return value;
        }

        public ReadOnlyMemory<byte> ReadBlobPadded()
        {
            var length = ReadInt32BigEndian();
            if (length < 0 || length > Remaining)
                throw new FormatException("Invalid OSC blob length.");

            var data = ReadRaw(length).ToArray();
            var padding = Pad4(length) - length;
            if (padding > 0)
                ReadRaw(padding);

            return data;
        }

        public ReadOnlySpan<byte> ReadRaw(int length)
        {
            if (length < 0 || length > Remaining)
                throw new FormatException("Unexpected end of OSC packet.");

            var slice = _span.Slice(_offset, length);
            _offset += length;
            return slice;
        }
    }

    private ref struct OSCBufferWriter
    {
        private byte[] _buffer;
        private int _offset;

        public OSCBufferWriter(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _offset = 0;
        }

        public void WriteInt32BigEndian(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_offset, 4), value);
            _offset += 4;
        }

        public void WriteUInt32BigEndian(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_offset, 4), value);
            _offset += 4;
        }

        public void WriteInt64BigEndian(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_offset, 8), value);
            _offset += 8;
        }

        public void WriteUInt64BigEndian(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_offset, 8), value);
            _offset += 8;
        }

        public void WriteFloat32BigEndian(float value)
            => WriteInt32BigEndian(BitConverter.SingleToInt32Bits(value));

        public void WriteDouble64BigEndian(double value)
            => WriteInt64BigEndian(BitConverter.DoubleToInt64Bits(value));

        public void WritePaddedAsciiString(string value)
        {
            var bytes = Ascii.GetByteCount(value);
            EnsureCapacity(Pad4(bytes + 1));
            Ascii.GetBytes(value, _buffer.AsSpan(_offset, bytes));
            _offset += bytes;
            _buffer[_offset++] = 0;

            while ((_offset & 3) != 0)
                _buffer[_offset++] = 0;
        }

        public void WriteBlobPadded(ReadOnlySpan<byte> value)
        {
            WriteInt32BigEndian(value.Length);
            WriteRaw(value);

            var padding = Pad4(value.Length) - value.Length;
            if (padding == 0)
                return;

            EnsureCapacity(padding);
            _buffer.AsSpan(_offset, padding).Clear();
            _offset += padding;
        }

        public void WriteRaw(ReadOnlySpan<byte> value)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(_buffer.AsSpan(_offset));
            _offset += value.Length;
        }

        public RentedBuffer Detach()
        {
            var detached = _buffer;
            var length = _offset;
            _buffer = Array.Empty<byte>();
            _offset = 0;
            return new RentedBuffer(detached, length);
        }

        private void EnsureCapacity(int additional)
        {
            var needed = _offset + additional;
            if (needed <= _buffer.Length)
                return;

            var newBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
            _buffer.AsSpan(0, _offset).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }
    }
}

