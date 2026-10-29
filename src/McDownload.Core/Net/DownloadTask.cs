using System.Net;
using McDownload.Core.Sources;

namespace McDownload.Core.Net;

/// <summary>One file to place inside the game directory.</summary>
public sealed class DownloadTask
{
    /// <summary>Absolute destination path.</summary>
    public required string TargetPath { get; init; }

    public required ArtifactKind Kind { get; init; }

    /// <summary>Values used to expand mirror URL templates (version ids, maven path, hashes…).</summary>
    public required SourceContext Context { get; init; }

    /// <summary>Expected lower-case SHA-1; when set the file is verified and skipped if it matches.</summary>
    public string? Sha1 { get; init; }

    /// <summary>Expected byte size, used for resume decisions and progress totals.</summary>
    public long? Size { get; init; }

    /// <summary>Missing optional files (for example a logging config) do not fail the install.</summary>
    public bool Optional { get; init; }

    /// <summary>Readable name used in logs and progress.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>When false the file is only checked, never downloaded (integrity verification mode).</summary>
    public bool DownloadAllowed { get; init; } = true;

    public override string ToString() => DisplayName.Length > 0 ? DisplayName : Path.GetFileName(TargetPath);
}

/// <summary>Result of one <see cref="DownloadTask"/>.</summary>
public readonly record struct DownloadOutcome(
    bool Success,
    bool Skipped,
    long Bytes,
    string? Error,
    string? SourceId,
    string? Url)
{
    public static DownloadOutcome Skip(long bytes) => new(true, true, bytes, null, null, null);
}

/// <summary>Aggregate result of a download batch.</summary>
public sealed class DownloadBatchResult
{
    public int Total { get; set; }

    public int Downloaded { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public long Bytes { get; set; }

    public List<(DownloadTask Task, string Error)> Errors { get; } = [];

    public bool HasErrors => Failed > 0;
}

/// <summary>Knobs for the HTTP layer.</summary>
public sealed class DownloaderOptions
{
    /// <summary>Inflight requests per batch.</summary>
    public int MaxParallel { get; init; } = 16;

    /// <summary>Attempts per URL before rotating to the next mirror.</summary>
    public int RetriesPerUrl { get; init; } = 2;

    public string UserAgent { get; init; } = "McDownload/1.0 (+https://github.com/mcdownload)";

    /// <summary>Minimum time budget for a request, before the size-based extension is applied.</summary>
    public TimeSpan BaseTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Assumed minimum throughput when sizing a request timeout (bytes/second).</summary>
    public long MinimumBytesPerSecond { get; init; } = 64 * 1024;

    /// <summary>When true (default) the SHA-1 of every downloaded file is verified.</summary>
    public bool VerifyHashes { get; init; } = true;
}

/// <summary>Classifies HTTP failures so the caller knows whether the same URL is worth retrying.</summary>
internal static class HttpFailure
{
    public static bool IsTransient(Exception exception, out HttpStatusCode? status)
    {
        status = null;
        switch (exception)
        {
            case HttpRequestException { StatusCode: { } code }:
                status = code;
                // 403/404/410 mean "this mirror does not have it" — another site may.
                return (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            case HttpRequestException:
                return true;
            case TaskCanceledException:
                return true;
            case IOException:
                return true;
            default:
                return false;
        }
    }
}
