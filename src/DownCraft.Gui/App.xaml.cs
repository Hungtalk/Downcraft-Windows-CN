using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DownCraft.Gui;

/// <summary>Application entry point with crash reporting so a UI failure is never silent.</summary>
public partial class App : Application
{
    private static string? _resolvedCrashLogPath;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception, "AppDomain");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception, "TaskScheduler");
            args.SetObserved();
        };
    }

    /// <summary>Crash log location; the first writable candidate wins.</summary>
    public static string CrashLogPath => _resolvedCrashLogPath ?? CandidateLogPaths()[0];

    private static string[] CandidateLogPaths()
    {
        var candidates = new List<string>();
        var overridden = Environment.GetEnvironmentVariable("DOWNCRAFT_GUI_LOG");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            candidates.Add(overridden);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "DownCraft", "logs", "gui-crash.log"));
        }

        try
        {
            candidates.Add(Path.Combine(Path.GetTempPath(), "DownCraft-gui-crash.log"));
        }
        catch (IOException)
        {
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "gui-crash.log"));
        return candidates.ToArray();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrashLog(e.Exception, "Dispatcher");
        e.Handled = true;

        MessageBox.Show(
            "界面出现了一个未处理的错误，已写入日志：\n" + (path ?? "（日志写入失败）") + "\n\n" + e.Exception.Message,
            "DownCraft",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /// <summary>Appends a crash report; returns the path that was written, or null.</summary>
    private static string? WriteCrashLog(Exception? exception, string source)
    {
        if (exception is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception.GetType().FullName}: {exception.Message}");
        builder.AppendLine(exception.StackTrace);
        if (exception.InnerException is { } inner)
        {
            builder.AppendLine("--- inner ---");
            builder.AppendLine(inner.ToString());
        }

        builder.AppendLine(new string('-', 80));
        var text = builder.ToString();

        foreach (var candidate in CandidateLogPaths())
        {
            try
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(candidate, text, Encoding.UTF8);
                _resolvedCrashLogPath = candidate;
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }
}
