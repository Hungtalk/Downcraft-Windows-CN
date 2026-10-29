using System.Text.Json.Serialization;

namespace McDownload.Core.Meta;

/// <summary><c>version_manifest_v2.json</c>.</summary>
public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions Latest { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = [];
}

/// <summary>The two "latest" pointers of the manifest.</summary>
public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("snapshot")]
    public string? Snapshot { get; set; }
}

/// <summary>One entry of the manifest version list.</summary>
public sealed class VersionSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "release";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset? ReleaseTime { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }

    [JsonIgnore]
    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The per-version JSON (also called "version meta" or "launch profile").</summary>
public sealed class VersionMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("jar")]
    public string? Jar { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int? ComplianceLevel { get; set; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int? MinimumLauncherVersion { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset? ReleaseTime { get; set; }

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; set; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexRef? AssetIndex { get; set; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; set; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; set; } = [];

    [JsonPropertyName("javaVersion")]
    public JavaVersionRef? JavaVersion { get; set; }

    [JsonPropertyName("logging")]
    public LoggingSection? Logging { get; set; }

    [JsonPropertyName("arguments")]
    public ArgumentsSection? Arguments { get; set; }

    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    /// <summary>The launcher version id this profile belongs to (its folder name).</summary>
    [JsonIgnore]
    public string LauncherVersionId => string.IsNullOrWhiteSpace(Id) ? "unknown" : Id;
}

/// <summary>A downloadable file with hash and size.</summary>
public class Artifact
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary><c>downloads</c> section of a version JSON.</summary>
public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public Artifact? Client { get; set; }

    [JsonPropertyName("server")]
    public Artifact? Server { get; set; }

    [JsonPropertyName("client_mappings")]
    public Artifact? ClientMappings { get; set; }

    [JsonPropertyName("server_mappings")]
    public Artifact? ServerMappings { get; set; }
}

/// <summary><c>assetIndex</c> section of a version JSON.</summary>
public sealed class AssetIndexRef
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("totalSize")]
    public long? TotalSize { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>One entry of <c>libraries</c>.</summary>
public sealed class Library
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Maven repository base URL (Fabric/Quilt profiles only).</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    /// <summary>Legacy natives mapping, for example <c>{"windows": "natives-windows-${arch}"}</c>.</summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; set; }

    [JsonPropertyName("extract")]
    public LibraryExtract? Extract { get; set; }
}

/// <summary><c>downloads</c> section of a library.</summary>
public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public Artifact? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, Artifact>? Classifiers { get; set; }
}

/// <summary>Natives extraction options.</summary>
public sealed class LibraryExtract
{
    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }
}

/// <summary>A rule that decides whether a library/argument applies to this platform.</summary>
public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }
}

/// <summary>OS predicate of a <see cref="Rule"/>.</summary>
public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary><c>arguments</c> section (kept for completeness; mcdl does not launch the game).</summary>
public sealed class ArgumentsSection
{
    [JsonPropertyName("game")]
    public List<System.Text.Json.JsonElement>? Game { get; set; }

    [JsonPropertyName("jvm")]
    public List<System.Text.Json.JsonElement>? Jvm { get; set; }
}

/// <summary><c>javaVersion</c> section.</summary>
public sealed class JavaVersionRef
{
    [JsonPropertyName("majorVersion")]
    public int? MajorVersion { get; set; }

    [JsonPropertyName("component")]
    public string? Component { get; set; }
}

/// <summary><c>logging</c> section.</summary>
public sealed class LoggingSection
{
    [JsonPropertyName("client")]
    public LoggingEntry? Client { get; set; }
}

/// <summary>One logging configuration file.</summary>
public sealed class LoggingEntry
{
    [JsonPropertyName("argument")]
    public string? Argument { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("file")]
    public LoggingArtifact? File { get; set; }
}

/// <summary>The logging configuration artifact.</summary>
public sealed class LoggingArtifact
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
