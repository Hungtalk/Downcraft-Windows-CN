namespace McDownload.Core.Native;

/// <summary>
/// Public view of the native helper status, shown by <c>mcdl doctor</c> and in the GUI footer.
/// </summary>
public static class NativeStatus
{
    /// <summary>True when <c>mcdnative.dll</c> loaded successfully.</summary>
    public static bool IsAvailable => NativeInterop.IsAvailable;

    /// <summary>Version banner reported by the DLL (for example <c>mcdnative 1.0.0 (msvc)</c>).</summary>
    public static string? Version => NativeInterop.Version;

    /// <summary>Explains why the native helper is not in use (empty when it is).</summary>
    public static string? UnavailableReason => NativeInterop.UnavailableReason;

    /// <summary>Formats a one-line status for diagnostics output.</summary>
    public static string Describe() =>
        IsAvailable ? Version ?? "(unknown native build)" : "managed fallback: " + (UnavailableReason ?? "not loaded");
}
