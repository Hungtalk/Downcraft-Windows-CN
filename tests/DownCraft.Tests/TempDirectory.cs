namespace DownCraft.Tests;

/// <summary>
/// A unique directory for file-system tests. It is created below the test binaries directory
/// when possible (always writable), falling back to the system temp folder, and disposed
/// best-effort so a locked handle never fails the test that created it.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private static readonly string Root = ResolveRoot();

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string CreateFile(string relativePath, string content)
    {
        var target = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveRoot()
    {
        // Some hardened Windows profiles deny writes to %TEMP% for binaries living on other
        // volumes, so prefer a folder next to the test assembly.
        var besideBinaries = System.IO.Path.Combine(AppContext.BaseDirectory, "test-temp");
        try
        {
            Directory.CreateDirectory(besideBinaries);
            return besideBinaries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var systemTemp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DownCraft.Tests");
        Directory.CreateDirectory(systemTemp);
        return systemTemp;
    }
}
