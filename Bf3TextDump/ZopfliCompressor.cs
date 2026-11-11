using System.Diagnostics;
using System.IO.Compression;

namespace Bf3TextDump;

internal static class ZopfliCompressor
{
    public static byte[] CompressBlock(ReadOnlySpan<byte> source, int iterations = 5)
    {
        if (iterations is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(iterations));
        var executable = Path.Combine(AppContext.BaseDirectory, "compressor", "zopfli.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Missing bundled Zopfli compressor for the larger translation", executable);

        var input = Path.Combine(Path.GetTempPath(), "bf3-zopfli-" + Guid.NewGuid().ToString("N") + ".bin");
        var output = input + ".zlib";
        try
        {
            File.WriteAllBytes(input, source.ToArray());
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("--zlib");
            info.ArgumentList.Add("--i" + iterations);
            info.ArgumentList.Add(input);
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start the bundled Zopfli compressor");
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Zopfli did not finish within two minutes");
            }
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidDataException("Zopfli failed: " + process.StandardError.ReadToEnd());

            var packed = File.ReadAllBytes(output);
            using var compressed = new MemoryStream(packed);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            zlib.CopyTo(decoded);
            if (!decoded.ToArray().AsSpan().SequenceEqual(source))
                throw new InvalidDataException("Zopfli block did not decode to its input");
            return packed;
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            if (File.Exists(input)) File.Delete(input);
        }
    }
}
