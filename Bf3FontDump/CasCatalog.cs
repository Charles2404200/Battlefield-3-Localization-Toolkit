using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Bf3FontDump;

internal sealed record CasEntry(string CatalogPath, string ArchivePath, uint Offset, uint Size, string Sha1);

internal sealed class CasCatalog
{
    private readonly Dictionary<string, CasEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0xD1, 0xCE, 0x00 }) ||
            bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0xD1, 0xCE, 0x01 }))
        {
            if (bytes.Length < 556) throw new InvalidDataException("Encrypted CAT is too short");
            var decoded = new byte[bytes.Length - 556];
            for (var index = 0; index < decoded.Length; index++)
                decoded[index] = (byte)(bytes[index + 556] ^ bytes[296 + index % 257] ^ 0x7B);
            bytes = decoded;
        }
        if (bytes.Length < 16 || !bytes.AsSpan(0, 16).SequenceEqual(Encoding.ASCII.GetBytes("NyanNyanNyanNyan")))
            throw new InvalidDataException($"CAT signature missing: {path}");
        if ((bytes.Length - 16) % 32 != 0)
            throw new InvalidDataException($"CAT entry table has an invalid length: {path}");

        for (var at = 16; at < bytes.Length; at += 32)
        {
            var sha1 = Convert.ToHexString(bytes.AsSpan(at, 20)).ToLowerInvariant();
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 20, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 24, 4));
            var archiveNumber = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 28, 4));
            var archive = Path.Combine(Path.GetDirectoryName(path)!, $"cas_{archiveNumber:00}.cas");
            _entries[sha1] = new CasEntry(path, archive, offset, size, sha1);
        }
    }

    public CasEntry Find(byte[] sha1)
    {
        var key = Convert.ToHexString(sha1).ToLowerInvariant();
        if (_entries.TryGetValue(key, out var entry)) return entry;
        throw new InvalidDataException($"SHA-1 {key} was not found in base or patch cas.cat");
    }

    public static byte[] ReadPayload(CasEntry entry, bool compressed, ulong expectedSize)
    {
        using var archive = File.OpenRead(entry.ArchivePath);
        if (entry.Offset < 32)
            throw new InvalidDataException($"CAS payload has no 32-byte record header: {entry.ArchivePath}");
        if ((long)entry.Offset + entry.Size > archive.Length)
            throw new InvalidDataException($"CAS record extends past archive: {entry.ArchivePath}");
        archive.Position = entry.Offset - 32;
        var recordHeader = new byte[32];
        archive.ReadExactly(recordHeader);
        if (!recordHeader.AsSpan(0, 4).SequenceEqual(new byte[] { 0xFA, 0xCE, 0x0F, 0xF0 }) ||
            Convert.ToHexString(recordHeader.AsSpan(4, 20)).ToLowerInvariant() != entry.Sha1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(24, 4)) != entry.Size ||
            BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(28, 4)) != 0)
            throw new InvalidDataException($"CAS record header does not match catalog: {entry.Sha1}");
        archive.Position = entry.Offset;
        var packed = new byte[checked((int)entry.Size)];
        archive.ReadExactly(packed);
        var actualSha1 = Convert.ToHexString(SHA1.HashData(packed)).ToLowerInvariant();
        if (!actualSha1.Equals(entry.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"CAS payload SHA-1 mismatch for {entry.Sha1}");
        if (!compressed)
        {
            if ((ulong)packed.Length != expectedSize)
                throw new InvalidDataException($"Unexpected raw asset size for {entry.Sha1}");
            return packed;
        }

        using var decoded = new MemoryStream();
        var position = 0;
        while (position < packed.Length)
        {
            if (packed.Length - position < 8)
                throw new InvalidDataException($"Incomplete CAS block header for {entry.Sha1}");
            var unpackedSize = BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(position, 4));
            var packedSize = BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(position + 4, 4));
            position += 8;
            if (packedSize > packed.Length - position || unpackedSize > 65536)
                throw new InvalidDataException($"Invalid CAS block length for {entry.Sha1}");
            var block = packed.AsSpan(position, (int)packedSize).ToArray();
            position += (int)packedSize;
            var before = decoded.Length;
            try
            {
                using var compressedBlock = new MemoryStream(block, writable: false);
                using var zlib = new ZLibStream(compressedBlock, CompressionMode.Decompress);
                zlib.CopyTo(decoded);
            }
            catch (InvalidDataException)
            {
                // Frostbite permits raw blocks alongside zlib blocks.
                decoded.Position = before;
                decoded.SetLength(before);
                decoded.Write(block);
            }
            if (decoded.Length - before != unpackedSize)
                throw new InvalidDataException($"CAS block decoded to the wrong length for {entry.Sha1}");
        }
        if ((ulong)decoded.Length != expectedSize)
            throw new InvalidDataException($"Decoded asset size mismatch for {entry.Sha1}: {decoded.Length} vs {expectedSize}");
        return decoded.ToArray();
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
