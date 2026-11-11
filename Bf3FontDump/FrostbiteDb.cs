using System.Buffers.Binary;
using System.Text;

namespace Bf3FontDump;

internal sealed class DbNode(string name, int type, int offset, int valueOffset, object? value, List<DbNode>? children)
{
    public string Name { get; } = name;
    public int Type { get; } = type;
    public int Offset { get; } = offset;
    public int ValueOffset { get; } = valueOffset;
    public object? Value { get; } = value;
    public IReadOnlyList<DbNode> Children { get; } = children ?? [];
    public DbNode? Child(string name) => Children.FirstOrDefault(child => child.Name == name);
    public string? Text(string name) => Child(name)?.Value as string;
    public byte[]? Bytes(string name) => Child(name)?.Value as byte[];
    public ulong? Number(string name) => Child(name)?.Value switch
    {
        uint number => number,
        ulong number => number,
        _ => null
    };
}

internal sealed class FrostbiteDb(byte[] data)
{
    private int _position;

    public static DbNode Read(string path)
    {
        var parser = new FrostbiteDb(File.ReadAllBytes(path));
        return parser.ReadNode();
    }

    public static DbNode Read(byte[] data) => new FrostbiteDb(data).ReadNode();

    public static DbNode ReadSigned(string path)
    {
        return Read(DecodeSigned(File.ReadAllBytes(path)));
    }

    public static byte[] DecodeSigned(byte[] source)
    {
        if (source.Length < 556 || !source.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0xD1, 0xCE, 1 }))
            throw new InvalidDataException("Expected signed/XOR Frostbite DB");
        var decoded = new byte[source.Length - 556];
        for (var index = 0; index < decoded.Length; index++)
            decoded[index] = (byte)(source[index + 556] ^ source[296 + index % 257] ^ 0x7B);
        return decoded;
    }

    public static byte[] EncodeSigned(byte[] original, byte[] decoded)
    {
        if (decoded.Length != original.Length - 556)
            throw new InvalidDataException("Signed/XOR DB changed byte length");
        var result = original.ToArray();
        for (var index = 0; index < decoded.Length; index++)
            result[index + 556] = (byte)(decoded[index] ^ original[296 + index % 257] ^ 0x7B);
        return result;
    }

    private byte ReadByte()
    {
        if (_position >= data.Length) throw new InvalidDataException("Unexpected end of Frostbite DB");
        return data[_position++];
    }

    private byte[] ReadBytes(int count)
    {
        if (count < 0 || (long)_position + count > data.Length)
            throw new InvalidDataException("Frostbite DB value extends past end of file");
        var result = data.AsSpan(_position, count).ToArray();
        _position += count;
        return result;
    }

    private ulong ReadUnsigned(int count)
    {
        var bytes = ReadBytes(count);
        return count switch
        {
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(count))
        };
    }

    private ulong ReadVarUInt()
    {
        ulong value = 0;
        for (var shift = 0; shift <= 63; shift += 7)
        {
            var next = ReadByte();
            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Invalid Frostbite DB variable integer");
    }

    private string ReadName()
    {
        var start = _position;
        while (ReadByte() != 0) { }
        return Encoding.UTF8.GetString(data, start, _position - start - 1);
    }

    private DbNode ReadNode()
    {
        var offset = _position;
        var header = ReadByte();
        var type = header & 0x1F;
        var name = (header & 0x80) != 0 ? "" : ReadName();
        var valueOffset = _position;
        object? value = null;
        List<DbNode>? children = null;
        switch (type)
        {
            case 1: // array
            case 2: // object
            {
                var byteLength = checked((int)ReadVarUInt());
                var end = checked(_position + byteLength);
                if (byteLength < 1 || end > data.Length)
                    throw new InvalidDataException($"Invalid DB container at 0x{offset:X}");
                children = [];
                while (_position < end - 1) children.Add(ReadNode());
                if (_position != end - 1 || ReadByte() != 0)
                    throw new InvalidDataException($"Missing DB container terminator at 0x{offset:X}");
                break;
            }
            case 4: // null
                break;
            case 5: // object ID
                value = ReadBytes(12);
                break;
            case 6: // bool
                value = ReadByte() != 0;
                break;
            case 7: // NUL-terminated, sized UTF-8 string
            {
                var length = checked((int)ReadVarUInt());
                var bytes = ReadBytes(length);
                if (length == 0 || bytes[^1] != 0)
                    throw new InvalidDataException($"Invalid DB string at 0x{offset:X}");
                value = Encoding.UTF8.GetString(bytes, 0, length - 1);
                break;
            }
            case 8:
                value = (uint)ReadUnsigned(4);
                break;
            case 9:
                value = ReadUnsigned(8);
                break;
            case 10:
            case 21:
                value = ReadVarUInt();
                break;
            case 11: // float
                value = BitConverter.Int32BitsToSingle((int)ReadUnsigned(4));
                break;
            case 12: // double
                value = BitConverter.Int64BitsToDouble((long)ReadUnsigned(8));
                break;
            case 13: // timestamp
                value = ReadBytes(8);
                break;
            case 14: // record ID
                value = ReadBytes(6);
                break;
            case 15: // GUID
                value = ReadBytes(16);
                break;
            case 16: // SHA-1
                value = ReadBytes(20);
                break;
            case 17: // matrix
                value = ReadBytes(64);
                break;
            case 18: // vector
                value = ReadBytes(16);
                break;
            case 19: // binary blob, including inline payloads
                value = ReadBytes(checked((int)ReadVarUInt()));
                break;
            case 20: // attachment SHA-1
                value = ReadBytes(20);
                break;
            default:
                throw new InvalidDataException($"Unsupported Frostbite DB type {type} at 0x{offset:X}");
        }
        return new DbNode(name, type, offset, valueOffset, value, children);
    }
}
