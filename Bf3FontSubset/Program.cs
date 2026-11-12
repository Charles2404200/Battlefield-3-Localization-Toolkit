using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bf3FontDump;

namespace Bf3FontSubset;

public static class Program
{
private sealed record AsciiGlyph(byte[] Shape, byte[] Advance, byte[] Bounds);

private static void Main(string[] args) => Run(args);

public static void Run(string[] args)
{
string? sourceDirectory = null;
string? manifestArgument = null;
string? corpusArgument = null;
bool minimal = false;
bool extendedProbe = false;
bool byteRangeProbe = false;
bool highPunctuationProbe = false;
string? vietnameseMapPath = null;
string? asciiReferenceDirectory = null;
var output = Path.GetFullPath(Path.Combine("build", "fonts", "subset"));
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--source-dir" && i + 1 < args.Length) sourceDirectory = Path.GetFullPath(args[++i]);
    else if (args[i] == "--manifest" && i + 1 < args.Length) manifestArgument = Path.GetFullPath(args[++i]);
    else if (args[i] == "--corpus" && i + 1 < args.Length) corpusArgument = Path.GetFullPath(args[++i]);
    else if (args[i] == "--output-dir" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
    else if (args[i] == "--minimal") minimal = true;
    else if (args[i] == "--extended-probe") extendedProbe = true;
    else if (args[i] == "--byte-range-probe") byteRangeProbe = true;
    else if (args[i] == "--high-punctuation-probe") highPunctuationProbe = true;
    else if (args[i] == "--vietnamese-map" && i + 1 < args.Length) vietnameseMapPath = Path.GetFullPath(args[++i]);
    else if (args[i] == "--ascii-reference-dir" && i + 1 < args.Length) asciiReferenceDirectory = Path.GetFullPath(args[++i]);
    else throw new ArgumentException("Usage: Bf3FontSubset [--source-dir GFX_DIRECTORY] [--output-dir OUTPUT_DIRECTORY] [--ascii-reference-dir ORIGINAL_FONT_DUMP] [--minimal | --extended-probe | --byte-range-probe | --high-punctuation-probe | --vietnamese-map MAP_JSON]");
}
if (new[] { minimal, extendedProbe, byteRangeProbe, highPunctuationProbe, vietnameseMapPath is not null }.Count(value => value) > 1)
    throw new ArgumentException("Choose one font profile");
if (asciiReferenceDirectory is not null && vietnameseMapPath is null)
    throw new ArgumentException("--ascii-reference-dir requires --vietnamese-map");
if (extendedProbe || byteRangeProbe || highPunctuationProbe) minimal = true;
var fullMapRemap = new Dictionary<ushort, ushort>();
if (vietnameseMapPath is not null)
{
    using var mapDocument = JsonDocument.Parse(File.ReadAllText(vietnameseMapPath));
    var root = mapDocument.RootElement;
    if (root.GetProperty("format").GetString() != "BF3 Vietnamese character map v1" ||
        root.GetProperty("vietnamese_char_to_byte").EnumerateObject().Count() != 134)
        throw new InvalidDataException("Invalid BF3 Vietnamese map manifest");
    foreach (var item in root.GetProperty("low_glyph_remap").EnumerateObject())
    {
        var sourceCode = Convert.ToUInt16(item.Name[2..], 16);
        var targetCode = Convert.ToUInt16(item.Value.GetString()![2..], 16);
        if (!fullMapRemap.TryAdd(sourceCode, targetCode))
            throw new InvalidDataException($"Duplicate low glyph source U+{sourceCode:X4}");
    }
    if (fullMapRemap.Count != 19 || fullMapRemap.Values.Distinct().Count() != 19 ||
        fullMapRemap.Values.Any(value => value is 0 or 9 or 10 or 13 or >= 0x20))
        throw new InvalidDataException("Vietnamese low glyph remap needs 19 distinct safe control slots");
}
var manifestPath = manifestArgument ?? Path.GetFullPath(Path.Combine("dump", "fonts", "en", "manifest.json"));
using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
using var referenceManifest = asciiReferenceDirectory is null ? null :
    JsonDocument.Parse(File.ReadAllText(Path.Combine(asciiReferenceDirectory, "manifest.json")));
var referenceHashes = referenceManifest is null ? null :
    referenceManifest.RootElement.GetProperty("assets").EnumerateArray()
        .Where(asset => asset.GetProperty("gfx").GetBoolean())
        .ToDictionary(asset => asset.GetProperty("asset").GetString()!,
            asset => asset.GetProperty("sha256").GetString()!, StringComparer.OrdinalIgnoreCase);
var corpus = File.ReadAllText(corpusArgument ?? Path.Combine("translations", "us_loc_en_vi.csv"), Encoding.UTF8);
var byteRemap = new Dictionary<ushort, ushort>
{
    [0x1EBE] = 0x00F4,
    [0x1ECA] = 0x00F1,
    [0x1EBF] = 0x00F2,
    [0x1ECB] = 0x00F3
};
var rangeProbeRemap = new Dictionary<ushort, ushort>(byteRemap)
{
    [0x0102] = 0x0080, [0x0103] = 0x0081,
    [0x0110] = 0x0082, [0x0111] = 0x0083,
    [0x1EA0] = 0x0001, [0x1EA1] = 0x0002,
    [0x1EA2] = 0x0003, [0x1EA3] = 0x0004,
    [0x1EA4] = 0x0005, [0x1EA5] = 0x0006,
    [0x1EA6] = 0x0007, [0x1EA7] = 0x0008
};
var punctuationProbeRemap = new Dictionary<ushort, ushort>(byteRemap)
{
    [0x1EA0] = 0x2018, [0x1EA1] = 0x2019,
    [0x1EA2] = 0x201C, [0x1EA3] = 0x201D,
    [0x1EA4] = 0x2013, [0x1EA5] = 0x2014,
    [0x1EA6] = 0x2026, [0x1EA7] = 0x20AC
};
var required = new HashSet<ushort> { 0, 0x00A0, 0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026 };
for (ushort code = 0x20; code <= 0x7E; code++) required.Add(code);
if (!minimal) for (ushort code = 0x1EA0; code <= 0x1EF9; code++) required.Add(code);
if (!minimal)
    foreach (ushort code in new ushort[]
    {
        0x00C0, 0x00C1, 0x00C2, 0x00C3, 0x00C8, 0x00C9, 0x00CA, 0x00CC,
        0x00CD, 0x00D2, 0x00D3, 0x00D4, 0x00D5, 0x00D9, 0x00DA, 0x00DD,
        0x00E0, 0x00E1, 0x00E2, 0x00E3, 0x00E8, 0x00E9, 0x00EA, 0x00EC,
        0x00ED, 0x00F2, 0x00F3, 0x00F4, 0x00F5, 0x00F9, 0x00FA, 0x00FD,
        0x0102, 0x0103, 0x0110, 0x0111, 0x0128, 0x0129, 0x0168, 0x0169,
        0x01A0, 0x01A1, 0x01AF, 0x01B0
    }) required.Add(code);
if (!minimal) foreach (var ch in "ÀÁÂÃÈÉÊÌÍÒÓÔÕÙÚÝàáâãèéêìíòóôõùúýĂăĐđĨĩŨũƠơƯư") required.Add(ch);
if (!minimal) foreach (var ch in corpus) if (!char.IsControl(ch)) required.Add(ch);
var vietnameseGlyphs = VietnameseGlyphs();
if (!minimal) required.UnionWith(vietnameseGlyphs);

if (minimal) required.UnionWith(byteRemap.Keys);
if (byteRangeProbe) required.UnionWith(rangeProbeRemap.Keys);
if (highPunctuationProbe) required.UnionWith(punctuationProbeRemap.Keys);
if (extendedProbe)
    for (ushort code = 0x0100; code <= 0x011F; code++) required.Add(code);

Directory.CreateDirectory(output);
foreach (var asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
{
    if (!asset.GetProperty("gfx").GetBoolean()) continue;
    var source = asset.GetProperty("output").GetString()!;
    if (sourceDirectory is not null) source = Path.Combine(sourceDirectory, Path.GetFileName(source));
    var originalSize = asset.GetProperty("decoded_size").GetInt32();
    var packedLimit = asset.GetProperty("packed_size").GetInt32();
    var input = File.ReadAllBytes(source);
    byte[]? asciiReference = null;
    if (asciiReferenceDirectory is not null)
    {
        var assetName = asset.GetProperty("asset").GetString()!;
        var relative = assetName.Replace('/', Path.DirectorySeparatorChar) + ".gfx";
        var referencePath = Path.GetFullPath(Path.Combine(asciiReferenceDirectory, relative));
        if (!referencePath.StartsWith(asciiReferenceDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Original font path escapes reference directory");
        asciiReference = File.ReadAllBytes(referencePath);
        if (referenceHashes is null || !referenceHashes.TryGetValue(assetName, out var expectedHash) ||
            !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(asciiReference))
                .Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Original font reference differs from the game archive: {referencePath}");
    }
    if (!minimal)
        foreach (var tag in GfxInspector.Analyze(input).Tags.Where(tag => tag.Code == 75))
        {
            var codes = FontCodes(input, tag);
            if (codes.Count <= 100) continue;
            var missing = vietnameseGlyphs.Except(codes).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"{Path.GetFileName(source)} is missing Vietnamese glyphs: " +
                    string.Join(' ', missing.Select(code => $"U+{code:X4}")));
        }
    foreach (var profile in vietnameseMapPath is not null
        ? new[] { "vietnamese-metadata" }
        : highPunctuationProbe
            ? new[] { "high-punctuation-probe-metadata" }
        : byteRangeProbe
            ? new[] { "byte-range-probe-metadata" }
        : extendedProbe
            ? new[] { "extended-probe-metadata" }
            : sourceDirectory is null
            ? new[] { "corpus", "corpus-metadata", "byte-remap-metadata", "preserve-original" }
            : new[] { "byte-remap-metadata" })
    {
        HashSet<ushort> keep;
        if (profile == "preserve-original")
        {
            var basePath = Path.Combine("build", "fonts", "baseline_check",
                Path.GetRelativePath(Path.Combine("dump", "fonts", "en"), source));
            var baseline = File.ReadAllBytes(basePath);
            keep = new HashSet<ushort>(required);
            foreach (var tag in GfxInspector.Analyze(baseline).Tags.Where(tag => tag.Code == 75))
                keep.UnionWith(FontCodes(baseline, tag));
        }
        else keep = new HashSet<ushort>(required);
        if (profile is "byte-remap-metadata" or "extended-probe-metadata" or "byte-range-probe-metadata" or "high-punctuation-probe-metadata")
            keep.ExceptWith(new ushort[] { 0x00F1, 0x00F2, 0x00F3, 0x00F4 });
        if (profile == "byte-range-probe-metadata")
            keep.ExceptWith(rangeProbeRemap.Values);
        if (profile == "high-punctuation-probe-metadata")
            keep.ExceptWith(punctuationProbeRemap.Values);
        if (profile == "vietnamese-metadata")
            keep.ExceptWith(fullMapRemap.Values);

        var remap = profile == "vietnamese-metadata" ? fullMapRemap
            : profile == "high-punctuation-probe-metadata" ? punctuationProbeRemap
            : profile == "byte-range-probe-metadata" ? rangeProbeRemap
            : profile is "byte-remap-metadata" or "extended-probe-metadata" ? byteRemap : null;
        var subset = SubsetGfx(input, keep, remap,
            profile == "vietnamese-metadata" ? asciiReference : null, out var fontCounts);
        if (profile == "vietnamese-metadata")
            foreach (var tag in GfxInspector.Analyze(subset).Tags.Where(tag => tag.Code == 75))
            {
                var codes = FontCodes(subset, tag);
                if (codes.Count <= 100) continue;
                var missing = vietnameseGlyphs.Where(code =>
                    !codes.Contains(fullMapRemap.TryGetValue(code, out var lowCode) ? lowCode : code)).ToArray();
                if (missing.Length > 0)
                    throw new InvalidDataException($"Generated font is missing mapped Vietnamese glyphs: " +
                        string.Join(' ', missing.Select(code => $"U+{code:X4}")));
            }
        var padded = subset.Length <= originalSize ? PadGfx(subset, originalSize) : null;
        var packed = padded is null ? null : PackCas(padded);
        if (profile == "corpus" && packed is not null && packed.Length < packedLimit)
        {
            (padded, packed) = TunePadding(subset, originalSize, packedLimit);
            if (!UnpackCas(packed).AsSpan().SequenceEqual(padded))
                throw new InvalidDataException("Tuned CAS payload does not round trip");
        }
        if (profile is "corpus-metadata" or "byte-remap-metadata" or "extended-probe-metadata" or "byte-range-probe-metadata" or "high-punctuation-probe-metadata" or "vietnamese-metadata" && subset.Length < originalSize)
        {
            (padded, packed) = TuneMetadata(subset, originalSize, packedLimit);
            if (!UnpackCas(packed).AsSpan().SequenceEqual(padded))
                throw new InvalidDataException("Metadata CAS payload does not round trip");
        }
        var destination = Path.Combine(output, profile, Path.GetFileName(source));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, subset);
        Console.WriteLine($"{profile} {Path.GetFileName(source)}: glyphs={string.Join('/', fontCounts)}, GFX={input.Length}->{subset.Length}, original decoded={originalSize}, padded CAS={(packed is null ? "n/a" : packed.Length.ToString())}/{packedLimit}");
        if (padded is not null)
        {
            var paddedPath = Path.ChangeExtension(destination, ".padded.gfx");
            File.WriteAllBytes(paddedPath, padded);
            File.WriteAllBytes(Path.ChangeExtension(destination, ".cas-payload"), packed!);
        }
    }
}

static HashSet<ushort> VietnameseGlyphs()
{
    var result = new HashSet<ushort>();
    foreach (var vowel in "aăâeêioôơuưyAĂÂEÊIOÔƠUƯY")
        foreach (var mark in new[] { "", "\u0301", "\u0300", "\u0309", "\u0303", "\u0323" })
        {
            var composed = (vowel + mark).Normalize(NormalizationForm.FormC);
            if (composed.Length != 1)
                throw new InvalidDataException($"Could not compose Vietnamese character {vowel}+{mark}");
            result.Add(composed[0]);
        }
    result.Add('đ');
    result.Add('Đ');
    if (result.Count != 146) throw new InvalidDataException("Vietnamese alphabet must contain 146 letters");
    return result;
}

static HashSet<ushort> FontCodes(byte[] data, GfxTag tag)
{
    var span = data.AsSpan(tag.PayloadOffset, tag.Length);
    var countAt = 5 + span[4];
    var count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(countAt, 2));
    var offsetsAt = countAt + 2;
    var codeOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offsetsAt + count * 4, 4));
    var codeTableAt = offsetsAt + checked((int)codeOffset);
    var result = new HashSet<ushort>();
    for (int i = 0; i < count; i++)
        result.Add(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(codeTableAt + i * 2, 2)));
    return result;
}

