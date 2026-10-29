using System.Globalization;
using System.Text.Json;
using McDownload.Core;
using McDownload.Core.Diagnostics;
using McDownload.Core.Install;
using McDownload.Core.Java;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;
using McDownload.Core.Util;

namespace McDownload.Cli;

/// <summary>All CLI commands. Each returns a process exit code.</summary>
internal static class Commands
{
    /// <summary>Options that never take a value, so <c>--flag</c> followed by a positional works.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "no-assets", "priority", "no-probe", "no-profile", "java", "no-java", "refresh",
        "no-installer", "verbose", "v", "installed", "all", "json", "help", "test",
        "list", "detect", "release", "snapshot", "yes", "y", "extract-only",
    };

    // ------------------------------------------------------------------ install / verify

    public static async Task<int> InstallAsync(ArgParser parser, CancellationToken cancellationToken)
        => await RunInstallAsync(parser, verifyOnly: false, cancellationToken);

    public static async Task<int> VerifyAsync(ArgParser parser, CancellationToken cancellationToken)
        => await RunInstallAsync(parser, verifyOnly: true, cancellationToken);

    private static async Task<int> RunInstallAsync(ArgParser parser, bool verifyOnly, CancellationToken cancellationToken)
    {
        var reporter = CreateReporter(parser);
        var gameDir = ResolveGameDir(parser.Value("dir", "d", "minecraft-dir"));
        var catalog = SourceCatalog.LoadEffective(parser.Value("sources-file"), out var catalogWarning);
        if (catalogWarning is not null)
        {
            reporter.Warn(catalogWarning);
        }

        var loader = ParseLoader(parser.Value("loader", "l", "modloader"));
        var loaderVersion = parser.Value("loader-version", "lv", "loaderVersion");
        var version = parser.Positionals.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            var catalogService = new MinecraftCatalog(catalog);
            var manifest = await catalogService.GetManifestAsync(null, false, reporter, cancellationToken);
            version = manifest.Latest.Release
                ?? throw new InstallFailedException("无法从版本清单确定最新正式版，请显式指定版本号");
            reporter.Info($"未指定版本，使用最新正式版 {version}");
        }

        var loaderName = loader == LoaderKind.Vanilla ? "原版" : loader.ToString();
        reporter.Info(verifyOnly
            ? $"校验 {version}（{loaderName}）于 {gameDir}"
            : $"安装 {version}（{loaderName}）到 {gameDir}");

        var options = new InstallOptions
        {
            GameDir = gameDir,
            MinecraftVersion = version!,
            Loader = loader,
            LoaderVersion = loaderVersion,
            DownloadAssets = !parser.Has("no-assets") && !verifyOnly,
            MaxParallel = Math.Clamp(parser.IntValue(16, "parallel", "j", "threads"), 1, 128),
            SourceOrder = parser.ListValue("source", "s", "mirror"),
            PreferSourcePriority = parser.Has("priority"),
            ProbeSources = !parser.Has("no-probe") && !verifyOnly,
            CreateLauncherProfile = !parser.Has("no-profile") && !verifyOnly,
            EnsureJava = loader != LoaderKind.Vanilla ? !parser.Has("no-java") : parser.Has("java"),
            Refresh = parser.Has("refresh"),
            VerifyOnly = verifyOnly,
            JavaPath = parser.Value("java-path"),
            RunLoaderInstaller = !parser.Has("no-installer") && !parser.Has("extract-only"),
            ProfileName = parser.Value("profile-name"),
        };

        var installer = new MinecraftInstaller(catalog);
        var result = await installer.InstallAsync(options, reporter, cancellationToken);

        PrintResult(result, verifyOnly);
        return result.Success ? Program.ExitOk : Program.ExitFailed;
    }

    private static void PrintResult(InstallResult result, bool verifyOnly)
    {
        Console.WriteLine();
        Console.WriteLine(verifyOnly ? "———— 校验结果 ————" : "———— 安装结果 ————");
        WriteRow("版本 ID", result.VersionId);
        if (!string.IsNullOrWhiteSpace(result.LoaderVersion))
        {
            WriteRow("Loader", result.LoaderVersion!);
        }

        WriteRow("游戏目录", result.GameDir);
        WriteRow("启动配置", result.VersionJsonPath ?? "-");
        if (result.ClientJarPath is not null)
        {
            WriteRow("客户端 jar", result.ClientJarPath);
        }

        WriteRow("库 / 资源", $"{result.LibraryCount} 个库 / {result.AssetCount} 个资源（natives {result.NativesCount} 个文件）");
        WriteRow(
            verifyOnly ? "文件状态" : "本次下载",
            verifyOnly
                ? $"正常 {result.Skipped} 个，缺失或损坏 {result.Failed} 个"
                : $"下载 {result.Downloaded} 个，跳过 {result.Skipped} 个，失败 {result.Failed} 个，共 {PathEx.FormatBytes(result.Bytes)}");
        WriteRow("用时", result.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("提示：");
            foreach (var warning in result.Warnings.Distinct())
            {
                Console.WriteLine($"  - {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine();
            foreach (var error in result.Errors.Distinct())
            {
                Console.WriteLine($"  ! {error}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.Success
            ? verifyOnly ? "[完成] 所有文件校验通过。" : "[完成] 现在可以在启动器里选择这个版本启动了。"
            : "[未完成] 存在失败项，请查看上面的提示。");
    }

    private static void WriteRow(string label, string value)
    {
        Console.WriteLine($"  {label,-10} {value}");
    }

    // ------------------------------------------------------------------ list

    public static async Task<int> ListAsync(ArgParser parser, CancellationToken cancellationToken)
    {
        var reporter = CreateReporter(parser);
        var gameDir = ResolveGameDir(parser.Value("dir", "d"));

        if (parser.Has("installed"))
        {
            return ListInstalled(gameDir);
        }

        var catalog = SourceCatalog.LoadEffective(parser.Value("sources-file"), out _);
        var service = new MinecraftCatalog(catalog);
        var manifest = await service.GetManifestAsync(null, parser.Has("refresh"), reporter, cancellationToken);

        var type = parser.Has("all") ? "all" : parser.Has("snapshot") ? "snapshot" : "release";
        var search = parser.Value("search", "q");
        var limit = parser.IntValue(20, "limit", "n");

        var versions = manifest.Versions
            .Where(v => type == "all" || string.Equals(v.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(v => search is null || v.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(limit <= 0 ? int.MaxValue : limit)
            .ToList();

        Console.WriteLine($"版本清单共 {manifest.Versions.Count} 个版本（显示 {versions.Count} 个，类型 {type}）");
        Console.WriteLine($"{"版本",-26}{"类型",-12}{"发布时间",-22}");
        foreach (var version in versions)
        {
            var marker = string.Equals(version.Id, manifest.Latest.Release, StringComparison.OrdinalIgnoreCase) ? " ★最新" : string.Empty;
            var released = version.ReleaseTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
            Console.WriteLine($"{version.Id,-26}{(version.Type + marker),-12}{released,-22}");
        }

        return Program.ExitOk;
    }

    private static int ListInstalled(string gameDir)
    {
        var paths = new GamePaths(gameDir);
        if (!Directory.Exists(paths.VersionsDir))
        {
            Console.WriteLine($"目录不存在或还没有安装任何版本：{paths.VersionsDir}");
            return Program.ExitOk;
        }

        var rows = new List<(string Id, string Type, string? Parent, long Size, DateTime When)>();
        foreach (var directory in Directory.EnumerateDirectories(paths.VersionsDir))
        {
            var id = Path.GetFileName(directory);
            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            string? parent = null;
            var type = "?";
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
                if (document.RootElement.TryGetProperty("inheritsFrom", out var inherits) && inherits.ValueKind == JsonValueKind.String)
                {
                    parent = inherits.GetString();
                }

                if (document.RootElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                {
                    type = typeElement.GetString() ?? type;
                }
            }
            catch (JsonException)
            {
            }

            var jarPath = Path.Combine(directory, id + ".jar");
            var size = File.Exists(jarPath) ? new FileInfo(jarPath).Length : 0;
            rows.Add((id, type, parent, size, Directory.GetLastWriteTime(directory)));
        }

        if (rows.Count == 0)
        {
            Console.WriteLine($"还没有安装任何版本：{paths.VersionsDir}");
            return Program.ExitOk;
        }

        Console.WriteLine($"已安装 {rows.Count} 个版本（{paths.VersionsDir}）");
        Console.WriteLine($"{"版本",-40}{"类型",-10}{"继承",-24}{"jar 大小",-12}");
        foreach (var row in rows.OrderByDescending(r => r.When))
        {
            var jarSize = row.Size > 0 ? PathEx.FormatBytes(row.Size) : "-";
            Console.WriteLine($"{row.Id,-40}{row.Type,-10}{row.Parent ?? "-",-24}{jarSize,-12}");
        }

        return Program.ExitOk;
    }

    // ------------------------------------------------------------------ loaders

    public static async Task<int> LoadersAsync(ArgParser parser, CancellationToken cancellationToken)
    {
        var reporter = CreateReporter(parser);
        var catalog = SourceCatalog.LoadEffective(parser.Value("sources-file"), out _);
        var loader = ParseLoader(parser.Value("kind", "loader") ?? "fabric");
        if (loader == LoaderKind.Vanilla)
        {
            Console.Error.WriteLine("请用 --kind fabric|forge|neoforge|quilt 指定 loader。");
            return Program.ExitUsage;
        }

        var version = parser.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            var manifest = await new MinecraftCatalog(catalog).GetManifestAsync(null, false, reporter, cancellationToken);
            version = manifest.Latest.Release ?? throw new InstallFailedException("无法确定最新版本");
            reporter.Info($"未指定游戏版本，使用最新正式版 {version}");
        }

        IReadOnlyList<string> versions;
        try
        {
            versions = await new MinecraftCatalog(catalog)
                .GetLoaderVersionsAsync(loader, version!, reporter, cancellationToken);
        }
        catch (DownloadFailedException ex)
        {
            Console.Error.WriteLine($"[失败] 无法获取 {loader} 版本列表：{ex.Message}");
            return Program.ExitFailed;
        }

        if (versions.Count == 0)
        {
            Console.WriteLine($"{loader} 没有为 Minecraft {version} 提供版本。");
            return Program.ExitOk;
        }

        Console.WriteLine($"{loader} 可用于 Minecraft {version} 的版本共 {versions.Count} 个（最新的在前）：");
        foreach (var item in versions.Take(30))
        {
            Console.WriteLine($"  {item}");
        }

        if (versions.Count > 30)
        {
            Console.WriteLine($"  … 其余 {versions.Count - 30} 个省略");
        }

        Console.WriteLine();
        Console.WriteLine($"安装示例： mcdl install {version} --loader {loader.ToString().ToLowerInvariant()} --loader-version {versions[0]}");
        return Program.ExitOk;
    }

    // ------------------------------------------------------------------ sources

    public static async Task<int> SourcesAsync(ArgParser parser, CancellationToken cancellationToken)
    {
        var reporter = CreateReporter(parser);
        var sub = parser.Positionals.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var path = SourceCatalog.UserConfigPath;

        switch (sub)
        {
            case "list":
            {
                var catalog = SourceCatalog.LoadEffective();
                var health = SourceHealthStore.Load(
                    new GamePaths(ResolveGameDir(parser.Value("dir", "d"))).SourceHealthFile);
                Console.WriteLine($"{"ID",-22}{"名称",-34}{"区域",-8}{"优先级",-8}{"状态",-8}{"延迟",-10}");
                foreach (var source in catalog.All)
                {
                    var latency = health.LatencyMs(source.Id);
                    var latencyText = latency is null ? "-" : $"{latency} ms";
                    var state = source.Enabled ? "启用" : "禁用";
                    Console.WriteLine($"{source.Id,-22}{Trim(source.DisplayName, 32),-34}{source.Region,-8}{source.Priority,-8}{state,-8}{latencyText,-10}");
                }

                Console.WriteLine();
                Console.WriteLine($"用户配置：{path}（{(File.Exists(path) ? "存在" : "未创建，当前使用内置配置")}）");
                return Program.ExitOk;
            }

            case "test":
            {
                var catalog = SourceCatalog.LoadEffective();
                Console.WriteLine("正在测速所有启用的下载源…");
                var results = await new MinecraftCatalog(catalog).ProbeAsync(reporter, cancellationToken);
                foreach (var result in results)
                {
                    var status = result.Ok ? $"{result.LatencyMs,6} ms" : $"不可用：{result.Error}";
                    Console.WriteLine($"  {result.DisplayName,-34}{status}");
                }

                return Program.ExitOk;
            }

            case "enable":
            case "disable":
            {
                var id = parser.Positionals.Skip(1).FirstOrDefault() ?? parser.Value("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    Console.Error.WriteLine("用法： mcdl sources enable|disable <source-id>");
                    return Program.ExitUsage;
                }

                var catalog = SourceCatalog.LoadEffective();
                var target = catalog.All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    Console.Error.WriteLine($"没有名为 {id} 的下载源。可用：{string.Join(", ", catalog.All.Select(s => s.Id))}");
                    return Program.ExitUsage;
                }

                var updated = catalog.All.Select(s => s.Clone()).ToList();
                foreach (var source in updated.Where(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    source.Enabled = sub == "enable";
                }

                new SourceCatalog(updated).Save(path);
                Console.WriteLine($"{target.DisplayName} 已{(sub == "enable" ? "启用" : "禁用")}，写入 {path}");
                return Program.ExitOk;
            }

            case "export":
            {
                var destination = parser.Value("out", "o") ?? path;
                PathEx.EnsureParent(destination);
                SourceCatalog.LoadDefault().Save(destination);
                Console.WriteLine($"已导出内置下载源到 {destination}，可编辑后用 --sources-file 指定。");
                return Program.ExitOk;
            }

            case "path":
                Console.WriteLine(path);
                return Program.ExitOk;

            default:
                Console.Error.WriteLine($"未知子命令：{sub}（可用：list、test、enable、disable、export、path）");
                return Program.ExitUsage;
        }
    }

    // ------------------------------------------------------------------ java

    public static async Task<int> JavaAsync(ArgParser parser, CancellationToken cancellationToken)
    {
        var reporter = CreateReporter(parser);
        var gameDir = ResolveGameDir(parser.Value("dir", "d"));
        var paths = new GamePaths(gameDir);
        paths.EnsureLayout();

        var catalog = SourceCatalog.LoadEffective(parser.Value("sources-file"), out _);
        var pump = new ProgressPump(reporter);
        using var http = McdHttp.Create(8, "McDownload/1.0");
        var router = new SourceRouter(catalog.Enabled, health: null, preferPriorityOrder: true);
        using var downloader = new Downloader(router, new DownloaderOptions(), reporter, pump, null, http);
        var manager = new JavaRuntimeManager(paths, downloader, reporter);

        if (parser.Has("list"))
        {
            var components = await manager.ListComponentsAsync(cancellationToken);
            Console.WriteLine($"Mojang 提供的 Java 运行时（{PlatformContext.Current.RuntimePlatformKey}）：");
            foreach (var (name, major, released) in components)
            {
                Console.WriteLine($"  {name,-26} Java {major,-3} {released}");
            }

            return Program.ExitOk;
        }

        if (parser.Has("detect") || !parser.Has("install"))
        {
            var found = await manager.DetectAllAsync(8, cancellationToken);
            if (found.Count == 0)
            {
                Console.WriteLine("没有检测到可用的 Java（>=8）。可以运行 “mcdl java --install 21” 自动下载。");
                return Program.ExitOk;
            }

            Console.WriteLine("检测到的 Java：");
            foreach (var java in found)
            {
                Console.WriteLine($"  Java {java.MajorVersion,-3} {java.Path}");
            }

            return Program.ExitOk;
        }

        var requested = parser.IntValue(21, "install");
        var installation = await manager.ProvisionAsync(null, requested, cancellationToken);
        Console.WriteLine($"Java {installation.MajorVersion} 已就绪：{installation.Path}");
        return Program.ExitOk;
    }

    // ------------------------------------------------------------------ doctor

    public static async Task<int> DoctorAsync(ArgParser parser, CancellationToken cancellationToken)
    {
        var reporter = CreateReporter(parser);
        var gameDir = ResolveGameDir(parser.Value("dir", "d"));
        var paths = new GamePaths(gameDir);

        Console.WriteLine("———— 环境检查 ————");
        Console.WriteLine($"  操作系统   : {Environment.OSVersion.VersionString} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"  .NET       : {Environment.Version}");
        Console.WriteLine($"  进程架构   : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"  原生模块   : {McDownload.Core.Native.NativeStatus.Describe()}");
        Console.WriteLine($"  游戏目录   : {paths.Root}（{(Directory.Exists(paths.Root) ? "存在" : "不存在")}）");

        var free = paths.GetFreeSpace();
        Console.WriteLine($"  可用空间   : {(free >= 0 ? PathEx.FormatBytes(free) : "未知")}");
        if (free >= 0 && free < 3L * 1024 * 1024 * 1024)
        {
            Console.WriteLine("     提示    : 空间不足 3 GB，现代版本（含资源文件）建议预留 3 GB 以上");
        }

        Console.WriteLine();
        Console.WriteLine("———— 下载源 ————");
        var catalog = SourceCatalog.LoadEffective(parser.Value("sources-file"), out var warning);
        if (warning is not null)
        {
            Console.WriteLine($"  ! {warning}");
        }

        var results = await new MinecraftCatalog(catalog).ProbeAsync(reporter, cancellationToken);
        foreach (var result in results)
        {
            Console.WriteLine($"  {(result.Ok ? "[可用]" : "[不可用]"),-9}{result.DisplayName,-32}{(result.Ok ? result.LatencyMs + " ms" : result.Error)}");
        }

        Console.WriteLine();
        Console.WriteLine("———— Java ————");
        var pump = new ProgressPump(reporter);
        using var http = McdHttp.Create(8, "McDownload/1.0");
        var router = new SourceRouter(catalog.Enabled, health: null, preferPriorityOrder: true);
        using var downloader = new Downloader(router, new DownloaderOptions(), reporter, pump, null, http);
        var java = await new JavaRuntimeManager(paths, downloader, reporter).DetectAllAsync(8, cancellationToken);
        if (java.Count == 0)
        {
            Console.WriteLine("  未检测到 Java（运行 mcdl install ... --loader forge 会自动下载 Mojang Java 运行时）");
        }
        else
        {
            foreach (var item in java)
            {
                Console.WriteLine($"  Java {item.MajorVersion,-3} {item.Path}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("———— 本地缓存 ————");
        var cacheFiles = Directory.Exists(paths.CacheDir) ? Directory.GetFiles(paths.CacheDir).Length : 0;
        Console.WriteLine($"  {paths.CacheDir}（{cacheFiles} 个文件）");
        Console.WriteLine();
        Console.WriteLine("检查完成。");
        return Program.ExitOk;
    }

    // ------------------------------------------------------------------ helpers

    private static ConsoleReporter CreateReporter(ArgParser parser) =>
        new(parser.Has("verbose", "v") ? LogLevel.Debug : LogLevel.Info);

    private static string ConfigDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "McDownload");

    private static string ResolveGameDir(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(appData) ? Environment.CurrentDirectory : appData, ".minecraft");
    }

    private static LoaderKind ParseLoader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LoaderKind.Vanilla;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "vanilla" or "none" or "原版" => LoaderKind.Vanilla,
            "fabric" => LoaderKind.Fabric,
            "quilt" => LoaderKind.Quilt,
            "forge" => LoaderKind.Forge,
            "neoforge" or "neo" or "neoforged" => LoaderKind.NeoForge,
            _ => throw new ArgumentException($"未知加载器：{value}（可用：vanilla、fabric、quilt、forge、neoforge）"),
        };
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public static void PrintHelp()
    {
        Console.WriteLine("""
            mcdl —— Minecraft 下载器（为不支持下载的第三方启动器准备文件）

            用法：
              mcdl install [版本] [选项]        下载原版 / 带 Mod 加载器的版本到 .minecraft
              mcdl verify  [版本] [选项]        校验已安装文件的完整性（不下载）
              mcdl list [--installed]           列出可下载版本，或列出已安装版本
              mcdl loaders [版本] --kind fabric  列出某加载器可用的版本
              mcdl sources [list|test|enable|disable|export|path]
              mcdl java [--detect|--list|--install <主版本>]
              mcdl doctor                       检查网络、下载源、Java 与磁盘空间

            安装选项：
              -d, --dir <目录>          目标 .minecraft 目录（默认 %APPDATA%\.minecraft）
              -l, --loader <类型>       vanilla | fabric | quilt | forge | neoforge
                  --loader-version <v>  指定加载器版本（默认取最新稳定/推荐版）
              -j, --parallel <N>        并发下载数（默认 16）
              -s, --source <id,id>      指定下载源顺序，如 bmclapi,mojang
                  --priority            按配置优先级排序而不是实测延迟
                  --no-probe            跳过下载源测速
                  --no-assets           不下载资源文件（离线包体时可用，游戏内会缺贴图）
                  --no-profile          不写入 launcher_profiles.json
                  --no-java             不自动下载 Java 运行时（Forge/NeoForge 需要）
                  --java-path <java.exe> 指定用哪个 Java 运行安装器
                  --no-installer        只放置加载器启动配置，不运行 Forge/NeoForge 安装器
                  --profile-name <名称> 启动器中的显示名称
                  --refresh             忽略本地缓存，重新拉取元数据
              -v, --verbose             输出调试日志

            示例：
              mcdl install 1.20.1
              mcdl install 1.20.1 --loader fabric
              mcdl install 1.21.1 --loader neoforge -d "D:\Games\.minecraft"
              mcdl install 1.20.1 --loader forge --loader-version 47.4.28 --source bmclapi,mojang
              mcdl verify 1.20.1 --loader fabric
              mcdl java --install 21
            """);
    }
}
