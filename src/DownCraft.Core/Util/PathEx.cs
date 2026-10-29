namespace DownCraft.Core.Util;

/// <summary>Path helpers that keep every write inside the chosen game directory.</summary>
public static class PathEx
{
    /// <summary>Converts a forward-slash relative path (the format used in Minecraft JSON) to the local separator.</summary>
    public static string Rel(string relativeSlashPath) =>
        relativeSlashPath.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Converts a local path back to the forward-slash form used in JSON and URLs.</summary>
    public static string ToUnix(string path) => path.Replace('\\', '/');

    /// <summary>Creates the parent directory of <paramref name="filePath"/> when it does not exist.</summary>
    public static void EnsureParent(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    /// <summary>
    /// Combines <paramref name="root"/> with a relative path and verifies the result is still
    /// inside <paramref name="root"/>. Guards against <c>..</c> segments coming from downloaded
    /// JSON or ZIP entry names (zip-slip).
    /// </summary>
    public static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, Rel(relative)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to write outside the game directory: {relative}");
        }

        return candidate;
    }

    /// <summary>True when <paramref name="relative"/> contains no traversal segments.</summary>
    public static bool IsSafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.StartsWith('\\'))
        {
            return false;
        }

        if (relative.Length >= 2 && char.IsLetter(relative[0]) && relative[1] == ':')
        {
            return false;
        }

        foreach (var segment in relative.Split('/', '\\'))
        {
            if (segment is ".." or ".")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Human readable byte count (Chinese units kept short for console tables).</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Best-effort directory delete that tolerates locks and read-only files.</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Returns a unique temp path next to <paramref name="target"/> (same volume, atomic move).</summary>
    public static string PartPathFor(string target) => target + ".downcraft-part";
}