static byte[] SubsetGfx(byte[] data, HashSet<ushort> keep, Dictionary<ushort, ushort>? remap,
    byte[]? asciiReference, out List<int> counts)
{
    var structure = GfxInspector.Analyze(data);
    var referenceFonts = asciiReference is null ? null : GfxInspector.Analyze(asciiReference).Tags
        .Where(tag => tag.Code == 75)
        .ToDictionary(tag => BinaryPrimitives.ReadUInt16LittleEndian(
            asciiReference.AsSpan(tag.PayloadOffset, 2)), tag => tag);
    int firstHeader = structure.Tags[0].PayloadOffset;
    // The first custom tag uses a short header in these files.
    firstHeader -= 2;
    using var output = new MemoryStream();
    output.Write(data, 0, firstHeader);
    counts = new List<int>();
    int priorEnd = firstHeader;
    foreach (var tag in structure.Tags)
    {
        var headerSize = tag.PayloadOffset - priorEnd;
        if (headerSize is not (2 or 6)) throw new InvalidDataException("Invalid GFX tag header span");
        byte[] payload;
        if (tag.Code == 75 && GfxInspector.AnalyzeFont(data, tag).GlyphCount > 100)
        {
            byte[]? referenceFont = null;
            if (referenceFonts is not null)
            {
                var fontId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(tag.PayloadOffset, 2));
                if (!referenceFonts.TryGetValue(fontId, out var referenceTag))
                    throw new InvalidDataException($"Original font {fontId} is missing");
                referenceFont = asciiReference!.AsSpan(referenceTag.PayloadOffset, referenceTag.Length).ToArray();
            }
            payload = SubsetFont(data.AsSpan(tag.PayloadOffset, tag.Length), keep, remap,
                referenceFont, out var count);
            counts.Add(count);
            WriteTagHeader(output, tag.Code, payload.Length);
            output.Write(payload);
        }
        else output.Write(data, priorEnd, headerSize + tag.Length);
        priorEnd = tag.PayloadOffset + tag.Length;
    }
    if (priorEnd != data.Length) output.Write(data, priorEnd, data.Length - priorEnd);
    var result = output.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)result.Length));
    var rebuilt = GfxInspector.Analyze(result);
    foreach (var fontTag in rebuilt.Tags.Where(tag => tag.Code == 75))
        GfxInspector.AnalyzeFont(result, fontTag);
    return result;
}

