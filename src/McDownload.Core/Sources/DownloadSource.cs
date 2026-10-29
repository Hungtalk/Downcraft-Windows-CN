using System.Text.Json;
using System.Text.Json.Serialization;

namespace McDownload.Core.Sources;

/// <summary>
/// The kind of artifact being fetched. Each kind maps to an endpoint key inside
/// <c>default-sources.json</c>, which is how a new mirror can be added without code.
/// </summary>
public enum ArtifactKind
{
    /// <summary><c>version_manifest_v2.json</c>.</summary>
    VersionManifest,

    /// <summary>The per-version JSON document.</summary>
    VersionJson,

    /// <summary><c>client.jar</c> for one version.</summary>
    ClientJar,

    /// <summary><c>assets/indexes/&lt;id&gt;.json</c>.</summary>
    AssetIndex,

    /// <summary>One hashed object below <c>assets/objects</c>.</summary>
    AssetObject,

    /// <summary>One library jar below <c>libraries</c>.</summary>
    Library,

    /// <summary><c>assets/log_configs/*.xml</c>.</summary>
    LoggingConfig,

    /// <summary>Fabric loader version list.</summary>
    FabricLoaderList,

    /// <summary>Fabric loader profile JSON (the merged launch profile).</summary>
    FabricProfile,

    /// <summary>Quilt loader version list.</summary>
    QuiltLoaderList,

    /// <summary>Quilt loader profile JSON.</summary>
    QuiltProfile,

    /// <summary>Forge installer jar.</summary>
    ForgeInstaller,

    /// <summary>Forge <c>maven-metadata.xml</c>.</summary>
    ForgeMavenMetadata,

    /// <summary>Forge promotions JSON (recommended/latest per game version).</summary>
    ForgePromotions,

    /// <summary>NeoForge installer jar.</summary>
    NeoForgeInstaller,

    /// <summary>NeoForge <c>maven-metadata.xml</c>.</summary>
    NeoForgeMavenMetadata,

    /// <summary>NeoForge 1.20.1-era metadata (<c>net.neoforged:forge</c> coordinates).</summary>
    NeoForgeLegacyMavenMetadata,

    /// <summary>Mojang Java runtime component manifest list (<c>all.json</c>).</summary>
    JavaRuntimeManifest,

    /// <summary>One file inside a Mojang Java runtime component.</summary>
    JavaRuntimeFile,

    /// <summary>An arbitrary absolute URL with no known mirror.</summary>
    Direct,
}

/// <summary>
/// One URL template plus an optional per-endpoint priority. In JSON an endpoint is
/// either <c>"https://…/{version}/…"</c> or <c>{ "url": "…", "priority": 600 }</c>.
/// </summary>
[JsonConverter(typeof(EndpointTemplateConverter))]
public sealed class EndpointTemplate
{
    public EndpointTemplate()
    {
    }

    public EndpointTemplate(string url, int priority = int.MaxValue)
    {
        Url = url;
        Priority = priority;
    }

    /// <summary>URL template supporting <c>{version}</c>, <c>{loader}</c>, <c>{path}</c>, <c>{hash}</c>, <c>{hash2}</c>, <c>{index}</c>, <c>{component}</c>, <c>{platform}</c>, <c>{file}</c>, <c>{sha1}</c>, <c>{url}</c>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Lower sorts first inside a source; <see cref="int.MaxValue"/> inherits the source priority.</summary>
    public int Priority { get; set; } = int.MaxValue;
}

/// <summary>Reads both the string and the object form of an endpoint template.</summary>
public sealed class EndpointTemplateConverter : JsonConverter<EndpointTemplate>
{
    public override EndpointTemplate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new EndpointTemplate(reader.GetString() ?? string.Empty);
            case JsonTokenType.StartObject:
            {
                var template = new EndpointTemplate();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    var name = reader.GetString();
                    reader.Read();
                    if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase))
                    {
                        template.Url = reader.GetString() ?? string.Empty;
                    }
                    else if (string.Equals(name, "priority", StringComparison.OrdinalIgnoreCase))
                    {
                        template.Priority = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : int.MaxValue;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return template;
            }

            case JsonTokenType.Null:
                return new EndpointTemplate();
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for an endpoint template.");
        }
    }

    public override void Write(Utf8JsonWriter writer, EndpointTemplate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("url", value.Url);
        if (value.Priority != int.MaxValue)
        {
            writer.WriteNumber("priority", value.Priority);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// A download site (official or mirror). Sources are pure data: they describe which
/// artifacts they can serve and how to build the URL for each one.
/// </summary>
public sealed class DownloadSource
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Free-form region tag ("global", "cn", "custom"); shown by <c>mcdl sources</c>.</summary>
    public string Region { get; set; } = "global";

    /// <summary>Default ordering weight; lower is preferred.</summary>
    public int Priority { get; set; } = 500;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true this source serves kinds it has no endpoint for by reusing the
    /// canonical URL from the version metadata (that is how the official site works).
    /// </summary>
    public bool UseOriginalUrl { get; set; }

    /// <summary>Small URL used by <c>mcdl sources test</c> to measure latency.</summary>
    public string? ProbeUrl { get; set; }

    public string? Homepage { get; set; }

    public string? Note { get; set; }

    public Dictionary<string, EndpointTemplate> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public EndpointTemplate? this[ArtifactKind kind]
    {
        get
        {
            if (Endpoints.TryGetValue(kind.ToString(), out var template) && !string.IsNullOrWhiteSpace(template.Url))
            {
                return template;
            }

            // Tolerate a dictionary whose comparer was lost (for example after JSON binding).
            foreach (var entry in Endpoints)
            {
                if (string.Equals(entry.Key, kind.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(entry.Value.Url))
                {
                    return entry.Value;
                }
            }

            return null;
        }
    }

    /// <summary>Canonicalises endpoint keys to the enum names so lookups always succeed.</summary>
    public void Normalize()
    {
        if (Endpoints.Count == 0)
        {
            return;
        }

        var canonical = new Dictionary<string, EndpointTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Endpoints)
        {
            if (Enum.TryParse<ArtifactKind>(key, ignoreCase: true, out var kind))
            {
                canonical[kind.ToString()] = value;
            }
        }

        Endpoints = canonical;
    }

    /// <summary>True when this source claims it can serve <paramref name="kind"/>.</summary>
    public bool Supports(ArtifactKind kind) => this[kind] is not null || (UseOriginalUrl && kind != ArtifactKind.Direct);
}

/// <summary>Everything a template may need in order to build a concrete URL.</summary>
public sealed record SourceContext(
    string? VersionId = null,
    string? LoaderVersion = null,
    string? MavenPath = null,
    string? AssetHash = null,
    string? IndexId = null,
    string? Component = null,
    string? Platform = null,
    string? FileName = null,
    string? Sha1 = null,
    string? OriginalUrl = null)
{
    public static SourceContext ForVersion(string versionId) => new(VersionId: versionId);
}

/// <summary>A candidate URL for one artifact, scored for ordering.</summary>
public readonly record struct SourceCandidate(string SourceId, string Url, long Score, int EffectivePriority);
