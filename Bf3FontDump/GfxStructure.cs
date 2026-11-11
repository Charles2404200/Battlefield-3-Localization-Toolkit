using System.Buffers.Binary;

namespace Bf3FontDump;

internal sealed record GfxTag(int Code, int PayloadOffset, int Length);
internal sealed record GfxFont(int Id, int GlyphCount, int CodeTableOffset, int FirstGlyphOffset,
    int LastGlyphOffset, int ShapeBytes, int DuplicateCodeCount);
internal sealed record GfxStructure(int TagCount, int EndOffset, int TrailingBytes, int FontTagCount,
    IReadOnlyList<GfxTag> Tags);

internal static class GfxInspector
{
    public static GfxFont AnalyzeFont(byte[] data, GfxTag tag)
    {
        if (tag.Code != 75 || tag.Length < 8) throw new InvalidDataException("Expected DefineFont3 tag");
        var span = data.AsSpan(tag.PayloadOffset, tag.Length);
        var id = BinaryPrimitives.ReadUInt16LittleEndian(span);
        var flags = span[2];
        if ((flags & 0x0C) != 0x0C)
            throw new InvalidDataException($"Font {id} requires wide offsets and codes");
        var nameLength = span[4];
        var countAt = 5 + nameLength;
        if (countAt + 2 > span.Length) throw new InvalidDataException($"Font {id} name is truncated");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(countAt, 2));
        var offsetsAt = countAt + 2;
        var codeOffsetAt = checked(offsetsAt + count * 4);
        if (codeOffsetAt + 4 > span.Length) throw new InvalidDataException($"Font {id} glyph offsets are truncated");
        var codeOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(codeOffsetAt, 4));
        var codeTableAt = checked(offsetsAt + (int)codeOffset);
        if (codeOffset < count * 4 + 4 || codeTableAt > span.Length ||
            (long)codeTableAt + count * 2 > span.Length)
            throw new InvalidDataException($"Font {id} code table lies outside tag");
        var prior = 0;
        var first = 0;
        for (var index = 0; index < count; index++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offsetsAt + index * 4, 4));
            if (offset < count * 4 + 4 || offset >= codeOffset || offset < prior)
                throw new InvalidDataException($"Font {id} glyph {index} has invalid offset {offset}");
            if (index == 0) first = checked((int)offset);
            prior = checked((int)offset);
        }
        var codes = new HashSet<ushort>();
        var duplicates = 0;
        for (var index = 0; index < count; index++)
        {
            var code = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(codeTableAt + index * 2, 2));
            if (!codes.Add(code)) duplicates++;
        }
        return new GfxFont(id, count, checked((int)codeOffset), first, prior,
            checked((int)codeOffset) - (count * 4 + 4), duplicates);
    }

    public static GfxStructure Analyze(byte[] bytes)
    {
        if (bytes.Length < 16 || !bytes.AsSpan().StartsWith("GFX"u8))
            throw new InvalidDataException("GFX header missing");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != bytes.Length)
            throw new InvalidDataException("GFX header length differs from file length");
        var rectBits = 5 + 4 * (bytes[8] >> 3);
        var position = 8 + (rectBits + 7) / 8 + 4; // SWF frame RECT, rate, count
        if (position >= bytes.Length) throw new InvalidDataException("GFX frame header is truncated");
        var tagCount = 0;
        var fontTags = 0;
        var tags = new List<GfxTag>();
        while (position + 2 <= bytes.Length)
        {
            var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
            position += 2;
            var tag = raw >> 6;
            uint length = (uint)(raw & 63);
            if (length == 63)
            {
                if (position + 4 > bytes.Length) throw new InvalidDataException("Extended GFX tag length is truncated");
                length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, 4));
                position += 4;
            }
            if (length > bytes.Length - position)
                throw new InvalidDataException($"GFX tag {tag} extends past end of file at 0x{position:X}");
            if (tag is 10 or 48 or 75 or 91) fontTags++;
            tags.Add(new GfxTag(tag, position, checked((int)length)));
            position += checked((int)length);
            tagCount++;
            if (tag == 0)
            {
                if (length != 0) throw new InvalidDataException("GFX End tag has payload");
                return new GfxStructure(tagCount, position, bytes.Length - position, fontTags, tags);
            }
        }
        throw new InvalidDataException("GFX End tag is missing");
    }
}