static byte[] SubsetFont(ReadOnlySpan<byte> span, HashSet<ushort> keep, Dictionary<ushort, ushort>? remap,
    byte[]? asciiReference, out int selectedCount)
{
    int countAt = 5 + span[4];
    int count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(countAt, 2));
    int offsetsAt = countAt + 2;
    int codeOffsetAt = offsetsAt + count * 4;
    int codeTableAt = offsetsAt + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(codeOffsetAt, 4)));
    int layoutAt = codeTableAt + count * 2;
    if ((span[2] & 0x80) == 0) throw new InvalidDataException("Font without layout is unsupported");
    if (layoutAt + 6 + count * 2 > span.Length) throw new InvalidDataException("Font layout truncated");
    int boundsAt = layoutAt + 6 + count * 2;
    var boundsStart = new int[count];
    var boundsLength = new int[count];
    int pos = boundsAt;
    for (int i = 0; i < count; i++)
    {
        boundsStart[i] = pos;
        int bits = span[pos] >> 3;
        int length = (5 + bits * 4 + 7) / 8;
        if (pos + length > span.Length) throw new InvalidDataException("Font RECT truncated");
        boundsLength[i] = length;
        pos += length;
    }
    int kerningAt = pos;
    if (kerningAt + 2 > span.Length) throw new InvalidDataException("Font kerning count truncated");
    int kerningCount = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(kerningAt, 2));
    int recordSize = (span[2] & 0x04) != 0 ? 6 : 4;
    if (kerningAt + 2 + kerningCount * recordSize != span.Length)
        throw new InvalidDataException("Font kerning table has unexpected length");

    var selected = new List<int>();
    var codes = new ushort[count];
    for (int i = 0; i < count; i++)
    {
        codes[i] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(codeTableAt + i * 2, 2));
        if (keep.Contains(codes[i])) selected.Add(i);
    }
    if (remap is not null)
    {
        if (remap.Keys.Any(code => !selected.Any(index => codes[index] == code)))
            throw new InvalidDataException("Vietnamese test glyph missing from source font");
        selected.Sort((a, b) =>
        {
            int byCode = Mapped(codes[a], remap).CompareTo(Mapped(codes[b], remap));
            return byCode != 0 ? byCode : a.CompareTo(b);
        });
    }
    selectedCount = selected.Count;
    if (selectedCount == 0) throw new InvalidDataException("Subset removed every glyph");

    var referenceGlyphs = asciiReference is null ? null : ReadAsciiGlyphs(asciiReference);
    var shapes = new List<byte[]>(selectedCount);
    var references = new List<AsciiGlyph?>(selectedCount);
    foreach (var index in selected)
    {
        int start = offsetsAt + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offsetsAt + index * 4, 4)));
        int end = index + 1 < count
            ? offsetsAt + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offsetsAt + (index + 1) * 4, 4)))
            : codeTableAt;
        if (end < start || end > codeTableAt) throw new InvalidDataException("Glyph shape span invalid");
        AsciiGlyph? reference = null;
        if (referenceGlyphs is not null && codes[index] is >= 0x21 and <= 0x7E &&
            !referenceGlyphs.TryGetValue(codes[index], out reference))
            throw new InvalidDataException($"Original ASCII glyph U+{codes[index]:X4} is missing");
        references.Add(reference);
        shapes.Add(reference?.Shape ?? span.Slice(start, end - start).ToArray());
    }

    using var output = new MemoryStream();
    output.Write(span.Slice(0, countAt));
    WriteU16(output, checked((ushort)selectedCount));
    uint shapeOffset = checked((uint)(selectedCount * 4 + 4));
    foreach (var shape in shapes)
    {
        WriteU32(output, shapeOffset);
        shapeOffset = checked(shapeOffset + (uint)shape.Length);
    }
    WriteU32(output, shapeOffset);
    foreach (var shape in shapes) output.Write(shape);
    foreach (var index in selected) WriteU16(output, Mapped(codes[index], remap));
    output.Write(span.Slice(layoutAt, 6));
    for (int i = 0; i < selected.Count; i++)
        output.Write(references[i]?.Advance ?? span.Slice(layoutAt + 6 + selected[i] * 2, 2).ToArray());
    for (int i = 0; i < selected.Count; i++)
        output.Write(references[i]?.Bounds ?? span.Slice(boundsStart[selected[i]], boundsLength[selected[i]]).ToArray());
    if (remap is null) output.Write(span.Slice(kerningAt));
    else
    {
        var kerning = span.Slice(kerningAt).ToArray();
        if (recordSize == 6)
            for (int i = 0; i < kerningCount; i++)
            {
                int at = 2 + i * recordSize;
                BinaryPrimitives.WriteUInt16LittleEndian(kerning.AsSpan(at, 2), Mapped(BinaryPrimitives.ReadUInt16LittleEndian(kerning.AsSpan(at, 2)), remap));
                BinaryPrimitives.WriteUInt16LittleEndian(kerning.AsSpan(at + 2, 2), Mapped(BinaryPrimitives.ReadUInt16LittleEndian(kerning.AsSpan(at + 2, 2)), remap));
            }
        output.Write(kerning);
    }
    return output.ToArray();
}

