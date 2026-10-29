using System.Security.Cryptography;
using System.Text;

namespace McDownload.Core.Util;

/// <summary>SHA-1 helpers. Uses the native DLL when present, managed crypto otherwise.</summary>
public static class Sha1
{
    /// <summary>Lower-case hex SHA-1 of a byte span.</summary>
    public static string OfBytes(ReadOnlySpan<byte> data)
    {
        if (Native.NativeInterop.TrySha1Bytes(data, out var nativeHex))
        {
            return nativeHex;
        }

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(data, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Lower-case hex SHA-1 of a file, streamed in 1 MiB chunks.</summary>
    public static string OfFile(string path)
    {
        if (Native.NativeInterop.TrySha1File(path, out var nativeHex))
        {
            return nativeHex;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Lower-case hex SHA-1 of a file, asynchronous.</summary>
    public static async Task<string> OfFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Native.NativeInterop.IsAvailable)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return OfFile(path);
            }, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Constant-time-ish comparison that tolerates null/empty and upper case.</summary>
    public static bool Matches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
        {
            return false;
        }

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>SHA-1/C RC32 wrappers used by the integrity checker.</summary>
public static class Checksums
{
    public static uint Crc32OfFile(string path)
    {
        if (Native.NativeInterop.TryCrc32File(path, out var crc))
        {
            return crc;
        }

        return Crc32.Compute(File.ReadAllBytes(path));
    }
}

/// <summary>Minimal CRC-32 (IEEE) fallback for machines without the native DLL.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>UTF-8 helper for reading mojibake-prone legacy JSON files.</summary>
public static class Text
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static Encoding Utf8 => Utf8NoBom;

    public static string FromUtf8(byte[] bytes)
    {
        // Legacy launchers sometimes wrote UTF-8 with BOM; Trim() handles it cheaply.
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }
}
