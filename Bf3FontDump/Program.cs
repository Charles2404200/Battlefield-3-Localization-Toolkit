using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf3FontDump;

public static class Program
{
    private const string DefaultGame = @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 3";
    private static readonly string DefaultOutput = Path.Combine("dump", "fonts", "en");

    private static int Main(string[] args)
    {
        try
        {
            var (game, output, import, install, looseTest, looseRestore) = ParseArguments(args);
            game = Path.GetFullPath(game);
            output = Path.GetFullPath(output);
            if (looseTest || looseRestore)
            {
                LooseFontInstaller.Run(game, output, looseRestore);
                return 0;
            }
            var baseData = Path.Combine(game, "Data");
            var patchData = Path.Combine(game, "Update", "Patch", "Data");
            if (import)
            {
                FontImporter.Run(game, output, install);
                return 0;
            }
            var patchedUi = Path.Combine(patchData, "Win32", "UI.sb");
            var baseUi = Path.Combine(baseData, "Win32", "UI.sb");
            var uiPath = File.Exists(patchedUi) ? patchedUi : baseUi;
            if (!File.Exists(uiPath)) throw new FileNotFoundException("UI.sb was not found", uiPath);

            var catalog = new CasCatalog();
            catalog.Add(Path.Combine(baseData, "cas.cat"));
            if (File.Exists(Path.Combine(patchData, "cas.cat")))
                catalog.Add(Path.Combine(patchData, "cas.cat"));

            var ui = FrostbiteDb.Read(uiPath);
            var bundles = ui.Child("bundles")?.Children
                ?? throw new InvalidDataException("UI.sb has no bundles list");
            var selected = bundles.Where(bundle =>
            {
                var path = bundle.Text("path");
                return path is not null &&
                    path.Contains("fontcollection", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith("_en_fontlib_bundle", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (selected.Count == 0)
                throw new InvalidDataException("No English font bundles found in UI.sb");

            Directory.CreateDirectory(output);
            var items = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bundle in selected)
            {
                var bundleName = bundle.Text("path")!;
                foreach (var kind in new[] { "ebx", "res" })
                {
                    foreach (var asset in bundle.Child(kind)?.Children ?? [])
                    {
                        var assetName = asset.Text("name");
                        if (assetName is null ||
                            !assetName.Contains("fontcollection_en_", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!assetName.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ||
                            assetName.Split('/').Any(part => part is ".." or "."))
                            throw new InvalidDataException($"Unsafe asset path in UI.sb: {assetName}");
                        if (!seen.Add(kind + ":" + assetName)) continue;

                        var sha1 = asset.Bytes("sha1") ?? throw new InvalidDataException($"Missing SHA-1: {assetName}");
                        var originalSize = asset.Number("originalSize") ?? throw new InvalidDataException($"Missing originalSize: {assetName}");
                        var packedSize = asset.Number("size") ?? throw new InvalidDataException($"Missing size: {assetName}");
                        var catalogEntry = catalog.Find(sha1);
                        if (catalogEntry.Size != packedSize)
                            throw new InvalidDataException($"Catalog/bundle size mismatch: {assetName}");
                        var payload = CasCatalog.ReadPayload(catalogEntry, kind == "res" || packedSize != originalSize, originalSize);
                        var isGfx = payload.AsSpan().StartsWith("GFX"u8);
                        if (isGfx && (payload.Length < 8 ||
                            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)) != payload.Length))
                            throw new InvalidDataException($"Invalid GFX length header: {assetName}");
                        var extension = isGfx ? ".gfx" : kind == "ebx" ? ".ebx" : ".res";
                        var relative = assetName.Replace('/', Path.DirectorySeparatorChar) + extension;
                        var destination = Path.GetFullPath(Path.Combine(output, relative));
                        if (!destination.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"Asset output escapes destination: {assetName}");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (File.Exists(destination))
                        {
                            if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(payload))
                                Console.WriteLine($"Keeping existing edited file: {destination}");
                        }
                        else File.WriteAllBytes(destination, payload);

                        bool? matchesLooseGfx = null;
                        if (isGfx)
                        {
                            var loosePath = Path.Combine(game, Path.GetFileName(destination));
                            if (File.Exists(loosePath))
                                matchesLooseGfx = SHA256.HashData(File.ReadAllBytes(loosePath))
                                    .AsSpan().SequenceEqual(SHA256.HashData(payload));
                        }
                        items.Add(new
                        {
                            bundle = bundleName,
                            asset = assetName,
                            kind,
                            bundle_entry_offset = asset.Offset,
                            sha1 = Convert.ToHexString(sha1).ToLowerInvariant(),
                            catalog = catalogEntry.CatalogPath,
                            archive = catalogEntry.ArchivePath,
                            archive_offset = catalogEntry.Offset,
                            packed_size = catalogEntry.Size,
                            decoded_size = payload.Length,
                            output = destination,
                            sha256 = CasCatalog.Sha256(payload),
                            gfx = isGfx,
                            matches_loose_gfx = matchesLooseGfx
                        });
                        Console.WriteLine($"{kind.ToUpperInvariant()} {assetName} -> {relative} ({payload.Length} bytes)");
                    }
                }
            }
            if (items.Count == 0) throw new InvalidDataException("English font bundles contained no matching assets");
            var manifest = new
            {
                game,
                ui_superbundle = uiPath,
                bundle_count = selected.Count,
                asset_count = items.Count,
                assets = items
            };
            var manifestPath = Path.Combine(output, "manifest.json");
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            Console.WriteLine($"Extracted {items.Count} assets from {selected.Count} bundles. Manifest: {manifestPath}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    public static int Run(string[] args) => Main(args);

    private static (string Game, string Output, bool Import, bool Install, bool LooseTest, bool LooseRestore) ParseArguments(string[] args)
    {
        var game = DefaultGame;
        var output = DefaultOutput;
        var gameSet = false;
        var import = false;
        var install = false;
        var looseTest = false;
        var looseRestore = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is "-h" or "--help")
            {
                Console.WriteLine("Usage: dotnet run --project Bf3FontDump -- [GAME_DIRECTORY] [-o FONT_DIRECTORY] [--import]");
                Console.WriteLine("       dotnet run --project Bf3FontDump -- [GAME_DIRECTORY] [-o FONT_DIRECTORY] --loose-test|--loose-restore");
                Console.WriteLine("Font --install is disabled because retail BF3 rejects the modified TOC/catalog.");
                Environment.Exit(0);
            }
            if (args[index] == "--import") import = true;
            else if (args[index] == "--install") install = true;
            else if (args[index] == "--loose-test") looseTest = true;
            else if (args[index] == "--loose-restore") looseRestore = true;
            else if (args[index] is "-o" or "--output")
            {
                if (++index >= args.Length) throw new ArgumentException("Missing path after -o/--output");
                output = args[index];
            }
            else if (args[index].StartsWith('-'))
                throw new ArgumentException($"Unknown option: {args[index]}");
            else if (!gameSet)
            {
                game = args[index];
                gameSet = true;
            }
            else
                throw new ArgumentException("Only one game directory may be specified");
        }
        if (install && !import) throw new ArgumentException("--install requires --import");
        if ((looseTest || looseRestore) && (import || install || looseTest && looseRestore))
            throw new ArgumentException("Choose one operation: --import, --loose-test, or --loose-restore");
        return (game, output, import, install, looseTest, looseRestore);
    }
}