static Dictionary<ushort, AsciiGlyph> ReadAsciiGlyphs(ReadOnlySpan<byte> font)
{
    int countAt = 5 + font[4];
    int count = BinaryPrimitives.ReadUInt16LittleEndian(font.Slice(countAt, 2));
    int offsetsAt = countAt + 2;
    int codeTableAt = offsetsAt + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
        font.Slice(offsetsAt + count * 4, 4)));
    int layoutAt = codeTableAt + count * 2;
    if ((font[2] & 0x80) == 0 || layoutAt + 6 + count * 2 > font.Length)
        throw new InvalidDataException("Original font ASCII layout is invalid");
    int boundsAt = layoutAt + 6 + count * 2;
    var result = new Dictionary<ushort, AsciiGlyph>();
    for (int index = 0; index < count; index++)
    {
        int bits = font[boundsAt] >> 3;
        int boundsLength = (5 + bits * 4 + 7) / 8;
        if (boundsAt + boundsLength > font.Length)
            throw new InvalidDataException("Original ASCII glyph bounds are truncated");
        ushort code = BinaryPrimitives.ReadUInt16LittleEndian(font.Slice(codeTableAt + index * 2, 2));
        if (code is >= 0x21 and <= 0x7E)
        {
            int shapeStart = offsetsAt + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                font.Slice(offsetsAt + index * 4, 4)));
            int shapeEnd = index + 1 < count
                ? offsetsAt + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    font.Slice(offsetsAt + (index + 1) * 4, 4)))
                : codeTableAt;
            if (shapeEnd < shapeStart || shapeEnd > codeTableAt)
                throw new InvalidDataException("Original ASCII glyph shape is invalid");
            if (!result.TryAdd(code, new AsciiGlyph(
                    font.Slice(shapeStart, shapeEnd - shapeStart).ToArray(),
                    font.Slice(layoutAt + 6 + index * 2, 2).ToArray(),
                    font.Slice(boundsAt, boundsLength).ToArray())))
                throw new InvalidDataException($"Original font repeats ASCII glyph U+{code:X4}");
        }
        boundsAt += boundsLength;
    }
    return result;
}

