using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf3TextDump;

public static partial class Program
{
    private const string DefaultInput =
        @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 3\Update\Patch\Data\Win32\Loc\en.sb";
    private static readonly string DefaultOutput = Path.Combine("dump", "us_loc_en.tsv");
    private static readonly string DefaultSheet = Path.Combine("translations", "us_loc_en_vi.csv");

    private sealed record BlockInfo(long file_offset, int chunk_offset, int packed_size, int unpacked_size);
    private sealed record ChunkEntry(uint Hash, uint RelativeOffset, int AbsoluteOffset, string Source,
        string ReadableSource, int RawLength, bool HasEscapedBytes);
    private sealed record ChunkHeader(uint unknown_header_0, uint declared_body_size, uint entry_count,
        uint table_offset, uint last_record_offset, int text_base, int chunk_size);
    private sealed record Options(string Input, string Output, string Sheet, bool ValidateOnly,
        bool Import, bool Install, string StageDirectory, bool MapVietnamese);
    private sealed record SheetValidation(int Rows, int Translated, int Missing, int SourceDifferences,
        int UnquotedTranslationCommaRows, IReadOnlyList<string> SourceDifferenceIds,
        IReadOnlyDictionary<uint, string> Translations);

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var referenceInput = ResolveReferenceInput(options);
            var (chunk, blocks, prefixHash) = ReadLocalizationChunk(referenceInput);
            var (entries, header) = ParseChunk(chunk);
            var sheetPath = Path.GetFullPath(options.Sheet);
            if (options.Import)
            {
                var checkedSheet = ValidateSheet(sheetPath, entries);
                if (checkedSheet.SourceDifferences != 0)
                    throw new InvalidDataException($"Translation sheet has {checkedSheet.SourceDifferences} changed English sources: " +
                        string.Join(", ", checkedSheet.SourceDifferenceIds.Take(5)));
                if (checkedSheet.Translated == 0)
                    throw new InvalidDataException("Translation sheet has no filled Vietnamese cells");
                if (checkedSheet.UnquotedTranslationCommaRows != 0)
                    Console.WriteLine($"Recovered commas in the Vietnamese column of " +
                        $"{checkedSheet.UnquotedTranslationCommaRows} rows; CSV file was not changed.");
                ImportTranslations(options, referenceInput, chunk, blocks, entries, header,
                    checkedSheet.Translations);
                return 0;
            }
            if (options.ValidateOnly)
            {
                var checkedSheet = ValidateSheet(sheetPath, entries);
                Console.WriteLine($"Valid translation sheet: {checkedSheet.Rows} known IDs, " +
                    $"{checkedSheet.Translated} translated, {checkedSheet.Missing} missing, " +
                    $"{checkedSheet.SourceDifferences} source differences.");
                if (checkedSheet.UnquotedTranslationCommaRows != 0)
                    Console.WriteLine($"Recovered commas in the Vietnamese column of " +
                        $"{checkedSheet.UnquotedTranslationCommaRows} rows; CSV file was not changed.");
                if (checkedSheet.SourceDifferences != 0)
                    throw new InvalidDataException("Translation sheet has changed English sources: " +
                        string.Join(", ", checkedSheet.SourceDifferenceIds.Take(5)));
                return 0;
            }

