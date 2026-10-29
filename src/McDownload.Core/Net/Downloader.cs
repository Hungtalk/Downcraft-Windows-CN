using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using McDownload.Core.Diagnostics;
using McDownload.Core.Sources;
using McDownload.Core.Util;

namespace McDownload.Core.Net;

/// <summary>
/// HTTP layer with mirror rotation: every artifact is fetched from the best available
/// site, verified against its SHA-1, resumed from a partial file when possible, and
/// retried on another mirror when a site fails.
/// </summary>
public sealed class Downloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly SourceRouter _router;
    private readonly SourceHealthStore? _health;
    private readonly SourceBreaker _breaker = new();
    private readonly DownloaderOptions _options;
    private readonly IReporter _reporter;
    private readonly ProgressPump _pump;
    private readonly bool _ownsHttpClient;
    private long _skippedFiles;
    private long _downloadedFiles;

    public Downloader(
        SourceRouter router,
        DownloaderOptions options,
        IReporter reporter,
        ProgressPump pump,
        SourceHealthStore? health = null,
        HttpClient? httpClient = null)
    {
        _router = router;
        _options = options;
        _reporter = reporter;
        _pump = pump;
        _health = health;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = Math.Max(4, options.MaxParallel),
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = true,
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            _ownsHttpClient = true;
        }
    }

    /// <summary>The shared client (also used for small metadata requests).</summary>
    public HttpClient Http => _http;

    public long SkippedFiles => Interlocked.Read(ref _skippedFiles);

    public long DownloadedFiles => Interlocked.Read(ref _downloadedFiles);

    /// <summary>
    /// Fetches a small document (JSON/XML) following the mirror order. Returns <c>null</c>
    /// when every candidate failed and <paramref name="required"/> is false.
    /// </summary>
    public async Task<byte[]?> GetBytesAsync(
        ArtifactKind kind,
        SourceContext context,
        bool required = true,
        CancellationToken cancellationToken = default)
    {
        var candidates = OrderedCandidates(kind, context);
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                using var response = await _http
                    .GetAsync(candidate.Url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (!string.IsNullOrEmpty(context.Sha1) && _options.VerifyHashes &&
                    !Sha1.Matches(context.Sha1, Sha1.OfBytes(bytes)))
                {
                    lastError = new InvalidDataException($"SHA-1 mismatch for {context.VersionId ?? kind.ToString()} from {candidate.SourceId}");
                    RecordFailure(candidate.SourceId, kind);
                    continue;
                }

                RecordSuccess(candidate.SourceId, kind, elapsed);
                _reporter.Debug($"get {kind} <- {candidate.SourceId} ({PathEx.FormatBytes(bytes.Length)}, {elapsed} ms)", "http");
                return bytes;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                RecordFailure(candidate.SourceId, kind);
                _reporter.Debug($"mirror {candidate.SourceId} failed for {kind}: {Describe(ex)} — trying next", "http");
            }
        }

        if (required)
        {
            throw new DownloadFailedException($"{kind} could not be fetched from any source.", lastError);
        }

        _reporter.Warn($"{kind} unavailable from all sources: {Describe(lastError)}", "http");
        return null;
    }

    /// <summary>
    /// Downloads (or verifies) a batch of files with bounded parallelism.
    /// Existing files that already match their hash are skipped.
    /// </summary>
    public async Task<DownloadBatchResult> RunAsync(
        IReadOnlyList<DownloadTask> tasks,
        InstallPhase phase,
        int? parallelism = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DownloadBatchResult { Total = tasks.Count };
        _pump.Phase = phase;
        _pump.AddTotal(tasks.Sum(t => t.Size ?? 0), tasks.Count);
        _pump.Report(force: true);

        var sync = new object();
        var parallel = Math.Max(1, parallelism ?? _options.MaxParallel);

        await Parallel.ForEachAsync(
            tasks,
            new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = cancellationToken },
            async (task, ct) =>
            {
                DownloadOutcome outcome;
                try
                {
                    outcome = await DownloadOneAsync(task, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = new DownloadOutcome(false, false, 0, Describe(ex), null, null);
                }

                lock (sync)
                {
                    result.Bytes += outcome.Bytes;
                    if (outcome.Success)
                    {
                        if (outcome.Skipped)
                        {
                            result.Skipped++;
                            Interlocked.Increment(ref _skippedFiles);
                        }
                        else
                        {
                            result.Downloaded++;
                            Interlocked.Increment(ref _downloadedFiles);
                        }
                    }
                    else
                    {
                        result.Failed++;
                        result.Errors.Add((task, outcome.Error ?? "unknown error"));
                    }
                }

                _pump.Report();
            }).ConfigureAwait(false);

        _pump.Report(force: true);
        return result;
    }

    /// <summary>Checks hash/size of a task without downloading it.</summary>
    public static bool IsUpToDate(DownloadTask task)
    {
        if (!File.Exists(task.TargetPath))
        {
            return false;
        }

        var info = new FileInfo(task.TargetPath);
        if (task.Size is long expectedSize && info.Length != expectedSize)
        {
            return false;
        }

        if (string.IsNullOrEmpty(task.Sha1))
        {
            return true;
        }

        try
        {
            return Sha1.Matches(task.Sha1, Sha1.OfFile(task.TargetPath));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<DownloadOutcome> DownloadOneAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        if (task.Kind == ArtifactKind.Direct && string.IsNullOrWhiteSpace(task.Context.OriginalUrl))
        {
            return new DownloadOutcome(false, false, 0, "no download URL for task", null, null);
        }

        if (File.Exists(task.TargetPath) && IsUpToDate(task))
        {
            var size = new FileInfo(task.TargetPath).Length;
            return new DownloadOutcome(true, true, size, null, null, task.TargetPath) with { Bytes = size };
        }

        if (!task.DownloadAllowed)
        {
            return new DownloadOutcome(false, false, 0, "file is missing and downloads are disabled", null, null)
            {
                Bytes = 0,
            };
        }

        var candidates = OrderedCandidates(task.Kind, task.Context, task.TargetPath);
        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            for (var attempt = 1; attempt <= _options.RetriesPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var bytes = await FetchToFileAsync(task, candidate.Url, cancellationToken).ConfigureAwait(false);
                    var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    RecordSuccess(candidate.SourceId, task.Kind, elapsed);
                    Interlocked.Add(ref _downloadedBytesThisBatch, 0);
                    return new DownloadOutcome(true, false, bytes, null, candidate.SourceId, candidate.Url);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HashMismatchException ex)
                {
                    // The file arrived but was corrupt: try another mirror immediately.
                    failures.Add($"{candidate.SourceId}: {ex.Message}");
                    RecordFailure(candidate.SourceId, task.Kind);
                    _reporter.Warn($"{task} 校验失败（{candidate.SourceId}），切换下载源", "http");
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    RecordFailure(candidate.SourceId, task.Kind);
                    var transient = HttpFailure.IsTransient(ex, out var status);
                    if (transient && attempt < _options.RetriesPerUrl)
                    {
                        _reporter.Debug($"retry {task} via {candidate.SourceId} ({Describe(ex)})", "http");
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    failures.Add($"{candidate.SourceId}: {Describe(ex)}{(status is null ? "" : $" ({(int)status} {status})")}");
                    _reporter.Debug($"mirror {candidate.SourceId} failed for {task}: {Describe(ex)}", "http");
                    break;
                }
            }
        }

        var message = failures.Count > 0 ? string.Join("; ", failures.Distinct().Take(4)) : "all mirrors failed";
        return new DownloadOutcome(false, false, 0, message, null, null);
    }

    private long _downloadedBytesThisBatch;

    private async Task<long> FetchToFileAsync(DownloadTask task, string url, CancellationToken cancellationToken)
    {
        var partPath = PathEx.PartPathFor(task.TargetPath);
        PathEx.EnsureParent(task.TargetPath);

        long resumedFrom = 0;
        if (File.Exists(partPath))
        {
            resumedFrom = new FileInfo(partPath).Length;
            if (task.Size is long known && resumedFrom >= known)
            {
                File.Delete(partPath);
                resumedFrom = 0;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumedFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumedFrom, null);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ComputeTimeout(task.Size));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumedFrom > 0)
        {
            File.Delete(partPath);
            throw new IOException("server rejected the resume range");
        }

        response.EnsureSuccessStatusCode();

        // A server that ignores Range replies 200 with the whole body: restart the file.
        if (resumedFrom > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            resumedFrom = 0;
        }

        var expectedLength = response.Content.Headers.ContentLength;
        long written = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var destination = new FileStream(
                partPath,
                resumedFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[64 * 1024];
            long lastProgress = Stopwatch.GetTimestamp();
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                written += read;
                _pump.AddBytes(read);
                if (Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds > 150)
                {
                    _pump.CurrentItem = task.ToString();
                    _pump.Report();
                    lastProgress = Stopwatch.GetTimestamp();
                }
            }

            await destination.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Roll back progress accounting for the bytes that will be re-fetched.
            _pump.AddBytes(-written);
            throw;
        }

        var totalSize = resumedFrom + written;
        if (expectedLength is long expectedBody && resumedFrom == 0 && written != expectedBody)
        {
            _pump.AddBytes(-written);
            throw new IOException($"truncated body: got {written} of {expectedBody} bytes");
        }

        if (task.Size is long expectedSize && totalSize != expectedSize)
        {
            File.Delete(partPath);
            _pump.AddBytes(-(written));
            throw new IOException($"size mismatch: got {totalSize} bytes, expected {expectedSize}");
        }

        if (_options.VerifyHashes && !string.IsNullOrEmpty(task.Sha1))
        {
            var actual = Sha1.OfFile(partPath);
            if (!Sha1.Matches(task.Sha1, actual))
            {
                try
                {
                    File.Delete(partPath);
                }
                catch (IOException)
                {
                }

                _pump.AddBytes(-(written));
                throw new HashMismatchException($"SHA-1 {actual} != {task.Sha1}");
            }
        }

        File.Move(partPath, task.TargetPath, overwrite: true);
        return totalSize;
    }

    private TimeSpan ComputeTimeout(long? size)
    {
        if (size is not long bytes || bytes <= 0)
        {
            return _options.BaseTimeout;
        }

        var seconds = bytes / (double)_options.MinimumBytesPerSecond;
        return TimeSpan.FromSeconds(Math.Clamp(_options.BaseTimeout.TotalSeconds + seconds, 100, 1800));
    }

    private List<SourceCandidate> OrderedCandidates(ArtifactKind kind, SourceContext context, string? targetPath = null)
    {
        var all = _router.Build(kind, context);
        if (all.Count == 0)
        {
            var fallback = context.OriginalUrl;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return [new SourceCandidate("original", fallback, 0, int.MaxValue)];
            }

            var file = targetPath is null ? string.Empty : $"（{Path.GetFileName(targetPath)}）";
            _reporter.Warn($"{kind} 没有任何可用下载源{file}，请检查 mcdl sources list", "sources");
            return [];
        }

        // Park failing mirrors, but never end up with an empty candidate list.
        var filtered = all.Where(c => !_breaker.ShouldSkip(c.SourceId, kind)).ToList();
        return filtered.Count > 0 ? filtered : all.ToList();
    }

    private void RecordSuccess(string sourceId, ArtifactKind kind, long latencyMs)
    {
        _breaker.RecordSuccess(sourceId, kind);
        if (!string.Equals(sourceId, "original", StringComparison.OrdinalIgnoreCase))
        {
            _health?.RecordSuccess(sourceId, latencyMs);
        }
    }

    private void RecordFailure(string sourceId, ArtifactKind kind)
    {
        _breaker.RecordFailure(sourceId, kind);
        if (!string.Equals(sourceId, "original", StringComparison.OrdinalIgnoreCase))
        {
            _health?.RecordFailure(sourceId);
        }
    }

    internal static string Describe(Exception? exception) => exception switch
    {
        null => "unknown error",
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status} {status}",
        TaskCanceledException => "request timed out",
        _ => exception.Message,
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

/// <summary>Raised when a downloaded artifact does not match its expected hash.</summary>
public sealed class HashMismatchException : Exception
{
    public HashMismatchException(string message) : base(message)
    {
    }
}

/// <summary>Raised when no source could serve an artifact.</summary>
public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