static ushort Mapped(ushort code, Dictionary<ushort, ushort>? remap) =>
    remap is not null && remap.TryGetValue(code, out var target) ? target : code;

static void WriteTagHeader(Stream output, int code, int length)
{
    if (length < 63) WriteU16(output, checked((ushort)((code << 6) | length)));
    else
    {
        WriteU16(output, checked((ushort)((code << 6) | 63)));
        WriteU32(output, checked((uint)length));
    }
}

static byte[] PadGfx(byte[] data, int length)
{
    var result = new byte[length];
    data.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)length));
    GfxInspector.Analyze(result);
    return result;
}

static (byte[] Gfx, byte[] Packed) TunePadding(byte[] subset, int decodedLength, int targetPackedLength)
{
    int tail = decodedLength - subset.Length;
    if (tail < 1) throw new InvalidDataException("No GFX padding space available");
    for (int seed = 1; seed <= 128; seed++)
    {
        var random = new Random(seed * 7919);
        var noise = new byte[tail];
        random.NextBytes(noise);
        var padded = PadGfx(subset, decodedLength);
        byte[] Try(int noiseLength)
        {
            Array.Clear(padded, subset.Length, tail);
            noise.AsSpan(0, noiseLength).CopyTo(padded.AsSpan(subset.Length));
            return PackCas(padded);
        }
        int low = 0, high = tail;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var candidate = Try(middle);
            if (candidate.Length == targetPackedLength) return (padded, candidate);
            if (candidate.Length < targetPackedLength) low = middle + 1;
            else high = middle - 1;
        }
        for (int length = Math.Max(0, high - 32); length <= Math.Min(tail, low + 32); length++)
        {
            var candidate = Try(length);
            if (candidate.Length == targetPackedLength) return (padded, candidate);
        }
    }
    throw new InvalidDataException($"Unable to tune compressed payload to exactly {targetPackedLength} bytes");
}

