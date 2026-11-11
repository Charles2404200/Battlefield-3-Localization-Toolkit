using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf3FontDump;

internal static class FontImporter
{
    private sealed record FontAsset(string Name, string Bundle, string SourcePath, string OriginalSha1,
        string OriginalSha256, long OriginalLength, byte[] Data, byte[] Packed, byte[] NewSha1);

    public static void Run(string game, string fontDirectory, bool install)
    {
        if (install)
            throw new InvalidDataException("Font installation is disabled: the staged UI.toc/cas.cat changes trigger BF3's 'Your game data is corrupt' error. Use --import for offline analysis only.");
        var gamePatch = Path.Combine(game, "Update", "Patch", "Data");
        var gameUi = Path.Combine(gamePatch, "Win32", "UI.sb");
        var gameToc = Path.ChangeExtension(gameUi, ".toc");
        var gameCat = Path.Combine(gamePatch, "cas.cat");
        var stage = Path.GetFullPath(Path.Combine("build", "fonts"));
        var backup = Path.Combine(stage, "backup");
        var stageUi = Path.Combine(stage, "UI.sb");
        var stageToc = Path.Combine(stage, "UI.toc");
        var stageCat = Path.Combine(stage, "cas.cat");
        var manifestPath = Path.Combine(fontDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Font dump manifest missing", manifestPath);
        if (!File.Exists(gameUi) || !File.Exists(gameToc) || !File.Exists(gameCat))
            throw new FileNotFoundException("Patch UI.sb, UI.toc or cas.cat is missing");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestGame = Path.GetFullPath(manifest.RootElement.GetProperty("game").GetString()!);
        if (!Path.TrimEndingDirectorySeparator(manifestGame).Equals(Path.TrimEndingDirectorySeparator(game), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Font dump was created from a different game directory");
        var assets = new List<FontAsset>();
        foreach (var item in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!item.GetProperty("gfx").GetBoolean()) continue;
            var path = Path.GetFullPath(item.GetProperty("output").GetString()!);
            if (!path.StartsWith(fontDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Font source path escapes font directory");
            var data = File.ReadAllBytes(path);
            if (data.Length < 8 || !data.AsSpan().StartsWith("GFX"u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) != data.Length)
                throw new InvalidDataException($"Invalid edited GFX length header: {path}");
            var oldLength = item.GetProperty("decoded_size").GetInt64();
            var oldHash = item.GetProperty("sha256").GetString()!;
            var oldSha1 = item.GetProperty("sha1").GetString()!;
            var sourceEntry = new CasEntry(item.GetProperty("catalog").GetString()!,
                item.GetProperty("archive").GetString()!,
                item.GetProperty("archive_offset").GetUInt32(),
                item.GetProperty("packed_size").GetUInt32(), oldSha1);
            var originalData = CasCatalog.ReadPayload(sourceEntry, compressed: true, (ulong)oldLength);
            if (CasCatalog.Sha256(originalData) != oldHash)
                throw new InvalidDataException($"Original font differs from dump manifest: {path}");
            var originalStructure = GfxInspector.Analyze(originalData);
            var editedStructure = GfxInspector.Analyze(data);
            Console.WriteLine($"GFX structure {Path.GetFileName(path)}: original tags={originalStructure.TagCount}, edited tags={editedStructure.TagCount}, original font tags={originalStructure.FontTagCount}, edited font tags={editedStructure.FontTagCount}, original trailing={originalStructure.TrailingBytes}, edited trailing={editedStructure.TrailingBytes}");
            Console.WriteLine("  original tag lengths: " + string.Join(", ", originalStructure.Tags.Select(tag => $"{tag.Code}:{tag.Length}")));
            Console.WriteLine("  edited tag lengths:   " + string.Join(", ", editedStructure.Tags.Select(tag => $"{tag.Code}:{tag.Length}")));
            Console.WriteLine("  font headers original: " + string.Join(" | ", originalStructure.Tags.Where(tag => tag.Code == 75)
                .Select(tag => Convert.ToHexString(originalData.AsSpan(tag.PayloadOffset, Math.Min(24, tag.Length))))));
            Console.WriteLine("  font headers edited:   " + string.Join(" | ", editedStructure.Tags.Where(tag => tag.Code == 75)
                .Select(tag => Convert.ToHexString(data.AsSpan(tag.PayloadOffset, Math.Min(24, tag.Length))))));
            foreach (var font in editedStructure.Tags.Where(tag => tag.Code == 75).Select(tag => GfxInspector.AnalyzeFont(data, tag)))
                Console.WriteLine($"  DefineFont3 id={font.Id} glyphs={font.GlyphCount} codeOffset={font.CodeTableOffset} shapeBytes={font.ShapeBytes} duplicateCodes={font.DuplicateCodeCount}");
            var newHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (newHash == oldHash) continue;
            var packed = PackCas(data);
            assets.Add(new FontAsset(item.GetProperty("asset").GetString()!, item.GetProperty("bundle").GetString()!,
                path, oldSha1, oldHash, oldLength,
                data, packed, SHA1.HashData(packed)));
        }
        if (assets.Count == 0) throw new InvalidDataException("No edited GFX files differ from the dump manifest");
        if (assets.Count != assets.Select(asset => asset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Duplicate font asset names in manifest");

        var referenceUi = ResolveReference(gameUi, Path.Combine(backup, "UI.sb"), stageUi);
        var referenceToc = ResolveReference(gameToc, Path.Combine(backup, "UI.toc"), stageToc);
        var referenceCat = ResolveReference(gameCat, Path.Combine(backup, "cas.cat"), stageCat);
        var originalUi = File.ReadAllBytes(referenceUi);
        var editedUi = originalUi.ToArray();
        var bundles = FrostbiteDb.Read(referenceUi).Child("bundles")?.Children
            ?? throw new InvalidDataException("Patch UI.sb has no bundles list");
        var originalToc = File.ReadAllBytes(referenceToc);
        var editedTocDecoded = FrostbiteDb.DecodeSigned(originalToc);
        var toc = FrostbiteDb.Read(editedTocDecoded);
        var tocBundles = toc.Child("bundles")?.Children
            ?? throw new InvalidDataException("Patch UI.toc has no bundles list");
        VerifyTocBundleChecksums(tocBundles, originalUi);
        foreach (var asset in assets)
        {
            var tocBundle = tocBundles.SingleOrDefault(node =>
                string.Equals(node.Text("id"), asset.Bundle, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"TOC bundle missing: {asset.Bundle}");
            var tocOffset = checked((int)(tocBundle.Number("offset") ?? 0));
            var tocSize = checked((int)(tocBundle.Number("size") ?? 0));
            var bundle = bundles.SingleOrDefault(node => node.Text("path") == asset.Bundle)
                ?? throw new InvalidDataException($"Bundle missing: {asset.Bundle}");
            var record = bundle.Child("res")?.Children.SingleOrDefault(node => node.Text("name") == asset.Name)
                ?? throw new InvalidDataException($"RES entry missing: {asset.Name}");
            var shaNode = record.Child("sha1") ?? throw new InvalidDataException("RES SHA-1 missing");
            var sizeNode = record.Child("size") ?? throw new InvalidDataException("RES size missing");
            var originalSizeNode = record.Child("originalSize") ?? throw new InvalidDataException("RES originalSize missing");
            var oldSha = Convert.ToHexString(shaNode.Value as byte[] ?? []).ToLowerInvariant();
            if (oldSha != asset.OriginalSha1 ||
                record.Number("originalSize") != (ulong)asset.OriginalLength)
                throw new InvalidDataException($"RES metadata differs from the dump manifest: {asset.Name}");
            WriteFixedSha1(editedUi, shaNode, asset.NewSha1, asset.OriginalSha1);
            WriteFixedNumber(editedUi, sizeNode, checked((ulong)asset.Packed.Length));
            WriteFixedNumber(editedUi, originalSizeNode, checked((ulong)asset.Data.Length));
            var checksumNode = tocBundle.Child("checksum")
                ?? throw new InvalidDataException($"TOC checksum missing: {asset.Bundle}");
            if (checksumNode.Type != 16 || checksumNode.Value is not byte[] { Length: 20 })
                throw new InvalidDataException($"Unexpected TOC checksum field: {asset.Bundle}");
            SHA1.HashData(editedUi.AsSpan(tocOffset, tocSize))
                .CopyTo(editedTocDecoded, checksumNode.ValueOffset);
            Console.WriteLine($"{asset.Name}: GFX {asset.OriginalLength} -> {asset.Data.Length}, CAS {record.Number("size")} -> {asset.Packed.Length}; UI.sb RES at 0x{record.Offset:X}");
        }
        var editedToc = FrostbiteDb.EncodeSigned(originalToc, editedTocDecoded);
        VerifyTocBundleChecksums(FrostbiteDb.Read(editedTocDecoded).Child("bundles")!.Children, editedUi);

        Directory.CreateDirectory(stage);
        var archiveNumber = PickArchiveNumber(gamePatch, stage);
        var archiveName = $"cas_{archiveNumber:00}.cas";
        var stageArchive = Path.Combine(stage, archiveName);
        using var archive = new MemoryStream();
        var catalogRecords = new List<(FontAsset Asset, uint Offset)>();
        Span<byte> recordHeader = stackalloc byte[32];
        foreach (var asset in assets)
        {
            recordHeader.Clear();
            recordHeader[0] = 0xFA;
            recordHeader[1] = 0xCE;
            recordHeader[2] = 0x0F;
            recordHeader[3] = 0xF0;
            asset.NewSha1.CopyTo(recordHeader[4..24]);
            BinaryPrimitives.WriteUInt32LittleEndian(recordHeader[24..28], checked((uint)asset.Packed.Length));
            archive.Write(recordHeader);
            var offset = checked((uint)archive.Position);
            archive.Write(asset.Packed);
            catalogRecords.Add((asset, offset));
        }
        var archiveBytes = archive.ToArray();
        var originalCat = File.ReadAllBytes(referenceCat);
        var editedCat = AddCatalogRecords(originalCat, catalogRecords, archiveNumber);
        File.WriteAllBytes(stageUi, editedUi);
        File.WriteAllBytes(stageToc, editedToc);
        File.WriteAllBytes(stageCat, editedCat);
        File.WriteAllBytes(stageArchive, archiveBytes);

        var checkCatalog = new CasCatalog();
        checkCatalog.Add(stageCat);
        foreach (var asset in assets)
        {
            var found = checkCatalog.Find(asset.NewSha1);
            var decoded = CasCatalog.ReadPayload(found, compressed: true, (ulong)asset.Data.Length);
            if (!decoded.AsSpan().SequenceEqual(asset.Data))
                throw new InvalidDataException($"Staged CAS readback failed: {asset.Name}");
        }
        var checkedBundles = FrostbiteDb.Read(stageUi).Child("bundles")!.Children;
        foreach (var asset in assets)
        {
            var record = checkedBundles.Single(node => node.Text("path") == asset.Bundle)
                .Child("res")!.Children.Single(node => node.Text("name") == asset.Name);
            if (!record.Bytes("sha1")!.AsSpan().SequenceEqual(asset.NewSha1) ||
                record.Number("size") != (ulong)asset.Packed.Length ||
                record.Number("originalSize") != (ulong)asset.Data.Length)
                throw new InvalidDataException($"Staged UI.sb readback failed: {asset.Name}");
        }
        var report = new
        {
            game,
            source_ui = referenceUi,
            source_toc = referenceToc,
            source_cat = referenceCat,
            archive = archiveName,
            archive_number = archiveNumber,
            staged_ui = stageUi,
            staged_toc = stageToc,
            staged_cat = stageCat,
            staged_archive = stageArchive,
            ui_sha256 = CasCatalog.Sha256(editedUi),
            toc_sha256 = CasCatalog.Sha256(editedToc),
            catalog_sha256 = CasCatalog.Sha256(editedCat),
            installable = false,
            reason = "Retail BF3 rejects the modified signed UI.toc/cas.cat; offline structure checks alone are insufficient.",
            fonts = catalogRecords.Select(entry => new
            {
                asset = entry.Asset.Name,
                input = entry.Asset.SourcePath,
                old_gfx_size = entry.Asset.OriginalLength,
                new_gfx_size = entry.Asset.Data.Length,
                packed_size = entry.Asset.Packed.Length,
                cas_offset = entry.Offset,
                old_sha1 = entry.Asset.OriginalSha1,
                new_sha1 = Convert.ToHexString(entry.Asset.NewSha1).ToLowerInvariant()
            }).ToList()
        };
        var reportPath = Path.Combine(stage, "import_manifest.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"Staged {assets.Count} edited fonts in {stage}; CAS {archiveName} ({archiveBytes.Length} bytes). Original CAS files unchanged.");
    }

    private static string ResolveReference(string gamePath, string backupPath, string stagePath)
    {
        if (!File.Exists(backupPath)) return gamePath;
        var actual = CasCatalog.Sha256(File.ReadAllBytes(gamePath));
        var backup = CasCatalog.Sha256(File.ReadAllBytes(backupPath));
        var staged = File.Exists(stagePath) ? CasCatalog.Sha256(File.ReadAllBytes(stagePath)) : null;
        if (actual == backup || actual == staged) return backupPath;
        throw new InvalidDataException($"Installed file changed outside this tool: {gamePath}");
    }

    private static void VerifyTocBundleChecksums(IReadOnlyList<DbNode> bundles, byte[] ui)
    {
        foreach (var bundle in bundles)
        {
            var offset = checked((int)(bundle.Number("offset") ?? throw new InvalidDataException("TOC bundle offset missing")));
            var size = checked((int)(bundle.Number("size") ?? throw new InvalidDataException("TOC bundle size missing")));
            var expected = bundle.Bytes("checksum") ?? throw new InvalidDataException("TOC bundle checksum missing");
            if (offset < 0 || size < 0 || (long)offset + size > ui.Length || expected.Length != 20 ||
                !SHA1.HashData(ui.AsSpan(offset, size)).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"TOC checksum mismatch: {bundle.Text("id")}");
        }
    }

    private static int PickArchiveNumber(string gamePatch, string stage)
    {
        for (var index = 3; index < 100; index++)
        {
            var gamePath = Path.Combine(gamePatch, $"cas_{index:00}.cas");
            var oldStage = Path.Combine(stage, $"cas_{index:00}.cas");
            if (!File.Exists(gamePath)) return index;
            if (File.Exists(oldStage) && CasCatalog.Sha256(File.ReadAllBytes(gamePath)) ==
                CasCatalog.Sha256(File.ReadAllBytes(oldStage))) return index;
        }
        throw new InvalidDataException("No free patch CAS archive number");
    }

    private static void WriteFixedSha1(byte[] bytes, DbNode node, byte[] replacement, string expected)
    {
        if (node.Type != 16 || replacement.Length != 20 ||
            Convert.ToHexString(bytes.AsSpan(node.ValueOffset, 20)).ToLowerInvariant() != expected)
            throw new InvalidDataException($"Unsupported SHA-1 field layout at 0x{node.Offset:X}");
        replacement.CopyTo(bytes, node.ValueOffset);
    }

    private static void WriteFixedNumber(byte[] bytes, DbNode node, ulong value)
    {
        if (node.Type == 8) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(node.ValueOffset, 4), checked((uint)value));
        else if (node.Type == 9) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(node.ValueOffset, 8), value);
        else throw new InvalidDataException($"Unsupported variable-size number field type {node.Type} at 0x{node.Offset:X}");
    }

    private static byte[] PackCas(byte[] data)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (var at = 0; at < data.Length; at += 65520)
        {
            var length = Math.Min(65520, data.Length - at);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(data, at, length);
            var block = compressed.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)length);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)block.Length);
            output.Write(header);
            output.Write(block);
        }
        return output.ToArray();
    }

    private static byte[] AddCatalogRecords(byte[] source,
        List<(FontAsset Asset, uint Offset)> records, int archiveNumber)
    {
        if (source.Length < 572 || !source.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0xD1, 0xCE, 0x01 }))
            throw new InvalidDataException("Expected encrypted BF3 patch cas.cat");
        var decoded = new byte[source.Length - 556 + records.Count * 32];
        for (var i = 0; i < source.Length - 556; i++)
            decoded[i] = (byte)(source[i + 556] ^ source[296 + i % 257] ^ 0x7B);
        if (!decoded.AsSpan(0, 16).SequenceEqual("NyanNyanNyanNyan"u8) || (source.Length - 572) % 32 != 0)
            throw new InvalidDataException("Invalid decoded patch cas.cat");
        var at = source.Length - 556;
        foreach (var (asset, offset) in records)
        {
            asset.NewSha1.CopyTo(decoded, at);
            BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(at + 20, 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(at + 24, 4), checked((uint)asset.Packed.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(at + 28, 4), (uint)archiveNumber);
            at += 32;
        }
        var result = new byte[source.Length + records.Count * 32];
        source.AsSpan(0, 556).CopyTo(result);
        for (var i = 0; i < decoded.Length; i++)
            result[i + 556] = (byte)(decoded[i] ^ source[296 + i % 257] ^ 0x7B);
        return result;
    }

}
