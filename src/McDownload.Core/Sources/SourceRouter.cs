using System.Text.RegularExpressions;

namespace McDownload.Core.Sources;

/// <summary>
/// Turns an <see cref="ArtifactKind"/> plus a <see cref="SourceContext"/> into an ordered
/// list of URLs to try: mirrors first (ordered by health or priority), canonical URL last.
/// </summary>
public sealed partial class SourceRouter
{
    private readonly IReadOnlyList<DownloadSource> _sources;
    private readonly SourceHealthStore? _health;

    public SourceRouter(IEnumerable<DownloadSource> sources, SourceHealthStore? health = null, bool preferPriorityOrder = false)
    {
        _sources = sources.Where(s => s.Enabled).OrderBy(s => s.Priority).ToList();
        _health = health;
        PreferPriorityOrder = preferPriorityOrder;
    }

    /// <summary>When true, ordering ignores measured latency and follows <c>priority</c>.</summary>
    public bool PreferPriorityOrder { get; }

    /// <summary>Builds the candidate list. The canonical URL is always appended last when known.</summary>
    public IReadOnlyList<SourceCandidate> Build(ArtifactKind kind, SourceContext context, bool includeOriginal = true)
    {
        var candidates = new List<SourceCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            var endpoint = source[kind];
            string? url;
            if (endpoint is not null)
            {
                url = Expand(endpoint.Url, context);
            }
            else if (source.UseOriginalUrl)
            {
                url = context.OriginalUrl;
            }
            else
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
            {
                continue;
            }

            var effectivePriority = endpoint is null || endpoint.Priority == int.MaxValue ? source.Priority : endpoint.Priority;
            candidates.Add(new SourceCandidate(source.Id, url, Score(source.Id, effectivePriority), effectivePriority));
        }

        if (includeOriginal && !string.IsNullOrWhiteSpace(context.OriginalUrl) && seen.Add(context.OriginalUrl))
        {
            candidates.Add(new SourceCandidate("original", context.OriginalUrl, long.MaxValue, int.MaxValue));
        }

        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
        return candidates;
    }

    /// <summary>True when the catalog can serve <paramref name="kind"/> from at least one mirror.</summary>
    public bool HasMirror(ArtifactKind kind) =>
        _sources.Any(s => s[kind] is not null) || _sources.Any(s => s.UseOriginalUrl);

    private long Score(string sourceId, int effectivePriority)
    {
        var latency = _health?.LatencyMs(sourceId);
        var failed = _health?.IsFailing(sourceId) ?? false;
        var measured = latency ?? 3000;
        if (failed)
        {
            measured = 120_000;
        }

        return PreferPriorityOrder
            ? (effectivePriority * 100_000L) + measured
            : (measured * 10L) + effectivePriority;
    }

    /// <summary>
    /// Expands a URL template. Returns <c>null</c> when a placeholder used by the template
    /// has no value in <paramref name="context"/> (the endpoint then simply does not apply).
    /// </summary>
    public static string? Expand(string template, SourceContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var url = template;
        url = Replace(url, "{version}", context.VersionId);
        url = Replace(url, "{minecraftVersion}", context.VersionId);
        url = Replace(url, "{loader}", context.LoaderVersion);
        url = Replace(url, "{path}", context.MavenPath);
        url = Replace(url, "{hash}", context.AssetHash);
        url = Replace(url, "{hash2}", context.AssetHash is { Length: >= 2 } hash ? hash[..2] : null);
        url = Replace(url, "{index}", context.IndexId);
        url = Replace(url, "{component}", context.Component);
        url = Replace(url, "{platform}", context.Platform);
        url = Replace(url, "{file}", context.FileName);
        url = Replace(url, "{sha1}", context.Sha1);
        url = Replace(url, "{url}", context.OriginalUrl);

        // Any placeholder that survived has no backing value: the template does not apply.
        return UnresolvedToken().IsMatch(url) ? null : url;
    }

    /// <summary>
    /// Replaces a placeholder only when a value exists. Leaving the token in place makes the
    /// final unresolved-token check reject the template instead of producing a broken URL.
    /// </summary>
    private static string Replace(string template, string token, string? value) =>
        value is null ? template : template.Replace(token, value, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\{[A-Za-z0-9_]+\}")]
    private static partial Regex UnresolvedToken();
}
