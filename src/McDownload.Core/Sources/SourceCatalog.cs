using System.Text.Json;
using System.Text.Json.Serialization;
using McDownload.Core.Util;

namespace McDownload.Core.Sources;

/// <summary>Loads and persists the list of download sites.</summary>
public sealed class SourceCatalog
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly List<DownloadSource> _sources;

    public SourceCatalog(IEnumerable<DownloadSource> sources)
    {
        _sources = sources.OrderBy(s => s.Priority).ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All sources, including disabled ones (in priority order).</summary>
    public IReadOnlyList<DownloadSource> All => _sources;

    /// <summary>Sources that participate in downloads.</summary>
    public IReadOnlyList<DownloadSource> Enabled => _sources.Where(s => s.Enabled).ToList();

    /// <summary>The built-in catalog embedded in the assembly.</summary>
    public static SourceCatalog LoadDefault()
    {
        var assembly = typeof(SourceCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("default-sources.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded default-sources.json is missing.");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Cannot open resource {name}.");
        return Load(stream);
    }

    /// <summary>Per-user overrides; created by <c>mcdl sources export</c> / <c>mcdl sources disable</c>.</summary>
    public static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "McDownload",
        "sources.json");

    /// <summary>
    /// Loads the user override file when it exists, otherwise the embedded defaults.
    /// A broken override file falls back to the defaults so a typo never blocks installs.
    /// </summary>
    public static SourceCatalog LoadEffective(string? path, out string? warning)
    {
        warning = null;
        var target = path ?? UserConfigPath;
        if (!File.Exists(target))
        {
            return LoadDefault();
        }

        try
        {
            return LoadFile(target);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning = $"无法读取下载源配置 {target}（{ex.Message}），改用内置下载源";
            return LoadDefault();
        }
    }

    /// <summary>Loads the effective catalog without capturing the warning.</summary>
    public static SourceCatalog LoadEffective() => LoadEffective(null, out _);

    public static SourceCatalog Load(Stream stream)
    {
        var document = JsonSerializer.Deserialize<SourceDocument>(stream, SerializerOptions)
            ?? throw new JsonException("sources file is empty or not an object.");
        if (document.Sources.Count == 0)
        {
            throw new JsonException("sources file contains no sources.");
        }

        foreach (var source in document.Sources)
        {
            // System.Text.Json replaces the property value with a case-sensitive dictionary,
            // while endpoint keys are matched against PascalCase ArtifactKind names.
            source.Endpoints = new Dictionary<string, EndpointTemplate>(
                source.Endpoints,
                StringComparer.OrdinalIgnoreCase);
            source.Normalize();
        }

        Validate(document.Sources);
        return new SourceCatalog(document.Sources);
    }

    public static SourceCatalog LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Writes a catalog back to disk (used by <c>mcdl sources add/disable</c>).</summary>
    public void Save(string path)
    {
        PathEx.EnsureParent(path);
        var document = new SourceDocument { Version = 1, Sources = _sources.ToList() };
        File.WriteAllText(path, JsonSerializer.Serialize(document, SerializerOptions), Text.Utf8);
    }

    private static void Validate(List<DownloadSource> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                throw new JsonException("Every source needs a non-empty 'id'.");
            }

            if (!seen.Add(source.Id))
            {
                throw new JsonException($"Duplicate source id '{source.Id}'.");
            }

            foreach (var key in source.Endpoints.Keys)
            {
                if (!Enum.TryParse<ArtifactKind>(key, ignoreCase: true, out _))
                {
                    throw new JsonException($"Source '{source.Id}' declares unknown artifact kind '{key}'.");
                }
            }
        }
    }

    /// <summary>Returns a catalog filtered/ordered by an explicit id list (used by <c>--source</c>).</summary>
    public SourceCatalog WithOrder(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return this;
        }

        var lookup = _sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<DownloadSource>();
        foreach (var id in ids)
        {
            if (!lookup.TryGetValue(id, out var source))
            {
                throw new ArgumentException($"Unknown download source '{id}'. Available: {string.Join(", ", _sources.Select(s => s.Id))}");
            }

            ordered.Add(source);
        }

        // Keep the remaining sources as fallbacks, after the explicitly requested ones.
        ordered.AddRange(_sources.Where(s => !ids.Contains(s.Id, StringComparer.OrdinalIgnoreCase)));
        return new SourceCatalog(ordered.Select((s, index) =>
        {
            var clone = s.Clone();
            clone.Priority = index * 10;
            return clone;
        }));
    }

    internal sealed class SourceDocument
    {
        public int Version { get; set; } = 1;

        public List<DownloadSource> Sources { get; set; } = [];
    }
}

/// <summary>Small helpers for <see cref="DownloadSource"/>.</summary>
public static class DownloadSourceExtensions
{
    public static DownloadSource Clone(this DownloadSource source) => new()
    {
        Id = source.Id,
        DisplayName = source.DisplayName,
        Region = source.Region,
        Priority = source.Priority,
        Enabled = source.Enabled,
        UseOriginalUrl = source.UseOriginalUrl,
        ProbeUrl = source.ProbeUrl,
        Homepage = source.Homepage,
        Note = source.Note,
        Endpoints = new Dictionary<string, EndpointTemplate>(source.Endpoints, StringComparer.OrdinalIgnoreCase),
    };
}
