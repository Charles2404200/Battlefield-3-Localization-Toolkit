using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf3TextDump;

public static partial class Program
{
    private const string HistogramGuid = "8676A58B815A29A2726B2D0F7ECB9B37";
    private const int HistogramMapOffset = 0x10C;
    private const uint CharsetTestId = 0x94005F41;

    private sealed record VietnameseMap(
        TocChunk Chunk,
        byte[] DecodedHistogram,
        byte[] PackedSlot,
        IReadOnlyDictionary<char, byte> CharacterBytes,
        IReadOnlyDictionary<ushort, ushort> LowGlyphRemap,
        IReadOnlyDictionary<byte, ushort> PreservedBytes);

    private static VietnameseMap BuildVietnameseMap(string baselineSb, IReadOnlyList<TocChunk> chunks,
        byte[] originalText, IReadOnlyList<ChunkEntry> entries)
    {
        if (chunks.Count != 5 || chunks[2].GuidBytes != HistogramGuid)
            throw new InvalidDataException("Expected BF3 English histogram as TOC chunk 2");
        var histogramChunk = chunks[2];
        var (decoded, originalSlot) = ReadHistogram(baselineSb, histogramChunk);
        if (decoded.Length != 6156 || histogramChunk.Size != 367 ||
            BinaryPrimitives.ReadUInt32LittleEndian(decoded) != 0x39001 ||
            BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(4, 4)) + 8 != decoded.Length ||
            HistogramMapOffset + 128 * 2 > decoded.Length)
            throw new InvalidDataException("Unknown BF3 histogram layout");
        ushort OriginalCode(byte value) => BinaryPrimitives.ReadUInt16LittleEndian(
            decoded.AsSpan(HistogramMapOffset + (value - 0x80) * 2, 2));
        if (OriginalCode(0x80) != 2 || OriginalCode(0x81) != 0x2122 ||
            OriginalCode(0x82) != 0x201C || OriginalCode(0xF4) != 0x00F4)
            throw new InvalidDataException("BF3 histogram character table does not match this English build");

        var usedHigh = new HashSet<byte>();
        foreach (var entry in entries)
        {
            if (entry.Hash == CharsetTestId) continue;
            foreach (var value in originalText.AsSpan(entry.AbsoluteOffset, entry.RawLength))
                if (value >= 0x80) usedHigh.Add(value);
        }
        if (usedHigh.Contains(0x80))
            throw new InvalidDataException("A game string outside the charset test uses histogram escape byte 0x80");

        var alphabet = VietnameseAlphabet();
        var characterBytes = new Dictionary<char, byte>();
        var preserved = new Dictionary<byte, ushort>();
        foreach (var value in usedHigh.OrderBy(value => value))
        {
            var code = OriginalCode(value);
            preserved.Add(value, code);
            if (alphabet.Contains(code) && !characterBytes.TryAdd((char)code, value))
                throw new InvalidDataException($"Vietnamese character U+{code:X4} has duplicate original bytes");
        }

        var freeHigh = Enumerable.Range(0x81, 0x7F).Select(value => (byte)value)
            .Where(value => !usedHigh.Contains(value)).ToArray();
        var remaining = alphabet.Where(code => !characterBytes.ContainsKey((char)code))
            .OrderBy(code => code).ToArray();
        var lowNeeded = remaining.Length - freeHigh.Length;
        // These avoid TAB, LF and CR. The first eight have been checked in the BF3 menu.
        ushort[] lowSlots = [1, 2, 3, 4, 5, 6, 7, 8, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24];
        if (lowNeeded != lowSlots.Length)
            throw new InvalidDataException($"This English build needs {lowNeeded} low glyph slots; expected {lowSlots.Length}");

