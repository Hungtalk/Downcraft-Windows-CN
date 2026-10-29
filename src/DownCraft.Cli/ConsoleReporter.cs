using DownCraft.Core.Diagnostics;
using DownCraft.Core.Util;

namespace DownCraft.Cli;

/// <summary>Console reporter: coloured log lines plus a single live progress line.</summary>
internal sealed class ConsoleReporter : Reporter
{
    private readonly LogLevel _threshold;
    private readonly object _gate = new();
    private readonly bool _live;
    private int _progressWidth;
    private bool _progressVisible;

    public ConsoleReporter(LogLevel threshold = LogLevel.Info, bool liveProgress = true)
    {
        _threshold = threshold;
        _live = liveProgress && !Console.IsOutputRedirected &&
                Environment.GetEnvironmentVariable("DOWNCRAFT_NO_PROGRESS") is null;
    }

    public override void Log(LogEntry entry)
    {
        if (entry.Level < _threshold)
        {
            return;
        }

        lock (_gate)
        {
            ClearProgressLine();
            var (color, prefix) = entry.Level switch
            {
                LogLevel.Error => (ConsoleColor.Red, "错误"),
                LogLevel.Warning => (ConsoleColor.Yellow, "警告"),
                LogLevel.Debug or LogLevel.Trace => (ConsoleColor.DarkGray, "调试"),
                _ => (ConsoleColor.Gray, "信息"),
            };

            var scope = string.IsNullOrWhiteSpace(entry.Scope) ? string.Empty : $"[{entry.Scope}] ";
            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine($"{prefix} {scope}{entry.Message}");
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }

    protected override void OnProgress(ProgressUpdate update)
    {
        if (!_live)
        {
            return;
        }

        lock (_gate)
        {
            var line = Format(update);
            var padding = Math.Max(0, _progressWidth - line.Length);
            Console.Write('\r');
            Console.Write(line + new string(' ', padding));
            _progressWidth = line.Length;
            _progressVisible = true;
        }
    }

    private void ClearProgressLine()
    {
        if (!_progressVisible)
        {
            return;
        }

        Console.Write('\r' + new string(' ', Math.Max(0, _progressWidth)) + '\r');
        _progressVisible = false;
        _progressWidth = 0;
    }

    private static string Format(ProgressUpdate update)
    {
        var phase = update.Phase switch
        {
            InstallPhase.ResolvingSources => "测速",
            InstallPhase.ReadingManifest => "清单",
            InstallPhase.DownloadingClient => "客户端",
            InstallPhase.DownloadingLibraries => "库文件",
            InstallPhase.ExtractingNatives => "natives",
            InstallPhase.DownloadingAssets => "资源",
            InstallPhase.ResolvingLoader => "Loader",
            InstallPhase.InstallingLoader => "安装Loader",
            InstallPhase.ProvisioningJava => "Java运行时",
            InstallPhase.WritingProfiles => "配置",
            InstallPhase.Verifying => "校验",
            InstallPhase.Done => "完成",
            _ => "处理中",
        };

        var items = update.ItemsTotal > 0 ? $"{update.ItemsDone}/{update.ItemsTotal}" : string.Empty;
        var bytes = update.BytesTotal > 0
            ? $"{PathEx.FormatBytes(update.BytesDone)}/{PathEx.FormatBytes(update.BytesTotal)}"
            : PathEx.FormatBytes(update.BytesDone);
        var percent = update.Fraction is { } fraction ? $" {fraction * 100,3:0}%" : string.Empty;
        var speed = update.BytesPerSecond > 0 ? $" {PathEx.FormatBytes((long)update.BytesPerSecond)}/s" : string.Empty;
        var item = string.IsNullOrWhiteSpace(update.CurrentItem) ? string.Empty : "  " + Trim(update.CurrentItem, 42);

        return $"  {phase,-8} {items,-11} {bytes}{percent}{speed}{item}";
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : "…" + value[^max..];
}
