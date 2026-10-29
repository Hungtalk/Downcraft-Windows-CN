using DownCraft.Core.Sources;

namespace DownCraft.Core.Install;

/// <summary>Supported mod loaders (plus Vanilla).</summary>
public enum LoaderKind
{
    Vanilla = 0,
    Forge = 1,
    NeoForge = 2,
    Fabric = 3,
    Quilt = 4,
}

/// <summary>Everything the installer needs to know. Immutable on purpose.</summary>
public sealed class InstallOptions
{
    /// <summary>The <c>.minecraft</c> directory (created when missing).</summary>
    public required string GameDir { get; init; }

    /// <summary>Minecraft version id, for example <c>1.20.1</c>.</summary>
    public required string MinecraftVersion { get; init; }

    public LoaderKind Loader { get; init; } = LoaderKind.Vanilla;

    /// <summary>Loader version; when null the newest stable/recommended build is used.</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>Download the asset index objects (roughly 200–500 MB for modern versions).</summary>
    public bool DownloadAssets { get; init; } = true;

    /// <summary>Parallel HTTP requests.</summary>
    public int MaxParallel { get; init; } = 16;

    /// <summary>Preferred source ids, highest priority first (for example <c>["bmclapi","mojang"]</c>).</summary>
    public IReadOnlyList<string> SourceOrder { get; init; } = [];

    /// <summary>Order mirrors by configured priority instead of measured latency.</summary>
    public bool PreferSourcePriority { get; init; }

    /// <summary>Measure mirror latency before downloading (recommended, takes a second).</summary>
    public bool ProbeSources { get; init; } = true;

    /// <summary>Add/refresh an entry in <c>launcher_profiles.json</c>.</summary>
    public bool CreateLauncherProfile { get; init; } = true;

    /// <summary>Provision a Mojang Java runtime when no suitable JVM is installed.</summary>
    public bool EnsureJava { get; init; }

    /// <summary>Re-download metadata instead of trusting local caches.</summary>
    public bool Refresh { get; init; }

    /// <summary>Only check files against their hashes; never download.</summary>
    public bool VerifyOnly { get; init; }

    /// <summary>Explicit <c>java.exe</c> used to run loader installers.</summary>
    public string? JavaPath { get; init; }

    /// <summary>When false the loader profile is only extracted, its installer is not executed.</summary>
    public bool RunLoaderInstaller { get; init; } = true;

    /// <summary>Display name for the launcher profile entry.</summary>
    public string? ProfileName { get; init; }
}

/// <summary>Outcome of one installation.</summary>
public sealed class InstallResult
{
    public required string GameDir { get; init; }

    /// <summary>Launcher version id that a launcher should launch, e.g. <c>fabric-loader-0.16.9-1.20.1</c>.</summary>
    public string VersionId { get; set; } = string.Empty;

    public string? VersionJsonPath { get; set; }

    public string? ClientJarPath { get; set; }

    public string? LoaderVersion { get; set; }

    public string? JavaPath { get; set; }

    public int Downloaded { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public int LibraryCount { get; set; }

    public int AssetCount { get; set; }

    public int NativesCount { get; set; }

    public long Bytes { get; set; }

    public TimeSpan Elapsed { get; set; }

    public List<string> Warnings { get; init; } = [];

    public List<string> Errors { get; init; } = [];

    public bool Success => Errors.Count == 0;

    public override string ToString() =>
        $"{(Success ? "OK" : "FAILED")} {VersionId} @ {GameDir} (+{Downloaded} files, {Skipped} skipped, {Failed} failed)";
}

/// <summary>Raised when a required step cannot be completed; carries the collected errors.</summary>
public sealed class InstallFailedException : Exception
{
    public InstallFailedException(string message, IReadOnlyList<string>? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Details = details ?? [];
    }

    public IReadOnlyList<string> Details { get; }
}

/// <summary>A natives jar that must be unpacked into the version's <c>natives</c> folder.</summary>
internal sealed record NativesJob(string JarPath, string DestinationDirectory, IReadOnlyList<string> ExcludePrefixes);

/// <summary>Legacy asset copy job (<c>virtual</c> / <c>map_to_resources</c> layouts).</summary>
internal sealed record AssetCopyJob(string SourcePath, string DestinationPath);

/// <summary>Files and post-processing steps computed for one version.</summary>
internal sealed class InstallPlan
{
    public List<Net.DownloadTask> Files { get; } = [];

    public List<NativesJob> Natives { get; } = [];

    public List<AssetCopyJob> AssetCopies { get; } = [];

    public List<string> Warnings { get; } = [];

    public IReadOnlyList<Net.DownloadTask> AssetFiles => Files.Where(f => f.Kind == ArtifactKind.AssetObject).ToList();

    public IReadOnlyList<Net.DownloadTask> NonAssetFiles => Files.Where(f => f.Kind != ArtifactKind.AssetObject).ToList();
}
