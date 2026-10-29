using System.Reflection;
using System.Text;
using McDownload.Core.Install;

namespace McDownload.Cli;

internal static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitFailed = 1;
    internal const int ExitUsage = 2;

    private static async Task<int> Main(string[] args)
    {
        TryUseUtf8Console();

        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            Commands.PrintHelp();
            return ExitOk;
        }

        if (args[0] is "version" or "--version" or "-V")
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            Console.WriteLine($"mcdl {version}  ({McDownload.Core.Native.NativeStatus.Describe()})");
            return ExitOk;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.WriteLine();
            Console.WriteLine("正在取消…（再次按 Ctrl+C 可强制退出）");
            cts.Cancel();
        };

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "install" or "i" => await Commands.InstallAsync(new ArgParser(args[1..]), cts.Token),
                "verify" => await Commands.VerifyAsync(new ArgParser(args[1..]), cts.Token),
                "list" or "versions" => await Commands.ListAsync(new ArgParser(args[1..]), cts.Token),
                "loaders" => await Commands.LoadersAsync(new ArgParser(args[1..]), cts.Token),
                "sources" => await Commands.SourcesAsync(new ArgParser(args[1..]), cts.Token),
                "java" => await Commands.JavaAsync(new ArgParser(args[1..]), cts.Token),
                "doctor" => await Commands.DoctorAsync(new ArgParser(args[1..]), cts.Token),
                _ => Unknown(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消。已下载的文件会保留，下次运行会继续。");
            return 130;
        }
        catch (InstallFailedException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[失败] {ex.Message}");
            foreach (var detail in ex.Details.Take(12))
            {
                Console.Error.WriteLine($"       {detail}");
            }

            return ExitFailed;
        }
        catch (McDownload.Core.Net.DownloadFailedException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[失败] 下载失败：{ex.Message}");
            Console.Error.WriteLine("       可以用 “mcdl sources test” 检查下载源，或加上 --verbose 查看细节。");
            return ExitFailed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[失败] {ex.Message}");
            return ExitFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[失败] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("       这是一个未预期的错误，请附上上面的信息反馈。");
            return ExitFailed;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        Console.Error.WriteLine("运行 “mcdl --help” 查看用法。");
        return ExitUsage;
    }

    private static void TryUseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
