using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf3FontDump;

internal static class LooseFontInstaller
{
    private sealed record Item(string Source, string Target, string Backup, string SourceHash, string BackupHash);

    public static void Run(string game, string fontDirectory, bool restore)
    {
        if (Process.GetProcessesByName("bf3").Length != 0)
            throw new InvalidDataException("Close Battlefield 3 before changing loose GFX files");
        var stage = Path.GetFullPath(Path.Combine("build", "fonts"));
        var backupDirectory = Path.Combine(stage, "loose_backup");
        var manifestPath = Path.Combine(stage, "loose_test_manifest.json");
        if (restore)
        {
            Restore(manifestPath);
            return;
        }

        var dumpManifestPath = Path.Combine(fontDirectory, "manifest.json");
        if (!File.Exists(dumpManifestPath)) throw new FileNotFoundException("Font dump manifest missing", dumpManifestPath);
        using var dumpManifest = JsonDocument.Parse(File.ReadAllText(dumpManifestPath));
        if (!Path.GetFullPath(dumpManifest.RootElement.GetProperty("game").GetString()!)
            .Equals(game, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Font dump belongs to another game directory");

        Directory.CreateDirectory(backupDirectory);
        var items = new List<Item>();
        foreach (var asset in dumpManifest.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!asset.GetProperty("gfx").GetBoolean()) continue;
            var source = Path.GetFullPath(asset.GetProperty("output").GetString()!);
            if (!source.StartsWith(fontDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GFX source path escapes font directory");
            var target = Path.Combine(game, Path.GetFileName(source));
            var backup = Path.Combine(backupDirectory, Path.GetFileName(source));
            if (!File.Exists(target)) throw new FileNotFoundException("Expected existing loose GFX file", target);
            var data = File.ReadAllBytes(source);
            GfxInspector.Analyze(data);
            var sourceHash = CasCatalog.Sha256(data);
            if (sourceHash == asset.GetProperty("sha256").GetString())
                throw new InvalidDataException($"GFX has no edits relative to original: {source}");
            var currentHash = CasCatalog.Sha256(File.ReadAllBytes(target));
            if (!File.Exists(backup)) File.Copy(target, backup);
            var backupHash = CasCatalog.Sha256(File.ReadAllBytes(backup));
            if (currentHash != backupHash && currentHash != sourceHash)
                throw new InvalidDataException($"Existing loose GFX changed since backup: {target}");
            items.Add(new Item(source, target, backup, sourceHash, backupHash));
        }
        if (items.Count != 3) throw new InvalidDataException($"Expected exactly three edited GFX files, got {items.Count}");
        var changed = new List<Item>();
        try
        {
            foreach (var item in items)
            {
                var temp = item.Target + ".bf3vi.tmp";
                File.Copy(item.Source, temp, overwrite: true);
                File.Move(temp, item.Target, overwrite: true);
                changed.Add(item);
                if (CasCatalog.Sha256(File.ReadAllBytes(item.Target)) != item.SourceHash)
                    throw new InvalidDataException($"Loose GFX copy failed: {item.Target}");
            }
        }
        catch
        {
            foreach (var item in changed)
                File.Copy(item.Backup, item.Target, overwrite: true);
            throw;
        }
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            game,
            purpose = "Loose GFX path test; signed UI.sb/UI.toc/cas.cat unchanged",
            fonts = items.Select(item => new
            {
                source = item.Source,
                target = item.Target,
                backup = item.Backup,
                installed_sha256 = item.SourceHash,
                backup_sha256 = item.BackupHash
            }).ToList()
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"Installed {items.Count} loose GFX files only. No TOC, CAT or CAS changed.");
        Console.WriteLine($"Original loose files: {backupDirectory}");
        Console.WriteLine($"Restore: dotnet run --project Bf3FontDump -c Release -- --loose-restore");
    }

    private static void Restore(string manifestPath)
    {
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Loose test manifest missing", manifestPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var font in manifest.RootElement.GetProperty("fonts").EnumerateArray())
        {
            var target = font.GetProperty("target").GetString()!;
            var backup = font.GetProperty("backup").GetString()!;
            var installedHash = font.GetProperty("installed_sha256").GetString()!;
            var backupHash = font.GetProperty("backup_sha256").GetString()!;
            if (!File.Exists(backup) || CasCatalog.Sha256(File.ReadAllBytes(backup)) != backupHash)
                throw new InvalidDataException($"Loose GFX backup is missing or changed: {backup}");
            var currentHash = File.Exists(target) ? CasCatalog.Sha256(File.ReadAllBytes(target)) : null;
            if (currentHash != installedHash && currentHash != backupHash)
                throw new InvalidDataException($"Loose GFX target changed outside tool: {target}");
            File.Copy(backup, target, overwrite: true);
            if (CasCatalog.Sha256(File.ReadAllBytes(target)) != backupHash)
                throw new InvalidDataException($"Loose GFX restore failed: {target}");
        }
        Console.WriteLine("Restored three original loose GFX files. TOC, CAT and CAS were not touched.");
    }
}
