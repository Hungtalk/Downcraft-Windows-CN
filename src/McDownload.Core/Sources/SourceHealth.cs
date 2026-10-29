using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using McDownload.Core.Util;

namespace McDownload.Core.Sources;

/// <summary>Persisted measurement for one source.</summary>
public sealed class SourceHealthEntry
{
    public string SourceId { get; set; } = string.Empty;

    public long? LatencyMs { get; set; }

    public bool Ok { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>
/// Remembers which mirrors were fast (or broken) so auto-ordering can prefer the best
/// site for this machine. Stored under <c>&lt;gameDir&gt;/.mcdl/source-health.json</c>.
/// </summary>
public sealed class SourceHealthStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, SourceHealthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly object _saveLock = new();

    private SourceHealthStore(string? path)
    {
        _path = path;
    }

    /// <summary>Best-known latency for a source, or <c>null</c> when never probed.</summary>
    public long? LatencyMs(string sourceId) =>
        _entries.TryGetValue(sourceId, out var entry) && entry.Ok ? entry.LatencyMs : null;

    /// <summary>True when the source has failed repeatedly and should be considered last.</summary>
    public bool IsFailing(string sourceId) =>
        _entries.TryGetValue(sourceId, out var entry) && (!entry.Ok || entry.ConsecutiveFailures >= 3);

    /// <summary>All recorded entries (ordered by id) for display and diagnostics.</summary>
    public IReadOnlyList<SourceHealthEntry> Snapshot() =>
        _entries.Values.OrderBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase).ToList();

    public void RecordSuccess(string sourceId, long latencyMs)
    {
        _entries[sourceId] = new SourceHealthEntry
        {
            SourceId = sourceId,
            LatencyMs = latencyMs,
            Ok = true,
            ConsecutiveFailures = 0,
            CheckedAt = DateTimeOffset.Now,
        };
    }

    public void RecordFailure(string sourceId)
    {
        var entry = _entries.GetOrAdd(sourceId, id => new SourceHealthEntry { SourceId = id });
        entry.Ok = false;
        entry.ConsecutiveFailures++;
        entry.CheckedAt = DateTimeOffset.Now;
    }

    /// <summary>Loads measurements, silently starting empty when the file is missing or corrupt.</summary>
    public static SourceHealthStore Load(string? path)
    {
        var store = new SourceHealthStore(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return store;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HealthDocument>(File.ReadAllText(path), SerializerOptions);
            if (document?.Entries is not null)
            {
                foreach (var entry in document.Entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.SourceId))
                    {
                        store._entries[entry.SourceId] = entry;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt cache must never break an install.
        }

        return store;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return;
        }

        lock (_saveLock)
        {
            try
            {
                PathEx.EnsureParent(_path);
                var document = new HealthDocument { Entries = Snapshot().ToList() };
                File.WriteAllText(_path, JsonSerializer.Serialize(document, SerializerOptions), Text.Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class HealthDocument
    {
        public List<SourceHealthEntry> Entries { get; set; } = [];
    }
}

/// <summary>
/// Per-install circuit breaker: once a mirror has failed <see cref="Threshold"/> times in a
/// row for one artifact kind, it is skipped for the rest of the run. This keeps a blocked
/// mirror (for example a Cloudflare 403 on thousands of asset files) from slowing every file.
/// </summary>
public sealed class SourceBreaker
{
    /// <summary>Consecutive failures before a (source, kind) pair is parked.</summary>
    public const int Threshold = 3;

    private readonly ConcurrentDictionary<(string Source, ArtifactKind Kind), int> _failures = new();

    public bool ShouldSkip(string sourceId, ArtifactKind kind) =>
        _failures.TryGetValue((sourceId, kind), out var count) && count >= Threshold;

    public void RecordSuccess(string sourceId, ArtifactKind kind) => _failures.TryRemove((sourceId, kind), out _);

    public void RecordFailure(string sourceId, ArtifactKind kind) =>
        _failures.AddOrUpdate((sourceId, kind), 1, static (_, current) => current + 1);

    /// <summary>Number of parked pairs, surfaced in logs when a mirror is skipped.</summary>
    public int ParkedCount => _failures.Count(kv => kv.Value >= Threshold);
}
