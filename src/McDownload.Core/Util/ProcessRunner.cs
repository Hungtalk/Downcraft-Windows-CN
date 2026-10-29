using System.Diagnostics;
using System.Text;

namespace McDownload.Core.Util;

/// <summary>Captured result of an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, TimeSpan Duration)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    public string Combined => string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>Runs child processes (Java, installer jars) with streamed output and a timeout.</summary>
public static class ProcessRunner
{
    private const int TailLines = 200;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var started = Stopwatch.GetTimestamp();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程 {fileName}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = PumpAsync(process.StandardOutput, stdout, onLine, timeoutCts.Token);
        var stderrTask = PumpAsync(process.StandardError, stderr, onLine, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(started);

        return new ProcessResult(
            process.HasExited ? process.ExitCode : -1,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            duration);

        static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine, CancellationToken token)
        {
            try
            {
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    lock (sink)
                    {
                        sink.AppendLine(line);
                        if (sink.Length > 64 * 1024)
                        {
                            sink.Remove(0, sink.Length - TailLines * 120);
                        }
                    }

                    onLine?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Runs a process and returns the full combined output, used for <c>java -version</c>.</summary>
    public static async Task<ProcessResult> CaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunAsync(
                fileName,
                arguments,
                workingDirectory: null,
                onLine: null,
                timeout ?? TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false, TimeSpan.Zero);
        }
    }
}
