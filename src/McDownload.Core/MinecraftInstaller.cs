using System.Diagnostics;
using McDownload.Core.Diagnostics;
using McDownload.Core.Install;
using McDownload.Core.Java;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;
using McDownload.Core.Util;

namespace McDownload.Core;

/// <summary>
/// High level entry point: installs a Minecraft version (optionally with a mod loader) into a
/// standard <c>.minecraft</c> folder that any launcher can pick up.
/// </summary>
public sealed class MinecraftInstaller
{
    private readonly SourceCatalog _catalog;

    public MinecraftInstaller(SourceCatalog? catalog = null)
    {
        _catalog = catalog ?? SourceCatalog.LoadEffective();
    }

    /// <summary>The download sites currently in use.</summary>
    public SourceCatalog Sources => _catalog;

    /// <summary>Runs the whole pipeline and reports progress through <paramref name="reporter"/>.</summary>
    public async Task<InstallResult> InstallAsync(
        InstallOptions options,
        IReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        reporter ??= NullReporter.Instance;
        var stopwatch = Stopwatch.StartNew();
        var paths = new GamePaths(options.GameDir);
        try
        {
            paths.EnsureLayout();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallFailedException(
                $"无法在 {paths.Root} 创建目录（{ex.Message}）",
                [
                    "可能原因：目录只读、被安全软件拦截，或没有写入权限。",
                    "请换一个可写目录，例如 D:\\Games\\.minecraft，然后重试。",
                ]);
        }

        var result = new InstallResult { GameDir = paths.Root };
        var catalog = options.SourceOrder.Count > 0 ? _catalog.WithOrder(options.SourceOrder) : _catalog;
        var health = SourceHealthStore.Load(paths.SourceHealthFile);
        var pump = new ProgressPump(reporter);
        using var http = McdHttp.Create(options.MaxParallel, "McDownload/1.0");

        if (options.ProbeSources && !options.PreferSourcePriority && !options.VerifyOnly)
        {
            pump.Phase = InstallPhase.ResolvingSources;
            reporter.Info("正在测速下载源…", "sources");
            var probes = await SourceProber.ProbeAsync(catalog.Enabled, http, health, reporter, cancellationToken)
                .ConfigureAwait(false);
            var summary = string.Join("，", probes
                .Take(5)
                .Select(p => $"{p.DisplayName} {(p.Ok ? p.LatencyMs.ToString() + " ms" : "不可用")}"));
            reporter.Info($"下载源测速：{summary}", "sources");
        }

        var router = new SourceRouter(catalog.Enabled, health, options.PreferSourcePriority);
        using var downloader = new Downloader(
            router,
            new DownloaderOptions
            {
                MaxParallel = options.MaxParallel,
                VerifyHashes = !options.VerifyOnly,
            },
            reporter,
            pump,
            health,
            http);

        var manifestService = new VersionManifestService(downloader, reporter);
        pump.Phase = InstallPhase.ReadingManifest;

        var manifest = await manifestService
            .GetManifestAsync(paths.CacheDir, options.Refresh, cancellationToken)
            .ConfigureAwait(false);

        var summaryEntry = manifestService.FindVersion(manifest, options.MinecraftVersion)
            ?? throw new InstallFailedException(
                $"版本清单中没有 “{options.MinecraftVersion}”（最新正式版：{manifest.Latest.Release}）");

        var vanillaMeta = await manifestService
            .LoadVersionAsync(paths, summaryEntry.Id, summaryEntry, options.Refresh, cancellationToken)
            .ConfigureAwait(false);

        var vanilla = new VanillaInstaller(downloader, manifestService, paths, reporter, pump);
        var vanillaResult = await vanilla.InstallAsync(vanillaMeta, options, cancellationToken).ConfigureAwait(false);

        result.VersionId = vanillaResult.VersionId;
        result.VersionJsonPath = vanillaResult.VersionJsonPath;
        result.ClientJarPath = vanillaResult.ClientJarPath;
        result.LibraryCount = vanillaResult.LibraryCount;
        result.AssetCount = vanillaResult.AssetCount;
        result.NativesCount = vanillaResult.NativeCount;
        result.Warnings.AddRange(vanillaResult.Warnings);
        result.Downloaded += vanillaResult.Downloads.Downloaded;
        result.Skipped += vanillaResult.Downloads.Skipped;
        result.Failed += vanillaResult.Downloads.Failed;
        result.Bytes += vanillaResult.Downloads.Bytes;

        var javaManager = new JavaRuntimeManager(paths, downloader, reporter);

        if (options.Loader != LoaderKind.Vanilla)
        {
            pump.Phase = InstallPhase.InstallingLoader;
            var loaderInstaller = new LoaderInstaller(paths, downloader, reporter, javaManager);
            var loaderResult = await loaderInstaller
                .InstallAsync(options.Loader, vanillaResult.VersionId, vanillaMeta, options.LoaderVersion, options, cancellationToken)
                .ConfigureAwait(false);

            result.VersionId = loaderResult.VersionId;
            result.LoaderVersion = loaderResult.LoaderVersion;
            result.Warnings.AddRange(loaderResult.Warnings);
            if (loaderResult.Downloads is { } loaderDownloads)
            {
                result.Downloaded += loaderDownloads.Downloaded;
                result.Skipped += loaderDownloads.Skipped;
                result.Failed += loaderDownloads.Failed;
                result.Bytes += loaderDownloads.Bytes;
            }
        }
        else if (options.EnsureJava)
        {
            var java = await javaManager
                .EnsureAsync(
                    vanillaMeta.JavaVersion?.Component,
                    vanillaMeta.JavaVersion?.MajorVersion ?? 8,
                    allowProvision: true,
                    options.JavaPath,
                    cancellationToken)
                .ConfigureAwait(false);
            result.JavaPath = java?.Path;
        }

        if (options.CreateLauncherProfile && !options.VerifyOnly)
        {
            pump.Phase = InstallPhase.WritingProfiles;
            LauncherProfiles.Upsert(
                paths,
                result.VersionId,
                options.ProfileName ?? DescribeProfile(options.Loader, options.MinecraftVersion, result.LoaderVersion),
                icon: null,
                reporter);
        }

        result.VersionJsonPath = paths.VersionJsonPath(result.VersionId);
        if (File.Exists(paths.VersionJarPath(options.MinecraftVersion)))
        {
            result.ClientJarPath = paths.VersionJarPath(options.MinecraftVersion);
        }

        health.Save();
        pump.Phase = InstallPhase.Done;
        pump.Report(force: true);
        result.Elapsed = stopwatch.Elapsed;

        if (options.VerifyOnly && result.Failed > 0)
        {
            result.Errors.Add($"{result.Failed} 个文件缺失或校验失败");
        }

        reporter.Info(
            options.VerifyOnly
                ? $"校验完成：{result.Skipped} 个文件正常，{result.Failed} 个缺失或损坏（{stopwatch.Elapsed:hh\\:mm\\:ss}）"
                : $"安装完成：{result.VersionId}，下载 {result.Downloaded} 个文件（{PathEx.FormatBytes(result.Bytes)}），跳过 {result.Skipped} 个（{stopwatch.Elapsed:hh\\:mm\\:ss}）",
            "done");

        return result;
    }

    private static string DescribeProfile(LoaderKind loader, string mcVersion, string? loaderVersion)
    {
        var loaderName = loader switch
        {
            LoaderKind.Forge => "Forge",
            LoaderKind.NeoForge => "NeoForge",
            LoaderKind.Fabric => "Fabric",
            LoaderKind.Quilt => "Quilt",
            _ => null,
        };

        if (loaderName is null)
        {
            return $"Minecraft {mcVersion}";
        }

        var versionPart = string.IsNullOrWhiteSpace(loaderVersion) ? string.Empty : $" {loaderVersion}";
        return $"Minecraft {mcVersion} {loaderName}{versionPart}";
    }
}
