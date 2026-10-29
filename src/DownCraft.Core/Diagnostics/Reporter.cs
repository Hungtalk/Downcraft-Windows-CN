using System.Runtime.CompilerServices;

namespace DownCraft.Core.Diagnostics;

/// <summary>Severity of a diagnostic line emitted while installing.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>A single diagnostic line. <see cref="Scope"/> groups lines by phase.</summary>
public readonly record struct LogEntry(LogLevel Level, string Message, string? Scope = null, DateTimeOffset? Time = null);

/// <summary>Coarse phase of the install pipeline, used to label progress in the UI.</summary>
public enum InstallPhase
{
    Idle = 0,
    ResolvingSources,
    ReadingManifest,
    DownloadingClient,
    DownloadingLibraries,
    ExtractingNatives,
    DownloadingAssets,
    ResolvingLoader,
    InstallingLoader,
    ProvisioningJava,
    WritingProfiles,
    Verifying,
    Done,
}

/// <summary>
/// Snapshot of download progress. Reported at most a few times per second so that
/// a WPF dispatcher or console renderer is never flooded.
/// </summary>
public readonly record struct ProgressUpdate(
    InstallPhase Phase,
    string? CurrentItem,
    long BytesDone,
    long BytesTotal,
    int ItemsDone,
    int ItemsTotal,
    double BytesPerSecond)
{
    /// <summary>0..1 when the total is known, otherwise <c>null</c>.</summary>
    public double? Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : null;
}

/// <summary>Receives log lines and progress snapshots from the installer pipeline.</summary>
public interface IReporter
{
    void Log(LogEntry entry);

    void Progress(ProgressUpdate update);
}

/// <summary>Convenience base class: override only what you need.</summary>
public abstract class Reporter : IReporter
{
    public virtual void Log(LogEntry entry) => OnLog(entry);

    public virtual void Progress(ProgressUpdate update) => OnProgress(update);

    protected virtual void OnLog(LogEntry entry) { }

    protected virtual void OnProgress(ProgressUpdate update) { }
}

/// <summary>Reporter that forwards to delegates; used by the CLI and by tests.</summary>
public sealed class DelegateReporter : Reporter
{
    private readonly Action<LogEntry>? _log;
    private readonly Action<ProgressUpdate>? _progress;

    public DelegateReporter(Action<LogEntry>? log = null, Action<ProgressUpdate>? progress = null)
    {
        _log = log;
        _progress = progress;
    }

    protected override void OnLog(LogEntry entry) => _log?.Invoke(entry);

    protected override void OnProgress(ProgressUpdate update) => _progress?.Invoke(update);
}

/// <summary>Writes nothing. Default when no reporter is supplied.</summary>
public sealed class NullReporter : Reporter
{
    public static readonly NullReporter Instance = new();
}

/// <summary>Small helpers so call sites stay terse.</summary>
public static class ReporterExtensions
{
    public static void Info(this IReporter r, string message, string? scope = null) =>
        r.Log(new LogEntry(LogLevel.Info, message, scope));

    public static void Warn(this IReporter r, string message, string? scope = null) =>
        r.Log(new LogEntry(LogLevel.Warning, message, scope));

    public static void Error(this IReporter r, string message, string? scope = null) =>
        r.Log(new LogEntry(LogLevel.Error, message, scope));

    public static void Debug(this IReporter r, string message, string? scope = null) =>
        r.Log(new LogEntry(LogLevel.Debug, message, scope));
}

/// <summary>Throttles progress callbacks to a fixed interval and tracks the transfer rate.</summary>
public sealed class ProgressPump
{
    private readonly IReporter _reporter;
    private readonly TimeSpan _interval;
    private readonly long _startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _bytesDone;
    private long _bytesTotal;
    private int _itemsDone;
    private int _itemsTotal;
    private long _lastReportTimestamp;

    public ProgressPump(IReporter reporter, TimeSpan? interval = null)
    {
        _reporter = reporter;
        _interval = interval ?? TimeSpan.FromMilliseconds(120);
    }

    public InstallPhase Phase { get; set; } = InstallPhase.Idle;

    public string? CurrentItem { get; set; }

    public long BytesDone => Interlocked.Read(ref _bytesDone);

    public long BytesTotal => Interlocked.Read(ref _bytesTotal);

    public int ItemsDone => Volatile.Read(ref _itemsDone);

    public int ItemsTotal => Volatile.Read(ref _itemsTotal);

    public void AddTotal(long bytes, int items = 0)
    {
        if (bytes != 0)
        {
            Interlocked.Add(ref _bytesTotal, bytes);
        }

        if (items > 0)
        {
            Interlocked.Add(ref _itemsTotal, items);
        }
    }

    /// <summary>Adds transferred bytes. Negative values roll back a failed attempt.</summary>
    public void AddBytes(long bytes)
    {
        if (bytes != 0)
        {
            Interlocked.Add(ref _bytesDone, bytes);
        }
    }

    public void AddItems(int items)
    {
        if (items != 0)
        {
            Interlocked.Add(ref _itemsDone, items);
        }
    }

    public void AddDone(long bytes, int items = 1)
    {
        AddBytes(bytes);
        AddItems(items);
    }

    public void Report(bool force = false)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastReportTimestamp);
        if (!force && last != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(last, now) < _interval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReportTimestamp, now);
        var done = Interlocked.Read(ref _bytesDone);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_startTimestamp, now).TotalSeconds;
        var speed = elapsed > 0 ? done / elapsed : 0;

        _reporter.Progress(new ProgressUpdate(
            Phase,
            CurrentItem,
            done,
            Interlocked.Read(ref _bytesTotal),
            Volatile.Read(ref _itemsDone),
            Volatile.Read(ref _itemsTotal),
            speed));
    }
}
