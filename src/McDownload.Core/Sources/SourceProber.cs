using System.Diagnostics;
using McDownload.Core.Diagnostics;
using McDownload.Core.Util;

namespace McDownload.Core.Sources;

/// <summary>Result of measuring one mirror.</summary>
public readonly record struct SourceProbeResult(string SourceId, string DisplayName, bool Ok, long LatencyMs, string? Error);

/// <summary>Measures mirror latency so auto-ordering can pick the fastest site for this network.</summary>
public static class SourceProber
{
    /// <summary>Probes every enabled source in parallel (bounded), updating the health store.</summary>
    public static async Task<IReadOnlyList<SourceProbeResult>> ProbeAsync(
        IEnumerable<DownloadSource> sources,
        HttpClient http,
        SourceHealthStore? health,
        IReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        var targets = sources
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.ProbeUrl))
            .ToList();

        var results = new List<SourceProbeResult>();
        var sync = new object();

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (source, ct) =>
            {
                var outcome = await ProbeOneAsync(source, http, ct).ConfigureAwait(false);
                lock (sync)
                {
                    results.Add(outcome);
                }

                if (health is not null)
                {
                    if (outcome.Ok)
                    {
                        health.RecordSuccess(source.Id, outcome.LatencyMs);
                    }
                    else
                    {
                        health.RecordFailure(source.Id);
                    }
                }

                reporter?.Debug(
                    outcome.Ok
                        ? $"镜像 {source.DisplayName} 延迟 {outcome.LatencyMs} ms"
                        : $"镜像 {source.DisplayName} 不可用：{outcome.Error}",
                    "sources");
            }).ConfigureAwait(false);

        results.Sort((a, b) =>
        {
            if (a.Ok != b.Ok)
            {
                return a.Ok ? -1 : 1;
            }

            return a.LatencyMs.CompareTo(b.LatencyMs);
        });
        return results;
    }

    private static async Task<SourceProbeResult> ProbeOneAsync(DownloadSource source, HttpClient http, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            using var request = new HttpRequestMessage(HttpMethod.Get, source.ProbeUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                return new SourceProbeResult(source.Id, source.DisplayName, false, elapsed, $"HTTP {(int)response.StatusCode}");
            }

            return new SourceProbeResult(source.Id, source.DisplayName, true, elapsed, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SourceProbeResult(source.Id, source.DisplayName, false, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new SourceProbeResult(source.Id, source.DisplayName, false, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
        }
    }
}
