using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bf3VietHoaGui;

internal static class InstallationService
{
    private static readonly IReadOnlyDictionary<string, long> FontSizes = new Dictionary<string, long>
    {
        ["fontcollection_en_fontlib.cas-payload"] = 62403,
        ["eulafontcollection_en_fontlib.cas-payload"] = 28727,
        ["storefontcollection_en_fontlib.cas-payload"] = 28845
    };

    private sealed record BackupManifest(string GameDirectory, Dictionary<string, bool> ExistingFiles);

    public static string Install(string gameDirectory, string stageDirectory, string fontOutputDirectory,
        string hookDll, string backupRoot)
    {
        EnsureGameClosed();
        var game = Path.GetFullPath(gameDirectory);
        var gameLoc = Path.Combine(game, "Update", "Patch", "Data", "Win32", "Loc");
        var gameFonts = Path.Combine(game, "fonts");
        var stagedSb = Path.Combine(stageDirectory, "en.sb");
        var stagedToc = Path.Combine(stageDirectory, "en.toc");
        var stagedMap = Path.Combine(stageDirectory, "vietnamese_map.json");
        var stagedFonts = Path.Combine(fontOutputDirectory, "vietnamese-metadata");
        var installedSb = Path.Combine(gameLoc, "en.sb");
        var installedToc = Path.Combine(gameLoc, "en.toc");
        var installedDll = Path.Combine(game, "dinput8.dll");

        foreach (var path in new[] { installedSb, installedToc, stagedSb, stagedToc, stagedMap, hookDll })
            if (!File.Exists(path)) throw new FileNotFoundException("Thiếu file cần để cài bản dịch", path);
        if (new FileInfo(stagedSb).Length != new FileInfo(installedSb).Length)
            throw new InvalidDataException("en.sb đã dựng không bằng kích thước bản game");
        if (Hash(stagedToc) != Hash(installedToc))
            throw new InvalidDataException("en.toc của game khác với bản đã dựng; dừng để tránh corrupt");
        if (!IsX86Dll(hookDll)) throw new InvalidDataException("Hook dinput8.dll không phải DLL x86");
        if (File.Exists(installedDll) && Hash(installedDll) != Hash(hookDll))
            throw new InvalidDataException("Game đã có dinput8.dll khác. Hãy kiểm tra mod hiện có trước khi cài hook font.");
        foreach (var (name, size) in FontSizes)
        {
            var payload = Path.Combine(stagedFonts, name);
            if (!File.Exists(payload) || new FileInfo(payload).Length != size)
                throw new InvalidDataException($"Payload font thiếu hoặc sai kích thước: {payload}");
        }

        Directory.CreateDirectory(gameFonts);
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(backup);
        var targets = new Dictionary<string, string>
        {
            ["en.sb"] = installedSb,
            ["dinput8.dll"] = installedDll
        };
        foreach (var name in FontSizes.Keys) targets.Add(name, Path.Combine(gameFonts, name));
        var existing = new Dictionary<string, bool>();
        foreach (var (name, target) in targets)
        {
            existing[name] = File.Exists(target);
            if (existing[name]) File.Copy(target, Path.Combine(backup, name));
        }
        File.WriteAllText(Path.Combine(backup, "backup_manifest.json"),
            JsonSerializer.Serialize(new BackupManifest(game, existing),
                new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            EnsureGameClosed();
            if (!File.Exists(installedDll)) AtomicCopy(hookDll, installedDll);
            foreach (var name in FontSizes.Keys)
                AtomicCopy(Path.Combine(stagedFonts, name), Path.Combine(gameFonts, name));
            AtomicCopy(stagedSb, installedSb);
            if (Hash(installedSb) != Hash(stagedSb))
                throw new InvalidDataException("Hash en.sb sau khi cài không khớp bản dựng");
            foreach (var name in FontSizes.Keys)
                if (Hash(Path.Combine(gameFonts, name)) != Hash(Path.Combine(stagedFonts, name)))
                    throw new InvalidDataException($"Hash font sau khi cài không khớp: {name}");
            if (Hash(installedDll) != Hash(hookDll) || Hash(installedToc) != Hash(stagedToc))
                throw new InvalidDataException("Hook hoặc TOC sau khi cài không khớp");
        }
        catch
        {
            RestoreFiles(backup, targets, existing);
            throw;
        }
        return backup;
    }

    public static string RestoreLatest(string gameDirectory, string backupRoot)
    {
        EnsureGameClosed();
        if (!Directory.Exists(backupRoot)) throw new DirectoryNotFoundException("Chưa có bản sao lưu từ GUI");
        var latest = Directory.GetDirectories(backupRoot)
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).FirstOrDefault();
        if (latest is null) throw new DirectoryNotFoundException("Chưa có bản sao lưu từ GUI");
        var manifestPath = Path.Combine(latest, "backup_manifest.json");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Không đọc được thông tin sao lưu");
        var game = Path.GetFullPath(gameDirectory);
        if (!string.Equals(game, Path.GetFullPath(manifest.GameDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bản sao lưu này thuộc thư mục game khác");
        var gameLoc = Path.Combine(game, "Update", "Patch", "Data", "Win32", "Loc", "en.sb");
        var targets = new Dictionary<string, string>
        {
            ["en.sb"] = gameLoc,
            ["dinput8.dll"] = Path.Combine(game, "dinput8.dll")
        };
        foreach (var name in FontSizes.Keys) targets.Add(name, Path.Combine(game, "fonts", name));
        RestoreFiles(latest, targets, manifest.ExistingFiles);
        return latest;
    }

    private static void RestoreFiles(string backup, IReadOnlyDictionary<string, string> targets,
        IReadOnlyDictionary<string, bool> existed)
    {
        foreach (var (name, target) in targets)
        {
            if (!existed.TryGetValue(name, out var wasPresent))
                throw new InvalidDataException($"Bản sao lưu thiếu trạng thái của {name}");
            if (wasPresent) AtomicCopy(Path.Combine(backup, name), target);
            else if (File.Exists(target)) File.Delete(target);
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        var temporary = destination + ".bf3vi.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void EnsureGameClosed()
    {
        if (Process.GetProcessesByName("bf3").Length != 0)
            throw new InvalidOperationException("Hãy thoát Battlefield 3 trước khi cài hoặc khôi phục file.");
    }

    private static bool IsX86Dll(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z') return false;
        var peAt = BitConverter.ToInt32(bytes, 0x3C);
        return peAt >= 0 && peAt + 6 <= bytes.Length &&
            bytes.AsSpan(peAt, 4).SequenceEqual("PE\0\0"u8) &&
            BitConverter.ToUInt16(bytes, peAt + 4) == 0x014C;
    }
}
