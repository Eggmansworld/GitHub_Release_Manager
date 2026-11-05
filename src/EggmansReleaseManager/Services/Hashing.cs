using System.Security.Cryptography;

namespace EggmansReleaseManager.Services;

public static class Hashing
{
    public static string Sha1File(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var sha = SHA1.Create();
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, n, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC32 (zlib/RomVault flavour) as 8 lower-case hex chars, streamed.</summary>
    public static string Crc32File(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buf = new byte[1 << 20];
        var crc = 0xFFFFFFFFu;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < n; i++) crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        }
        return (crc ^ 0xFFFFFFFFu).ToString("x8");
    }
}