static (byte[] Gfx, byte[] Packed) TuneMetadata(byte[] subset, int decodedLength, int targetPackedLength)
{
    int metadataBytes = decodedLength - subset.Length;
    int payloadBytes = metadataBytes - 6;
    int maxNoise = (payloadBytes - MetadataPrefix().Length - MetadataSuffix().Length) / 4 * 3;
    if (maxNoise < 1) throw new InvalidDataException("No room for GFX metadata padding");
    for (int seed = 1; seed <= 128; seed++)
    {
        var noise = new byte[maxNoise];
        new Random(seed * 7919).NextBytes(noise);
        (byte[] gfx, byte[] packed) Try(int count)
        {
            var gfx = BuildMetadataGfx(subset, decodedLength, noise.AsSpan(0, count));
            return (gfx, PackCas(gfx));
        }
        int low = 0, high = maxNoise;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var candidate = Try(middle);
            if (candidate.packed.Length == targetPackedLength) return candidate;
            if (candidate.packed.Length < targetPackedLength) low = middle + 1;
            else high = middle - 1;
        }
        for (int count = Math.Max(0, high - 16); count <= Math.Min(maxNoise, low + 16); count++)
        {
            var candidate = Try(count);
            if (candidate.packed.Length == targetPackedLength) return candidate;
        }
    }
    throw new InvalidDataException($"Unable to tune metadata payload to exactly {targetPackedLength} bytes");
}

