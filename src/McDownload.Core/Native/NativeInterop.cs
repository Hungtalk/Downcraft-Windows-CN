using System.Runtime.InteropServices;
using System.Text;

namespace McDownload.Core.Native;

/// <summary>
/// P/Invoke surface for <c>mcdnative.dll</c>. Every entry point is optional: when the
/// DLL is missing (or rejects a call) the caller falls back to a managed implementation,
/// so the application never depends on the native build being present.
/// </summary>
internal static class NativeInterop
{
    private const string DllName = "mcdnative.dll";

    private static readonly Lazy<bool> LazyAvailable = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when <c>mcdnative.dll</c> loaded and reports a version.</summary>
    public static bool IsAvailable => LazyAvailable.Value;

    /// <summary>Native version banner, or <c>null</c> when unavailable.</summary>
    public static string? Version { get; private set; }

    /// <summary>Diagnostic reason recorded when the DLL could not be loaded.</summary>
    public static string? UnavailableReason { get; private set; }

    private static bool Probe()
    {
        try
        {
            var ptr = mcdl_version();
            if (ptr == IntPtr.Zero)
            {
                UnavailableReason = "mcdl_version returned NULL";
                return false;
            }

            Version = Marshal.PtrToStringAnsi(ptr) ?? "(unknown)";
            return true;
        }
        catch (DllNotFoundException)
        {
            UnavailableReason = "mcdnative.dll not found (managed fallback in use)";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            UnavailableReason = "mcdnative.dll is outdated: " + ex.Message;
            return false;
        }
        catch (BadImageFormatException ex)
        {
            UnavailableReason = "mcdnative.dll has the wrong architecture: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public static bool TrySha1File(string path, out string hex)
    {
        hex = string.Empty;
        if (!IsAvailable)
        {
            return false;
        }

        var buffer = new byte[41];
        var rc = mcdl_sha1_file(path, buffer);
        if (rc != 0)
        {
            return false;
        }

        hex = Encoding.ASCII.GetString(buffer, 0, 40);
        return hex.Length == 40;
    }

    public static bool TrySha1Bytes(ReadOnlySpan<byte> data, out string hex)
    {
        hex = string.Empty;
        if (!IsAvailable)
        {
            return false;
        }

        var owned = data.ToArray();
        var buffer = new byte[41];
        var rc = mcdl_sha1_bytes(owned, (nuint)owned.Length, buffer);
        if (rc != 0)
        {
            return false;
        }

        hex = Encoding.ASCII.GetString(buffer, 0, 40);
        return hex.Length == 40;
    }

    public static bool TryCrc32File(string path, out uint crc)
    {
        crc = 0;
        if (!IsAvailable)
        {
            return false;
        }

        return mcdl_crc32_file(path, out crc) == 0;
    }

    public static bool TryZipExtractAll(string zipPath, string destDir, IReadOnlyList<string> excludePrefixes, out int filesWritten)
    {
        filesWritten = 0;
        if (!IsAvailable)
        {
            return false;
        }

        // String arrays cannot use LPUTF8Str, so the UTF-8 pointers are allocated by hand.
        var allocated = new IntPtr[excludePrefixes.Count];
        try
        {
            for (var i = 0; i < excludePrefixes.Count; i++)
            {
                allocated[i] = Marshal.StringToCoTaskMemUTF8(excludePrefixes[i]);
            }

            var rc = mcdl_zip_extract_all(zipPath, destDir, allocated, (nuint)allocated.Length);
            if (rc < 0)
            {
                return false;
            }

            filesWritten = rc;
            return true;
        }
        finally
        {
            foreach (var pointer in allocated)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    public static bool TryZipExtractEntry(string zipPath, string entryName, string destPath)
        => IsAvailable && mcdl_zip_extract(zipPath, entryName, destPath) == 0;

    public static bool TryZipReadEntry(string zipPath, string entryName, out byte[] content)
    {
        content = [];
        if (!IsAvailable)
        {
            return false;
        }

        var sizeStatus = mcdl_zip_entry_size(zipPath, entryName, out var size);
        if (sizeStatus != 1 || size > int.MaxValue)
        {
            return false;
        }

        if (size == 0)
        {
            content = [];
            return true;
        }

        var buffer = new byte[(int)size];
        var rc = mcdl_zip_read(zipPath, entryName, buffer, (nuint)buffer.Length, out var written);
        if (rc != 0)
        {
            return false;
        }

        if ((int)written != buffer.Length)
        {
            Array.Resize(ref buffer, (int)written);
        }

        content = buffer;
        return true;
    }

    public static bool TryDiskFree(string path, out ulong freeBytes)
    {
        freeBytes = 0;
        return IsAvailable && mcdl_disk_free(path, out freeBytes) == 0;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern IntPtr mcdl_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_sha1_file([MarshalAs(UnmanagedType.LPWStr)] string path, byte[] outHex);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int mcdl_sha1_bytes(byte[] data, nuint len, byte[] outHex);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_crc32_file([MarshalAs(UnmanagedType.LPWStr)] string path, out uint crc);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_zip_entry_size(
        [MarshalAs(UnmanagedType.LPWStr)] string zipPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string entryName,
        out ulong size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_zip_read(
        [MarshalAs(UnmanagedType.LPWStr)] string zipPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string entryName,
        byte[] buffer,
        nuint bufferSize,
        out nuint written);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_zip_extract(
        [MarshalAs(UnmanagedType.LPWStr)] string zipPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string entryName,
        [MarshalAs(UnmanagedType.LPWStr)] string destPath);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_zip_extract_all(
        [MarshalAs(UnmanagedType.LPWStr)] string zipPath,
        [MarshalAs(UnmanagedType.LPWStr)] string destDir,
        IntPtr[] excludePrefixes,
        nuint excludeCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int mcdl_disk_free([MarshalAs(UnmanagedType.LPWStr)] string path, out ulong freeBytes);
}