        var lowGlyphRemap = new Dictionary<ushort, ushort>();
        for (var i = 0; i < lowNeeded; i++)
        {
            var code = remaining[i];
            characterBytes.Add((char)code, checked((byte)lowSlots[i]));
            lowGlyphRemap.Add(code, lowSlots[i]);
        }
        var highCharacters = remaining.Skip(lowNeeded).ToArray();
        if (highCharacters.Length != freeHigh.Length)
            throw new InvalidDataException("High-byte mapping length mismatch");
        for (var i = 0; i < freeHigh.Length; i++)
        {
            var code = highCharacters[i];
            var value = freeHigh[i];
            characterBytes.Add((char)code, value);
            BinaryPrimitives.WriteUInt16LittleEndian(
                decoded.AsSpan(HistogramMapOffset + (value - 0x80) * 2, 2), code);
        }
        if (characterBytes.Count != 134 || characterBytes.Values.Distinct().Count() != 134)
            throw new InvalidDataException("Vietnamese map must contain 134 unique non-ASCII bytes");
        foreach (var (value, code) in preserved)
            if (OriginalCode(value) != code)
                throw new InvalidDataException($"Original special byte 0x{value:X2} was changed");

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(decoded);
        var payload = compressed.ToArray();
        var capacity = originalSlot.Length - 8;
        if (payload.Length > capacity)
            throw new InvalidDataException($"Vietnamese histogram needs {payload.Length} compressed bytes, slot has {capacity}");
        var packedSlot = originalSlot.ToArray();
        packedSlot.AsSpan(8).Clear();
        payload.CopyTo(packedSlot, 8);
        return new VietnameseMap(histogramChunk, decoded, packedSlot, characterBytes, lowGlyphRemap, preserved);
    }

    private static HashSet<ushort> VietnameseAlphabet()
    {
        var result = new HashSet<ushort>();
        foreach (var vowel in "aăâeêioôơuưyAĂÂEÊIOÔƠUƯY")
            foreach (var tone in new[] { "", "\u0301", "\u0300", "\u0309", "\u0303", "\u0323" })
            {
                var composed = (vowel + tone).Normalize(NormalizationForm.FormC);
                if (composed.Length != 1)
                    throw new InvalidDataException($"Could not compose Vietnamese character {vowel}+{tone}");
                if (composed[0] >= 0x80) result.Add(composed[0]);
            }
        result.Add('đ');
        result.Add('Đ');
        if (result.Count != 134) throw new InvalidDataException("Vietnamese alphabet must have 134 non-ASCII letters");
        return result;
    }

    private static (byte[] Decoded, byte[] PackedSlot) ReadHistogram(string sb, TocChunk chunk)
    {
        var slot = new byte[checked((int)chunk.Size)];
        using (var source = File.OpenRead(sb))
        {
            source.Position = checked((long)chunk.Offset);
            source.ReadExactly(slot);
        }
        if (slot.Length < 10) throw new InvalidDataException("Histogram CAS slot is too short");
        var decodedSize = BinaryPrimitives.ReadUInt32BigEndian(slot.AsSpan(0, 4));
        var packedSize = BinaryPrimitives.ReadUInt32BigEndian(slot.AsSpan(4, 4));
        if (decodedSize is 0 or > 65536 || packedSize != slot.Length - 8 || slot[8] != 0x78)
            throw new InvalidDataException("Histogram CAS slot has unexpected zlib header");
        using var input = new MemoryStream(slot, 8, checked((int)packedSize));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        if (decoded.Length != decodedSize)
            throw new InvalidDataException("Histogram decoded size mismatch");
        return (decoded.ToArray(), slot);
    }

    private static void WriteVietnameseMap(string directory, VietnameseMap map)
    {
        var manifest = new
        {
            format = "BF3 Vietnamese character map v1",
            histogram_guid = map.Chunk.GuidBytes,
            histogram_offset = map.Chunk.Offset,
            histogram_packed_size = map.Chunk.Size,
            histogram_decoded_sha256 = HashHex(SHA256.HashData(map.DecodedHistogram)),
            vietnamese_char_to_byte = map.CharacterBytes.OrderBy(pair => pair.Key)
                .ToDictionary(pair => $"U+{(int)pair.Key:X4}", pair => $"0x{pair.Value:X2}"),
            low_glyph_remap = map.LowGlyphRemap.OrderBy(pair => pair.Key)
                .ToDictionary(pair => $"U+{pair.Key:X4}", pair => $"U+{pair.Value:X4}"),
            preserved_original_bytes = map.PreservedBytes.OrderBy(pair => pair.Key)
                .ToDictionary(pair => $"0x{pair.Key:X2}", pair => $"U+{pair.Value:X4}")
        };
        File.WriteAllText(Path.Combine(directory, "vietnamese_map.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    private static void ApplyVietnameseHistogram(string stageSb, VietnameseMap map)
    {
        using (var stage = new FileStream(stageSb, FileMode.Open, FileAccess.Write))
        {
            stage.Position = checked((long)map.Chunk.Offset);
            stage.Write(map.PackedSlot);
        }
        var (readBack, _) = ReadHistogram(stageSb, map.Chunk);
        if (!readBack.AsSpan().SequenceEqual(map.DecodedHistogram))
            throw new InvalidDataException("Staged Vietnamese histogram did not decode correctly");
    }
}