static byte[] BuildMetadataGfx(byte[] subset, int decodedLength, ReadOnlySpan<byte> noise)
{
    var structure = GfxInspector.Analyze(subset);
    if (structure.TrailingBytes != 0) throw new InvalidDataException("GFX already has trailing data");
    var attributes = structure.Tags.Single(tag => tag.Code == 69);
    if (attributes.Length != 4) throw new InvalidDataException("Unexpected FileAttributes length");
    int insertAt = attributes.PayloadOffset + attributes.Length;
    int payloadLength = decodedLength - subset.Length - 6;
    var prefix = MetadataPrefix();
    var suffix = MetadataSuffix();
    var encodedNoise = Encoding.ASCII.GetBytes(Convert.ToBase64String(noise));
    int filler = payloadLength - prefix.Length - suffix.Length - encodedNoise.Length;
    if (filler < 0) throw new InvalidDataException("Metadata noise exceeds payload space");
    using var output = new MemoryStream(decodedLength);
    output.Write(subset, 0, insertAt);
    WriteTagHeader(output, 77, payloadLength);
    output.Write(prefix);
    output.Write(encodedNoise);
    for (int i = 0; i < filler; i++) output.WriteByte((byte)'A');
    output.Write(suffix);
    output.Write(subset, insertAt, subset.Length - insertAt);
    var result = output.ToArray();
    if (result.Length != decodedLength) throw new InvalidDataException("Metadata GFX size mismatch");
    result[attributes.PayloadOffset] |= 0x10; // HasMetadata in FileAttributes
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)result.Length));
    if (GfxInspector.Analyze(result).TrailingBytes != 0)
        throw new InvalidDataException("Metadata GFX has trailing data");
    return result;
}

