using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf3TextDump;

public static partial class Program
{
    private const string UsLocGuidBytes = "22550350D322C3B89AACCE617DEAE923";
    private sealed record TocChunk(string GuidBytes, ulong Offset, uint Size);
    private sealed record TextChange(string id_hash, int old_offset, int new_offset,
        int old_byte_length, int new_byte_length, string vietnamese);
    private sealed record EncodedPrefix(byte[] Bytes, string Compressor, int BlockSize, int Padding);
    private sealed class PackedSlotOverflowException(string message) : IOException(message);

    private static string ResolveReferenceInput(Options options)
    {
        if (!options.Import && !options.ValidateOnly) return options.Input;
        var backup = Path.Combine(Path.GetFullPath(options.StageDirectory), "backup", "en.sb");
        if (!File.Exists(backup)) return options.Input;
        var gameSb = Path.GetFullPath(options.Input);
        if (!gameSb.Replace('\\', '/').EndsWith("/Update/Patch/Data/Win32/Loc/en.sb",
                StringComparison.OrdinalIgnoreCase)) return options.Input;
        var installedHash = HashHex(SHA256.HashData(File.ReadAllBytes(gameSb)));
        var backupHash = HashHex(SHA256.HashData(File.ReadAllBytes(backup)));
        var staged = Path.Combine(Path.GetFullPath(options.StageDirectory), "en.sb");
        var stagedHash = File.Exists(staged)
            ? HashHex(SHA256.HashData(File.ReadAllBytes(staged))) : null;
        if (installedHash != backupHash && installedHash != stagedHash)
            throw new InvalidDataException("Installed en.sb differs from both the backup and last staged build");
        return backup;
    }

