namespace DownCraft.Core.Install;

/// <summary>
/// Layout of a standard <c>.minecraft</c> directory. Every path the installer writes is
/// derived from here, which is what makes third-party launchers able to pick the result up.
/// </summary>
public sealed class GamePaths
{
    public GamePaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Game directory must not be empty.", nameof(root));
        }

        Root = Path.GetFullPath(root);
    }

    /// <summary>The <c>.minecraft</c> directory.</summary>
    public string Root { get; }

    public string VersionsDir => Path.Combine(Root, "versions");

    public string LibrariesDir => Path.Combine(Root, "libraries");

    public string AssetsDir => Path.Combine(Root, "assets");

    public string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>downcraft's own bookkeeping (caches, installers, health data) kept out of launcher files.</summary>
    public string StateDir => Path.Combine(Root, ".downcraft");

    public string CacheDir => Path.Combine(StateDir, "cache");

    public string InstallersDir => Path.Combine(StateDir, "installers");

    public string SourceHealthFile => Path.Combine(StateDir, "source-health.json");

    public string LauncherProfilesPath => Path.Combine(Root, "launcher_profiles.json");

    public string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);

    public string VersionJsonPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");

    public string VersionJarPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");

    public string NativesDir(string versionId) => Path.Combine(VersionDir(versionId), "natives");

    public string AssetIndexPath(string indexId) => Path.Combine(AssetsDir, "indexes", indexId + ".json");

    public string AssetObjectPath(string hash) =>
        Path.Combine(AssetsDir, "objects", hash.Length >= 2 ? hash[..2] : "00", hash);

    public string VirtualAssetsDir(string indexId) => Path.Combine(AssetsDir, "virtual", indexId);

    public string LegacyResourcesDir => Path.Combine(Root, "resources");

    public string LoggingConfigPath(string id) => Path.Combine(AssetsDir, "log_configs", id);

    /// <summary>Creates the directory skeleton that launchers expect to exist.</summary>
    public void EnsureLayout()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(LibrariesDir);
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(Path.Combine(AssetsDir, "indexes"));
        Directory.CreateDirectory(Path.Combine(AssetsDir, "objects"));
        Directory.CreateDirectory(Path.Combine(AssetsDir, "log_configs"));
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(InstallersDir);
    }

    /// <summary>Free space on the volume that holds the game directory.</summary>
    public long GetFreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Root) ?? "C:\\").AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public override string ToString() => Root;
}