static byte[] MetadataPrefix() => Encoding.ASCII.GetBytes(
    "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\"><pad xmlns=\"urn:bf3\">");

static byte[] MetadataSuffix() => Encoding.ASCII.GetBytes("</pad></rdf:Description></rdf:RDF>\0");

static byte[] UnpackCas(byte[] packed)
{
    using var result = new MemoryStream();
    int at = 0;
    while (at < packed.Length)
    {
        if (at + 8 > packed.Length) throw new InvalidDataException("Truncated CAS block header");
        int decodedLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(at, 4)));
        int compressedLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(at + 4, 4)));
        at += 8;
        if (compressedLength < 0 || at + compressedLength > packed.Length)
            throw new InvalidDataException("Truncated CAS zlib block");
        using var input = new MemoryStream(packed, at, compressedLength);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        long before = result.Length;
        zlib.CopyTo(result);
        if (result.Length - before != decodedLength) throw new InvalidDataException("Decoded block length mismatch");
        at += compressedLength;
    }
    return result.ToArray();
}

static byte[] PackCas(byte[] data)
{
    using var output = new MemoryStream();
    for (int at = 0; at < data.Length; at += 65536)
    {
        var block = data.AsSpan(at, Math.Min(65536, data.Length - at));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(block);
        WriteU32Big(output, checked((uint)block.Length));
        WriteU32Big(output, checked((uint)compressed.Length));
        compressed.WriteTo(output);
    }
    return output.ToArray();
}

static void WriteU16(Stream output, ushort value)
{
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    output.Write(buffer);
}

static void WriteU32(Stream output, uint value)
{
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    output.Write(buffer);
}

static void WriteU32Big(Stream output, uint value)
{
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
    output.Write(buffer);
}
}
}