    private static void ImportTranslations(Options options, string baselineSb,
        byte[] originalChunk, List<BlockInfo> blocks,
        List<ChunkEntry> entries, ChunkHeader header, IReadOnlyDictionary<uint, string> translations)
    {
        var gameSb = Path.GetFullPath(options.Input);
        if (!Path.GetExtension(gameSb).Equals(".sb", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import requires the patched Loc/en.sb, not an extracted chunk");
        var sourceToc = Path.ChangeExtension(gameSb, ".toc");
        if (!File.Exists(sourceToc)) throw new FileNotFoundException("Matching en.toc is missing", sourceToc);
        var tocChunks = ReadTocChunks(sourceToc);
        var originalPrefixLength = checked((int)(blocks[^1].file_offset + 8 + blocks[^1].packed_size));
        if (tocChunks.Count != 5 ||
            tocChunks[0].GuidBytes != UsLocGuidBytes ||
            tocChunks[0].Offset != 0 || tocChunks[0].Size != originalPrefixLength)
            throw new InvalidDataException("Patch TOC does not match the expected us_loc chunk at file offset 0");
        var sourceLength = new FileInfo(baselineSb).Length;
        ulong nextOffset = 0;
        foreach (var item in tocChunks)
        {
            if (item.Offset != nextOffset)
                throw new InvalidDataException("Patch TOC chunks are not contiguous");
            nextOffset += item.Size;
        }
        if (nextOffset != (ulong)sourceLength)
            throw new InvalidDataException("Patch TOC chunk sizes do not cover en.sb exactly");
        var vietnameseMap = options.MapVietnamese
            ? BuildVietnameseMap(baselineSb, tocChunks, originalChunk, entries) : null;
        Dictionary<char, byte>? translationBytes = null;
        if (vietnameseMap is not null)
        {
            translationBytes = new Dictionary<char, byte>(vietnameseMap.CharacterBytes);
            foreach (var (value, code) in vietnameseMap.PreservedBytes)
                if (code >= 0x80) translationBytes.TryAdd((char)code, value);
        }

        var noOp = RebuildChunk(originalChunk, entries, header, new Dictionary<uint, string>(), out _);
        if (!noOp.AsSpan().SequenceEqual(originalChunk))
            throw new InvalidDataException("No-op rebuild changed the original us_loc chunk");
        var modifiedChunk = RebuildChunk(originalChunk, entries, header, translations, out var changes,
            translationBytes);
        if (changes.Count == 0)
            throw new InvalidDataException("All filled translations are byte-identical to English");
        VerifyRebuiltContent(originalChunk, modifiedChunk, entries, translations, translationBytes);

        var stageDir = Path.GetFullPath(options.StageDirectory);
        Directory.CreateDirectory(stageDir);
        File.WriteAllBytes(Path.Combine(stageDir, "modified_chunk.bin"), modifiedChunk);
        var textLayout = "original";
        EncodedPrefix encoded;
        try
        {
            encoded = EncodeToOriginalLength(modifiedChunk, originalPrefixLength, quickZopfli: true);
        }
        catch (PackedSlotOverflowException)
        {
            Console.WriteLine("Original text order did not fit; grouping translated strings at the end.");
            var compactChunk = RebuildChunk(originalChunk, entries, header, translations,
                out var compactChanges, translationBytes, translatedLast: true);
            VerifyRebuiltContent(originalChunk, compactChunk, entries, translations, translationBytes);
            try
            {
                encoded = EncodeToOriginalLength(compactChunk, originalPrefixLength);
                modifiedChunk = compactChunk;
                changes = compactChanges;
                textLayout = "translated-last";
            }
            catch (PackedSlotOverflowException)
            {
                Console.WriteLine("Grouped text did not fit; retrying all block sizes with original order.");
                encoded = EncodeToOriginalLength(modifiedChunk, originalPrefixLength);
            }
        }
        File.WriteAllBytes(Path.Combine(stageDir, "modified_chunk.bin"), modifiedChunk);
        var stageSb = Path.Combine(stageDir, "en.sb");
        var stageToc = Path.Combine(stageDir, "en.toc");
        using (var source = File.OpenRead(baselineSb))
        using (var destination = File.Create(stageSb))
        {
            destination.Write(encoded.Bytes);
            source.Position = originalPrefixLength;
            source.CopyTo(destination);
        }
        File.Copy(sourceToc, stageToc, overwrite: true);
        if (vietnameseMap is not null)
        {
            ApplyVietnameseHistogram(stageSb, vietnameseMap);
            WriteVietnameseMap(stageDir, vietnameseMap);
        }
        if (new FileInfo(stageSb).Length != sourceLength)
            throw new InvalidDataException("Staged SB changed total file length");
        var (readBack, _, _) = ReadLocalizationChunk(stageSb);
        if (!readBack.AsSpan().SequenceEqual(modifiedChunk))
            throw new InvalidDataException("Staged SB did not decode to the modified chunk");
        if (vietnameseMap is null) VerifyTailEqual(baselineSb, stageSb, originalPrefixLength);
        else
        {
            VerifyRangeEqual(baselineSb, stageSb, originalPrefixLength,
                checked((long)vietnameseMap.Chunk.Offset));
            VerifyRangeEqual(baselineSb, stageSb,
                checked((long)(vietnameseMap.Chunk.Offset + vietnameseMap.Chunk.Size)), sourceLength);
        }

        var manifest = new
        {
            source_sb = baselineSb,
            target_sb = gameSb,
            source_toc = sourceToc,
            staged_sb = stageSb,
            staged_toc = stageToc,
            toc_unchanged = true,
            original_sb_size = sourceLength,
            original_prefix_size = originalPrefixLength,
            original_chunk_size = originalChunk.Length,
            modified_chunk_size = modifiedChunk.Length,
            compressor = encoded.Compressor,
            text_layout = textLayout,
            zlib_block_size = encoded.BlockSize,
            padding_inside_last_block = encoded.Padding,
            original_chunk_sha256 = HashHex(SHA256.HashData(originalChunk)),
            modified_chunk_sha256 = HashHex(SHA256.HashData(modifiedChunk)),
            staged_sb_sha256 = HashHex(SHA256.HashData(File.ReadAllBytes(stageSb))),
            text_encoding = options.MapVietnamese ? "BF3 histogram-mapped Vietnamese" : "UTF-8 with raw byte escapes",
            histogram_modified = vietnameseMap is not null,
            vietnamese_map = vietnameseMap is null ? null : Path.Combine(stageDir, "vietnamese_map.json"),
            changes
        };
        var manifestPath = Path.Combine(stageDir, "import_manifest.json");
        File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        Console.WriteLine($"Staged {changes.Count} changed IDs: {originalChunk.Length} -> {modifiedChunk.Length} decoded bytes");
        Console.WriteLine(vietnameseMap is null
            ? $"Compressed prefix stays {originalPrefixLength} bytes; TOC and other 4 chunks stay unchanged"
            : $"Compressed prefix stays {originalPrefixLength} bytes; histogram uses the same {vietnameseMap.Chunk.Size}-byte TOC slot; TOC unchanged");
        Console.WriteLine($"Staged SB: {stageSb}");
        Console.WriteLine($"Manifest: {manifestPath}");
        if (options.Install) InstallStagedSb(gameSb, sourceToc, stageSb, stageDir, modifiedChunk, vietnameseMap);
    }

    private static byte[] RebuildChunk(byte[] original, List<ChunkEntry> hashEntries,
        ChunkHeader header, IReadOnlyDictionary<uint, string> translations, out List<TextChange> changes,
        IReadOnlyDictionary<char, byte>? vietnameseBytes = null, bool translatedLast = false)
    {
        var textBase = header.text_base;
        var prefix = original.AsSpan(0, textBase).ToArray();
        var recordIndex = hashEntries.Select((entry, index) => (entry.Hash, index))
            .ToDictionary(item => item.Hash, item => item.index);
        var expectedOldOffset = 0;
        var physicalOrder = hashEntries.OrderBy(entry => entry.RelativeOffset).ToArray();
        foreach (var entry in physicalOrder)
        {
            if (entry.RelativeOffset != expectedOldOffset)
                throw new InvalidDataException($"String region has a gap before ID 0x{entry.Hash:X8}");
            expectedOldOffset += entry.RawLength + 1;
        }
        using var output = new MemoryStream(original.Length + 1024);
        output.Write(prefix);
        changes = [];
        var outputOrder = translatedLast
            ? physicalOrder.OrderBy(entry => translations.ContainsKey(entry.Hash) ? 1 : 0)
            : physicalOrder.AsEnumerable();
        foreach (var entry in outputOrder)
        {
            var newOffset = checked((int)output.Position - textBase);
            var raw = original.AsSpan(entry.AbsoluteOffset, entry.RawLength).ToArray();
            var replacement = translations.TryGetValue(entry.Hash, out var vietnamese)
                ? EncodeTranslation(vietnamese!, vietnameseBytes) : raw;
            if (!replacement.AsSpan().SequenceEqual(raw))
                changes.Add(new TextChange($"0x{entry.Hash:X8}", (int)entry.RelativeOffset,
                    newOffset, raw.Length, replacement.Length, vietnamese!));
            var offsetAt = checked((int)header.table_offset + 8 * (recordIndex[entry.Hash] + 1) + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(offsetAt, 4), (uint)newOffset);
            output.Write(replacement);
            output.WriteByte(0);
        }
        var tail = original.AsSpan(textBase + expectedOldOffset);
        if (tail.Length > 3 || tail.IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException($"String region has {tail.Length} non-padding trailing bytes");
        output.Write(tail);
        var result = output.ToArray();
        prefix.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)(result.Length - 8)));
        return result;
    }

