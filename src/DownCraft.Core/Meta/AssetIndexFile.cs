using System.Text.Json.Serialization;

namespace DownCraft.Core.Meta;

/// <summary><c>assets/indexes/&lt;id&gt;.json</c>.</summary>
public sealed class AssetIndexFile
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Legacy layouts (pre-1.7) expect files under <c>assets/virtual/&lt;index&gt;</c>.</summary>
    [JsonPropertyName("virtual")]
    public bool Virtual { get; set; }

    /// <summary>Very old layouts expect files under <c>&lt;gameDir&gt;/resources</c>.</summary>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }
}

/// <summary>One asset object; its SHA-1 doubles as the on-disk file name.</summary>
public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