            var outputPath = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            WriteTsv(outputPath, entries);
            var sheetCreated = false;
            if (!File.Exists(sheetPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sheetPath)!);
                WriteTranslationSheet(sheetPath, entries);
                var check = ValidateSheet(sheetPath, entries);
                if (check.Missing != 0 || check.SourceDifferences != 0)
                    throw new InvalidDataException("Generated translation CSV did not round-trip cleanly");
                sheetCreated = true;
            }

            var metadataPath = Path.ChangeExtension(outputPath, ".meta.json");
            var metadata = new
            {
                input = Path.GetFullPath(options.Input),
                output = outputPath,
                translation_sheet = sheetPath,
                translatable_entries = entries.Count(entry => entry.RawLength > 0),
                chunk_sha256 = HashHex(SHA256.HashData(chunk)),
                compressed_prefix_sha256 = prefixHash,
                format = "BF3 us_loc binary chunk; little-endian hash/offset pairs; NUL strings",
                source_text_encoding = @"ASCII with non-ASCII/control bytes escaped as \xNN",
                header,
                compressed_blocks = blocks,
                empty_entries = entries.Count(entry => entry.RawLength == 0),
                escaped_entries = entries.Count(entry => entry.HasEscapedBytes)
            };
            File.WriteAllText(metadataPath,
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));

            Console.WriteLine($"Dumped {entries.Count} entries to {outputPath}");
            Console.WriteLine($"Chunk size: {chunk.Length} bytes; text base: 0x{header.text_base:X}");
            Console.WriteLine(sheetCreated ? $"Created translation sheet: {sheetPath}" :
                $"Kept existing translation sheet: {sheetPath}");
            Console.WriteLine($"Metadata: {metadataPath}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static int Run(string[] args) => Main(args);

    private static Options ParseArguments(string[] args)
    {
        var input = DefaultInput;
        var output = DefaultOutput;
        var sheet = DefaultSheet;
        var validateOnly = false;
        var import = false;
        var install = false;
        var mapVietnamese = false;
        var stageDirectory = Path.Combine("build", "Loc");
        var inputSet = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "-h" or "--help")
            {
                Console.WriteLine("Usage: dotnet run --project Bf3TextDump -- [INPUT_EN_SB_OR_CHUNK] [-o RAW_TSV] [-s TRANSLATION_CSV] [--validate | --import [--install]] [--map-vietnamese]");
                Console.WriteLine("Defaults to the installed BF3 patch, dump/us_loc_en.tsv and translations/us_loc_en_vi.csv.");
                Console.WriteLine("The translation CSV is created once and is never overwritten by a normal dump.");
                Environment.Exit(0);
            }
            if (args[index] is "-o" or "--output")
            {
                if (++index >= args.Length) throw new ArgumentException("Missing path after -o/--output");
                output = args[index];
            }
            else if (args[index] is "-s" or "--sheet")
            {
                if (++index >= args.Length) throw new ArgumentException("Missing path after -s/--sheet");
                sheet = args[index];
            }
            else if (args[index] == "--validate")
            {
                validateOnly = true;
            }
            else if (args[index] == "--import")
            {
                import = true;
            }
            else if (args[index] == "--install")
            {
                import = true;
                install = true;
            }
            else if (args[index] == "--map-vietnamese")
            {
                mapVietnamese = true;
            }
            else if (args[index] is "--stage")
            {
                if (++index >= args.Length) throw new ArgumentException("Missing path after --stage");
                stageDirectory = args[index];
            }
            else if (args[index].StartsWith('-'))
            {
                throw new ArgumentException($"Unknown option: {args[index]}");
            }
            else if (!inputSet)
            {
                input = args[index];
                inputSet = true;
            }
            else
            {
                throw new ArgumentException("Only one input file may be specified");
            }
        }
        if (validateOnly && import) throw new ArgumentException("--validate and --import are separate commands");
        return new Options(input, output, sheet, validateOnly, import, install, stageDirectory, mapVietnamese);
    }

    private static (byte[] Chunk, List<BlockInfo> Blocks, string? PrefixHash) ReadLocalizationChunk(string input)
    {
        if (!Path.GetExtension(input).Equals(".sb", StringComparison.OrdinalIgnoreCase))
            return (File.ReadAllBytes(input), [], null);

        var blocks = new List<BlockInfo>();
        using var source = File.OpenRead(input);
        using var decoded = new MemoryStream();
        using var prefixHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long? targetSize = null;
        Span<byte> blockHeader = stackalloc byte[8];

        while (targetSize is null || decoded.Length < targetSize)
        {
            var fileOffset = source.Position;
            source.ReadExactly(blockHeader);
            var unpackedSize = BinaryPrimitives.ReadUInt32BigEndian(blockHeader[..4]);
            var packedSize = BinaryPrimitives.ReadUInt32BigEndian(blockHeader[4..]);
            if (unpackedSize is 0 or > 65536 || packedSize is 0 or > 65536)
                throw new InvalidDataException($"Invalid compressed block at file offset 0x{fileOffset:X}; use the patched Loc/en.sb or an extracted chunk");

            var packed = new byte[(int)packedSize];
            source.ReadExactly(packed);
            prefixHasher.AppendData(blockHeader);
            prefixHasher.AppendData(packed);
            var chunkOffset = checked((int)decoded.Length);
            using (var packedStream = new MemoryStream(packed, writable: false))
            using (var zlib = new ZLibStream(packedStream, CompressionMode.Decompress))
                zlib.CopyTo(decoded);
            if (decoded.Length - chunkOffset != unpackedSize)
                throw new InvalidDataException($"Wrong decoded size at 0x{fileOffset:X}");
            blocks.Add(new BlockInfo(fileOffset, chunkOffset, (int)packedSize, (int)unpackedSize));

            if (targetSize is null)
            {
                if (decoded.Length < 20) throw new InvalidDataException("First block is too short for localization header");
                targetSize = BinaryPrimitives.ReadUInt32LittleEndian(decoded.GetBuffer().AsSpan(4, 4)) + 8L;
                if (targetSize is < 28 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("First SB payload is not a plausible us_loc chunk");
            }
            if (decoded.Length > targetSize)
                throw new InvalidDataException("Compressed blocks exceed chunk length");
        }
        return (decoded.ToArray(), blocks, HashHex(prefixHasher.GetHashAndReset()));
    }

    private static (List<ChunkEntry> Entries, ChunkHeader Header) ParseChunk(byte[] chunk,
        bool allowSharedOffsets = false)
    {
        if (chunk.Length < 36) throw new InvalidDataException("Localization chunk is too short");
        uint Read32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset, 4));

        var unknown = Read32(0);
        var bodySize = Read32(4);
        var count = Read32(8);
        var tableOffset = Read32(12);
        var lastRecordOffset = Read32(16);
        var textBase = (long)lastRecordOffset + 8;
        if ((long)bodySize + 8 != chunk.Length)
            throw new InvalidDataException("Header length does not match decoded chunk");
        if (tableOffset < 20 || (long)tableOffset + 8L * (count + 1L) != textBase)
            throw new InvalidDataException("Hash table boundaries do not match text start");
        if (textBase >= chunk.Length)
            throw new InvalidDataException("Text start is outside the chunk");
        if (Read32((int)tableOffset) != 0 || Read32((int)tableOffset + 4) != 0)
            throw new InvalidDataException("Expected a zero sentinel at the start of the hash table");

        var entries = new List<ChunkEntry>((int)count);
        var seenOffsets = new HashSet<uint>();
        uint previousHash = 0;
        for (var index = 1; index <= count; index++)
        {
            var recordAt = (int)(tableOffset + 8L * index);
            var hash = Read32(recordAt);
            var relativeOffset = Read32(recordAt + 4);
            var absoluteOffset = textBase + relativeOffset;
            if (hash <= previousHash)
                throw new InvalidDataException($"Hash order is invalid at entry {index}");
            if (!seenOffsets.Add(relativeOffset) && !allowSharedOffsets)
                throw new InvalidDataException($"Duplicate text offset at entry {index}");
            if (absoluteOffset >= chunk.Length)
                throw new InvalidDataException($"Text offset outside chunk at entry {index}");
            var start = (int)absoluteOffset;
            if (chunk[start - 1] != 0)
                throw new InvalidDataException($"Text offset is not at a string boundary: {index}");
            var end = Array.IndexOf(chunk, (byte)0, start);
            if (end < 0)
                throw new InvalidDataException($"Missing NUL terminator for entry {index}");
            var raw = chunk.AsSpan(start, end - start);
            var escaped = false;
            foreach (var value in raw)
                if (value < 32 || value > 126) { escaped = true; break; }
            entries.Add(new ChunkEntry(hash, relativeOffset, start, EscapeSource(raw),
                ReadableSource(raw), raw.Length, escaped));
            previousHash = hash;
        }

        return (entries, new ChunkHeader(unknown, bodySize, count, tableOffset,
            lastRecordOffset, (int)textBase, chunk.Length));
    }

    private static string EscapeSource(ReadOnlySpan<byte> raw)
    {
        var result = new StringBuilder(raw.Length);
        foreach (var value in raw)
        {
            switch (value)
            {
                case (byte)'\\': result.Append(@"\\"); break;
                case (byte)'\n': result.Append(@"\n"); break;
                case (byte)'\r': result.Append(@"\r"); break;
                case (byte)'\t': result.Append(@"\t"); break;
                default:
                    if (value is >= 32 and <= 126) result.Append((char)value);
                    else result.Append("\\x").Append(value.ToString("X2"));
                    break;
            }
        }
        return result.ToString();
    }

    private static string ReadableSource(ReadOnlySpan<byte> raw)
    {
        var result = new StringBuilder(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var value = raw[index];
            if (value >= 128)
            {
                var status = Rune.DecodeFromUtf8(raw[index..], out var rune, out var consumed);
                if (status == OperationStatus.Done && consumed > 1)
                {
                    result.Append(rune.ToString());
                    index += consumed - 1;
                }
                else if (value == 0xA0)
                    result.Append(' ');
                else if (value >= 0xA1)
                    result.Append((char)value);
                else
                    result.Append("\\x").Append(value.ToString("X2"));
                continue;
            }
            switch (value)
            {
                case (byte)'\\': result.Append(@"\\"); break;
                case (byte)'\n': result.Append('\n'); break;
                case (byte)'\r': result.Append('\r'); break;
                case (byte)'\t': result.Append('\t'); break;
                default:
                    if (value >= 32) result.Append((char)value);
                    else result.Append("\\x").Append(value.ToString("X2"));
                    break;
            }
        }
        return result.ToString();
    }

    private static void WriteTranslationSheet(string path, List<ChunkEntry> entries)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));
        writer.NewLine = "\n";
        writer.WriteLine("id_hash,english,vietnamese");
        foreach (var entry in entries.Where(entry => entry.RawLength > 0).OrderBy(entry => entry.AbsoluteOffset))
        {
            WriteCsvCell(writer, $"0x{entry.Hash:X8}");
            writer.Write(',');
            WriteCsvCell(writer, entry.ReadableSource);
            writer.Write(',');
            writer.WriteLine();
        }
    }

    private static void WriteCsvCell(TextWriter writer, string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\r') && !value.Contains('\n'))
        {
            writer.Write(value);
            return;
        }
        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\""));
        writer.Write('"');
    }

    private static SheetValidation ValidateSheet(string path, List<ChunkEntry> entries)
    {
        var expected = entries.Where(entry => entry.RawLength > 0)
            .ToDictionary(entry => $"0x{entry.Hash:X8}", entry => entry.ReadableSource);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var translations = new Dictionary<uint, string>();
        var translated = 0;
        var sourceDifferences = 0;
        var sourceDifferenceIds = new List<string>();
        var unquotedTranslationCommaRows = 0;
        var rows = ReadCsvRows(path);
        if (rows.Count == 0 || rows[0] is not ["id_hash", "english", "vietnamese"])
            throw new InvalidDataException("Translation CSV header must be id_hash,english,vietnamese");
        for (var row = 1; row < rows.Count; row++)
        {
            var fields = rows[row];
            if (fields.Length < 3)
                throw new InvalidDataException($"Translation CSV row {row + 1} has {fields.Length} columns; expected at least 3");
            var id = fields[0];
            if (!expected.TryGetValue(id, out var english))
                throw new InvalidDataException($"Unknown translation ID: {id}");
            if (!seen.Add(id))
                throw new InvalidDataException($"Duplicate translation ID: {id}");
            var sourceMatches = NormalizeSourceForComparison(fields[1]) ==
                NormalizeSourceForComparison(english);
            if (!sourceMatches)
            {
                sourceDifferences++;
                sourceDifferenceIds.Add($"{id} (row {row + 1})");
            }
            if (fields.Length > 3 && !sourceMatches)
                throw new InvalidDataException($"Translation CSV row {row + 1} ({id}) has extra columns and changed English text; cannot safely recover the Vietnamese column");
            var vietnamese = fields.Length == 3 ? fields[2] : string.Join(',', fields.Skip(2));
            if (fields.Length > 3) unquotedTranslationCommaRows++;
            if (!string.IsNullOrWhiteSpace(vietnamese))
            {
                translated++;
                translations.Add(Convert.ToUInt32(id[2..], 16), vietnamese);
            }
        }
        return new SheetValidation(seen.Count, translated, expected.Count - seen.Count,
            sourceDifferences, unquotedTranslationCommaRows, sourceDifferenceIds, translations);
    }

    private static string NormalizeSourceForComparison(string value) => value
        .Replace("\\n", " ").Replace("\\r", " ")
        .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
        .Trim(' ', '\t');

    private static List<string[]> ReadCsvRows(string path)
    {
        var content = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var start = content.Length > 0 && content[0] == '\uFEFF' ? 1 : 0;
        for (var index = start; index < content.Length; index++)
        {
            var ch = content[index];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        closedQuote = true;
                    }
                }
                else field.Append(ch);
                continue;
            }
            if (ch == ',' || ch == '\r' || ch == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                closedQuote = false;
                if (ch != ',')
                {
                    rows.Add(fields.ToArray());
                    fields.Clear();
                    if (ch == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                }
            }
            else if (ch == '"' && field.Length == 0 && !closedQuote)
                quoted = true;
            else if (closedQuote || ch == '"')
                throw new InvalidDataException($"Invalid CSV quoting near character {index}");
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("Translation CSV has an unclosed quoted field");
        if (field.Length > 0 || fields.Count > 0 || closedQuote)
        {
            fields.Add(field.ToString());
            rows.Add(fields.ToArray());
        }
        return rows;
    }

    private static void WriteTsv(string output, List<ChunkEntry> entries)
    {
        using var writer = new StreamWriter(output, append: false, new UTF8Encoding(true));
        writer.NewLine = "\n";
        writer.WriteLine("id_hash\ttext_offset\tchunk_offset\tenglish\tvietnamese");
        foreach (var entry in entries)
        {
            writer.Write("0x"); writer.Write(entry.Hash.ToString("X8")); writer.Write('\t');
            writer.Write("0x"); writer.Write(entry.RelativeOffset.ToString("X")); writer.Write('\t');
            writer.Write("0x"); writer.Write(entry.AbsoluteOffset.ToString("X")); writer.Write('\t');
            WriteTsvCell(writer, entry.Source);
            writer.Write('\t');
            writer.WriteLine();
        }
    }

    private static void WriteTsvCell(TextWriter writer, string value)
    {
        if (!value.Contains('\"') && !value.Contains('\t') && !value.Contains('\r') && !value.Contains('\n'))
        {
            writer.Write(value);
            return;
        }
        writer.Write('\"');
        writer.Write(value.Replace("\"", "\"\""));
        writer.Write('\"');
    }

    private static string HashHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
