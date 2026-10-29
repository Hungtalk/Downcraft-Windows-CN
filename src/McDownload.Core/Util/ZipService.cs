using System.IO.Compression;
using McDownload.Core.Util;

namespace McDownload.Core.Util;

/// <summary>
/// ZIP access used for <c>natives</c> jars and for inspecting loader installers.
/// Prefers <c>mcdnative.dll</c> (faster, no managed allocation per entry) and falls back
/// to <see cref="ZipArchive"/> with the same zip-slip protection.
/// </summary>
public static class ZipService
{
    /// <summary>Prefixes skipped when extracting a natives archive.</summary>
    public static readonly string[] NativesExcludes = ["META-INF/", "META-INF\\", ".git/", "__MACOSX/"];

    /// <summary>Extracts every file entry into <paramref name="destinationDirectory"/>. Returns the file count.</summary>
    public static int ExtractAll(string zipPath, string destinationDirectory, IReadOnlyList<string>? excludePrefixes = null)
    {
        Directory.CreateDirectory(destinationDirectory);
        var excludes = excludePrefixes ?? NativesExcludes;

        if (Native.NativeInterop.TryZipExtractAll(zipPath, destinationDirectory, excludes, out var nativeCount))
        {
            return nativeCount;
        }

        var written = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            if (excludes.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!PathEx.IsSafeRelative(entry.FullName))
            {
                throw new InvalidDataException($"ZIP 条目包含非法路径：{entry.FullName}");
            }

            var destination = PathEx.SafeCombine(destinationDirectory, entry.FullName);
            PathEx.EnsureParent(destination);
            entry.ExtractToFile(destination, overwrite: true);
            written++;
        }

        return written;
    }

    /// <summary>Extracts a single named entry, creating parent directories.</summary>
    public static void ExtractEntry(string zipPath, string entryName, string destinationPath)
    {
        PathEx.EnsureParent(destinationPath);
        if (Native.NativeInterop.TryZipExtractEntry(zipPath, entryName, destinationPath))
        {
            return;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName)
            ?? throw new FileNotFoundException($"ZIP 中不存在条目 {entryName}", zipPath);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    /// <summary>Reads one entry into memory. Returns <c>null</c> when the entry does not exist.</summary>
    public static byte[]? ReadEntry(string zipPath, string entryName)
    {
        if (Native.NativeInterop.TryZipReadEntry(zipPath, entryName, out var nativeBytes))
        {
            return nativeBytes;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream(entry.Length > 0 ? (int)entry.Length : 4096);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Lists entry names (used for diagnostics and for locating installer profiles).</summary>
    public static IReadOnlyList<string> ListEntries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries.Select(e => e.FullName).ToList();
    }
}