    private static void VerifyRebuiltContent(byte[] original, byte[] rebuilt,
        IReadOnlyList<ChunkEntry> oldEntries, IReadOnlyDictionary<uint, string> translations,
        IReadOnlyDictionary<char, byte>? vietnameseBytes)
    {
        var (newEntries, _) = ParseChunk(rebuilt);
        if (newEntries.Count != oldEntries.Count)
            throw new InvalidDataException("Rebuilt chunk lost text IDs");
        var byHash = newEntries.ToDictionary(entry => entry.Hash);
        foreach (var old in oldEntries)
        {
            if (!byHash.TryGetValue(old.Hash, out var current))
                throw new InvalidDataException($"Rebuilt chunk lost ID 0x{old.Hash:X8}");
            var expected = translations.TryGetValue(old.Hash, out var translation)
                ? EncodeTranslation(translation, vietnameseBytes)
                : original.AsSpan(old.AbsoluteOffset, old.RawLength).ToArray();
            if (current.RawLength != expected.Length ||
                !rebuilt.AsSpan(current.AbsoluteOffset, current.RawLength).SequenceEqual(expected))
                throw new InvalidDataException($"Rebuilt text differs for ID 0x{old.Hash:X8}");
        }
    }

    private static byte[] EncodeTranslation(string text, IReadOnlyDictionary<char, byte>? vietnameseBytes)
    {
        if (vietnameseBytes is not null) text = text.Normalize(NormalizationForm.FormC);
        var result = new List<byte>();
        var pending = new StringBuilder();
        var utf8 = new UTF8Encoding(false, true);
        void Flush()
        {
            if (pending.Length == 0) return;
            if (vietnameseBytes is not null)
            {
                foreach (var ch in pending.ToString())
                {
                    if (ch <= 0x7F) result.Add((byte)ch);
                    else if (vietnameseBytes.TryGetValue(ch, out var mapped)) result.Add(mapped);
                    else throw new InvalidDataException($"Vietnamese histogram does not map '{ch}' (U+{(int)ch:X4})");
                }
            }
            else result.AddRange(utf8.GetBytes(pending.ToString()));
            pending.Clear();
        }
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                if (text[index + 1] == '\\')
                {
                    pending.Append('\\');
                    index++;
                    continue;
                }
                if (text[index + 1] == 'x' && index + 3 < text.Length &&
                    byte.TryParse(text.AsSpan(index + 2, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var special))
                {
                    Flush();
                    if (special == 0) throw new InvalidDataException("Translation cannot contain NUL (\\x00)");
                    result.Add(special);
                    index += 3;
                    continue;
                }
            }
            if (text[index] == '\0') throw new InvalidDataException("Translation cannot contain NUL");
            pending.Append(text[index]);
        }
        Flush();
        return [.. result];
    }

    private static EncodedPrefix EncodeToOriginalLength(byte[] chunk, int originalLength,
        bool quickZopfli = false)
    {
        var candidates = new List<(byte[] Bytes, CompressionLevel Level, int BlockSize, int LastHeader, int LastPacked)>();
        var blockSizes = new[] { 65536, 65520, 65504, 65472, 65408, 65280, 65024,
            64512, 64000, 63000, 62000, 60000, 57344, 49152, 32768 };
        Span<byte> blockHeader = stackalloc byte[8];
        foreach (var blockSize in blockSizes)
        {
            var level = CompressionLevel.SmallestSize;
            using var output = new MemoryStream();
            var lastHeader = 0;
            var lastPacked = 0;
            for (var start = 0; start < chunk.Length; start += blockSize)
            {
                var length = Math.Min(blockSize, chunk.Length - start);
                using var packedStream = new MemoryStream();
                using (var zlib = new ZLibStream(packedStream, level, leaveOpen: true))
                    zlib.Write(chunk, start, length);
                var packed = packedStream.ToArray();
                lastHeader = (int)output.Position;
                lastPacked = packed.Length;
                BinaryPrimitives.WriteUInt32BigEndian(blockHeader[..4], (uint)length);
                BinaryPrimitives.WriteUInt32BigEndian(blockHeader[4..], (uint)packed.Length);
                output.Write(blockHeader);
                output.Write(packed);
            }
            candidates.Add((output.ToArray(), level, blockSize, lastHeader, lastPacked));
        }
        var chosen = candidates.Where(candidate => candidate.Bytes.Length <= originalLength &&
                candidate.LastPacked + originalLength - candidate.Bytes.Length <= 65536)
            .OrderByDescending(candidate => candidate.Bytes.Length).FirstOrDefault();
        if (chosen.Bytes is null)
        {
            Console.WriteLine($"Standard zlib needs at least {candidates.Min(item => item.Bytes.Length):N0} bytes; " +
                $"trying stronger Zopfli compression for the {originalLength:N0}-byte TOC slot.");
            return EncodeWithZopfli(chunk, originalLength, quickZopfli);
        }
        var padding = originalLength - chosen.Bytes.Length;
        var padded = new byte[originalLength];
        chosen.Bytes.CopyTo(padded, 0);
        BinaryPrimitives.WriteUInt32BigEndian(padded.AsSpan(chosen.LastHeader + 4, 4),
            (uint)(chosen.LastPacked + padding));
        return new EncodedPrefix(padded, "zlib SmallestSize", chosen.BlockSize, padding);
    }

    private static EncodedPrefix EncodeWithZopfli(byte[] chunk, int originalLength, bool quick)
    {
        var bestLength = int.MaxValue;
        var candidates = quick
            ? new[] { (63500, 15), (64000, 15), (59250, 5) }
            : (from iterations in new[] { 5, 15 }
               from blockSize in new[] { 64000, 63500, 59250, 59100, 59300, 58500,
                   60500, 60000, 62000, 63000, 65536, 61500, 62500, 61000 }
               select (blockSize, iterations)).ToArray();
        foreach (var (blockSize, iterations) in candidates)
        {
            using var output = new MemoryStream();
            var lastHeader = 0;
            var lastPacked = 0;
            Span<byte> blockHeader = stackalloc byte[8];
            for (var start = 0; start < chunk.Length; start += blockSize)
            {
                var length = Math.Min(blockSize, chunk.Length - start);
                var packed = ZopfliCompressor.CompressBlock(chunk.AsSpan(start, length), iterations);
                lastHeader = checked((int)output.Position);
                lastPacked = packed.Length;
                BinaryPrimitives.WriteUInt32BigEndian(blockHeader[..4], (uint)length);
                BinaryPrimitives.WriteUInt32BigEndian(blockHeader[4..], (uint)packed.Length);
                output.Write(blockHeader);
                output.Write(packed);
            }
            var encoded = output.ToArray();
            bestLength = Math.Min(bestLength, encoded.Length);
            var padding = originalLength - encoded.Length;
            Console.WriteLine($"Zopfli i{iterations} block={blockSize}: {encoded.Length:N0}/{originalLength:N0} bytes");
            if (padding < 0 || lastPacked + padding > 65536) continue;
            var padded = new byte[originalLength];
            encoded.CopyTo(padded, 0);
            BinaryPrimitives.WriteUInt32BigEndian(padded.AsSpan(lastHeader + 4, 4),
                checked((uint)(lastPacked + padding)));
            return new EncodedPrefix(padded, $"Zopfli zlib i{iterations}", blockSize, padding);
        }
        throw new PackedSlotOverflowException($"Even Zopfli needs {bestLength:N0} bytes for the translated text, " +
            $"but the signed TOC slot holds {originalLength:N0}. No game file was changed.");
    }

    private static List<TocChunk> ReadTocChunks(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 556 || !data.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0xD1, 0xCE, 1 }))
            throw new InvalidDataException("Expected signed/XOR BF3 patch TOC");
        var decoded = new byte[data.Length - 556];
        for (var index = 0; index < decoded.Length; index++)
            decoded[index] = (byte)(data[index + 556] ^ data[296 + index % 257] ^ 0x7B);
        var records = new List<TocChunk>();
        ReadOnlySpan<byte> idMarker = [15, (byte)'i', (byte)'d', 0];
        ReadOnlySpan<byte> offsetMarker = [9, (byte)'o', (byte)'f', (byte)'f', (byte)'s', (byte)'e', (byte)'t', 0];
        ReadOnlySpan<byte> sizeMarker = [8, (byte)'s', (byte)'i', (byte)'z', (byte)'e', 0];
        for (var at = 0; at + 42 <= decoded.Length; at++)
        {
            if (!decoded.AsSpan(at, 4).SequenceEqual(idMarker)) continue;
            var guid = Convert.ToHexString(decoded.AsSpan(at + 4, 16));
            var next = at + 20;
            if (!decoded.AsSpan(next, 8).SequenceEqual(offsetMarker) ||
                !decoded.AsSpan(next + 16, 6).SequenceEqual(sizeMarker)) continue;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(decoded.AsSpan(next + 8, 8));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(next + 22, 4));
            records.Add(new TocChunk(guid, offset, size));
            at = next + 25;
        }
        return records;
    }

    private static void VerifyTailEqual(string original, string staged, int start)
        => VerifyRangeEqual(original, staged, start, new FileInfo(original).Length);

    private static void VerifyRangeEqual(string original, string staged, long start, long end)
    {
        using var a = File.OpenRead(original);
        using var b = File.OpenRead(staged);
        a.Position = start;
        b.Position = start;
        var left = new byte[65536];
        var right = new byte[65536];
        while (a.Position < end)
        {
            var read = a.Read(left.AsSpan(0, checked((int)Math.Min(left.Length, end - a.Position))));
            if (read == 0) throw new EndOfStreamException("Unexpected end while comparing unchanged SB ranges");
            b.ReadExactly(right.AsSpan(0, read));
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read)))
                throw new InvalidDataException($"Unchanged SB range differs at file offset 0x{a.Position - read:X}");
        }
    }

    private static void InstallStagedSb(string sourceSb, string sourceToc, string stageSb,
        string stageDir, byte[] expectedChunk, VietnameseMap? vietnameseMap)
    {
        if (!sourceSb.Replace('\\', '/').EndsWith("/Update/Patch/Data/Win32/Loc/en.sb",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("--install requires the real Update/Patch/Data/Win32/Loc/en.sb");
        if (Process.GetProcessesByName("bf3").Length != 0)
            throw new InvalidDataException("Close Battlefield 3 before installing translated en.sb");
        var backupDir = Path.Combine(stageDir, "backup");
        Directory.CreateDirectory(backupDir);
        var backupSb = Path.Combine(backupDir, "en.sb");
        var backupToc = Path.Combine(backupDir, "en.toc");
        if (!File.Exists(backupSb)) File.Copy(sourceSb, backupSb);
        if (!File.Exists(backupToc)) File.Copy(sourceToc, backupToc);
        var temp = sourceSb + ".bf3vi.tmp";
        var previous = Path.Combine(stageDir, "previous_en.sb");
        File.Copy(sourceSb, previous, overwrite: true);
        try
        {
            File.Copy(stageSb, temp, overwrite: true);
            File.Move(temp, sourceSb, overwrite: true);
            var (installedChunk, _, _) = ReadLocalizationChunk(sourceSb);
            if (!installedChunk.AsSpan().SequenceEqual(expectedChunk))
                throw new InvalidDataException("Installed SB did not decode to expected text");
            if (vietnameseMap is not null)
            {
                var (installedHistogram, _) = ReadHistogram(sourceSb, vietnameseMap.Chunk);
                if (!installedHistogram.AsSpan().SequenceEqual(vietnameseMap.DecodedHistogram))
                    throw new InvalidDataException("Installed SB did not decode to expected Vietnamese histogram");
            }
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            File.Copy(previous, sourceSb, overwrite: true);
            throw;
        }
        File.Delete(previous);
        Console.WriteLine($"Installed translated SB into game; original backup: {backupSb}");
    }
}
