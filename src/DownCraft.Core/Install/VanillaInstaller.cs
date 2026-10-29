using DownCraft.Core.Diagnostics;
using DownCraft.Core.Meta;
using DownCraft.Core.Net;
using DownCraft.Core.Sources;
using DownCraft.Core.Util;

namespace DownCraft.Core.Install;

/// <summary>Result of installing the vanilla part of a version.</summary>
public sealed record VanillaInstallResult(
    string VersionId,
    string VersionJsonPath,
    string? ClientJarPath,
    int LibraryCount,
    int NativeCount,
    int AssetCount,
    DownloadBatchResult Downloads,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Installs everything a launcher needs for a plain Minecraft version: <c>client.jar</c>,
/// <c>libraries</c> (with rules filtering and legacy natives), the asset index and its
/// hashed objects, and the logging configuration.
/// </summary>
public sealed class VanillaInstaller
{
    private readonly Downloader _downloader;
    private readonly VersionManifestService _manifest;
    private readonly GamePaths _paths;
    private readonly IReporter _reporter;
    private readonly ProgressPump _pump;

    public VanillaInstaller(
        Downloader downloader,
        VersionManifestService manifest,
        GamePaths paths,
        IReporter reporter,
        ProgressPump pump)
    {
        _downloader = downloader;
        _manifest = manifest;
        _paths = paths;
        _reporter = reporter;
        _pump = pump;
    }

    public async Task<VanillaInstallResult> InstallAsync(
        VersionMeta meta,
        InstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var versionId = string.IsNullOrWhiteSpace(meta.Id) ? options.MinecraftVersion : meta.Id;
        _paths.EnsureLayout();

        var plan = await BuildPlanAsync(meta, versionId, options, cancellationToken).ConfigureAwait(false);
        foreach (var warning in plan.Warnings)
        {
            _reporter.Warn(warning, versionId);
        }

        // Libraries, client jar and logging config first: the Forge/NeoForge installers need
        // versions/<id>/<id>.jar to exist before they run.
        var primary = plan.NonAssetFiles;
        var primaryResult = new DownloadBatchResult { Total = primary.Count };
        if (primary.Count > 0)
        {
            _reporter.Info($"开始下载 {versionId}：{primary.Count} 个文件", versionId);
            primaryResult = await _downloader
                .RunAsync(primary, InstallPhase.DownloadingLibraries, options.MaxParallel, cancellationToken)
                .ConfigureAwait(false);
        }

        var fatal = primaryResult.Errors
            .Where(e => !e.Task.Optional)
            .Select(e => $"{e.Task}: {e.Error}")
            .ToList();
        if (fatal.Count > 0 && !options.VerifyOnly)
        {
            throw new InstallFailedException($"{versionId} 的关键文件下载失败（{fatal.Count} 个）", fatal);
        }

        var assetResult = new DownloadBatchResult();
        var assetFiles = plan.AssetFiles;
        if (assetFiles.Count > 0)
        {
            var estimated = assetFiles.Sum(t => t.Size ?? 0);
            _reporter.Info($"开始下载资源文件：{assetFiles.Count} 个文件，约 {PathEx.FormatBytes(estimated)}", "assets");
            assetResult = await _downloader
                .RunAsync(assetFiles, InstallPhase.DownloadingAssets, options.MaxParallel, cancellationToken)
                .ConfigureAwait(false);

            if (assetResult.Failed > 0)
            {
                _reporter.Warn($"有 {assetResult.Failed} 个资源文件下载失败，游戏可能缺少贴图或声音", "assets");
            }
        }

        var nativesCount = ExtractNatives(plan);
        CopyLegacyAssets(plan);

        var clientPath = _paths.VersionJarPath(versionId);
        var jsonPath = _paths.VersionJsonPath(versionId);

        return new VanillaInstallResult(
            versionId,
            jsonPath,
            File.Exists(clientPath) ? clientPath : null,
            meta.Libraries.Count,
            nativesCount,
            assetFiles.Count,
            Merge(primaryResult, assetResult),
            plan.Warnings);
    }

    private static DownloadBatchResult Merge(DownloadBatchResult first, DownloadBatchResult second)
    {
        var merged = new DownloadBatchResult
        {
            Total = first.Total + second.Total,
            Downloaded = first.Downloaded + second.Downloaded,
            Skipped = first.Skipped + second.Skipped,
            Failed = first.Failed + second.Failed,
            Bytes = first.Bytes + second.Bytes,
        };
        merged.Errors.AddRange(first.Errors);
        merged.Errors.AddRange(second.Errors);
        return merged;
    }

    internal async Task<InstallPlan> BuildPlanAsync(
        VersionMeta meta,
        string versionId,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var plan = new InstallPlan();
        var platform = PlatformContext.Current;
        var allowDownload = !options.VerifyOnly;

        // ---- client.jar -----------------------------------------------------------
        var client = meta.Downloads?.Client;
        if (client is not null && !string.IsNullOrWhiteSpace(client.Url))
        {
            plan.Files.Add(new DownloadTask
            {
                TargetPath = _paths.VersionJarPath(versionId),
                Kind = ArtifactKind.ClientJar,
                Context = new SourceContext(
                    VersionId: versionId,
                    FileName: "client.jar",
                    Sha1: client.Sha1,
                    OriginalUrl: client.Url),
                Sha1 = client.Sha1,
                Size = client.Size,
                DownloadAllowed = allowDownload,
                DisplayName = $"{versionId} client.jar",
            });
        }
        else
        {
            plan.Warnings.Add($"版本 {versionId} 没有提供 client.jar 下载地址（可能为极早期版本或仅服务端版本）");
        }

        // ---- logging configuration ------------------------------------------------
        if (meta.Logging?.Client?.File is { } logging && !string.IsNullOrWhiteSpace(logging.Url) && !string.IsNullOrWhiteSpace(logging.Id))
        {
            plan.Files.Add(new DownloadTask
            {
                TargetPath = _paths.LoggingConfigPath(logging.Id),
                Kind = ArtifactKind.LoggingConfig,
                Context = new SourceContext(
                    VersionId: versionId,
                    FileName: logging.Id,
                    Sha1: logging.Sha1,
                    OriginalUrl: logging.Url),
                Sha1 = logging.Sha1,
                Size = logging.Size,
                Optional = true,
                DownloadAllowed = allowDownload,
                DisplayName = $"logging {logging.Id}",
            });
        }

        // ---- libraries ------------------------------------------------------------
        foreach (var library in meta.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, platform))
            {
                continue;
            }

            PlanLibrary(plan, library, platform, versionId, allowDownload);
        }

        // ---- assets ---------------------------------------------------------------
        if (options.DownloadAssets && meta.AssetIndex is { } index && !string.IsNullOrWhiteSpace(index.Url))
        {
            var assetIndex = await _manifest
                .LoadAssetIndexAsync(_paths, index, options.Refresh, cancellationToken)
                .ConfigureAwait(false);
            PlanAssets(plan, assetIndex, index.Id ?? "legacy", allowDownload);
        }
        else if (!options.DownloadAssets && meta.AssetIndex is not null)
        {
            plan.Warnings.Add("已按参数跳过资源文件下载（游戏内会缺少贴图与声音）");
        }

        return plan;
    }

    private void PlanLibrary(InstallPlan plan, Library library, PlatformContext platform, string versionId, bool allowDownload)
    {
        var artifact = library.Downloads?.Artifact;
        var parsed = MavenCoordinate.TryParse(library.Name, out var coordinate);

        var path = artifact?.Path;
        var url = artifact?.Url;
        var sha1 = artifact?.Sha1 ?? library.Sha1;
        var size = artifact?.Size ?? library.Size;

        if (string.IsNullOrWhiteSpace(path))
        {
            if (!parsed)
            {
                plan.Warnings.Add($"无法解析库坐标：{library.Name}");
                return;
            }

            path = coordinate.ToRepositoryPath();
            url ??= BuildMavenUrl(library.Url, path);
        }
        else if (string.IsNullOrWhiteSpace(url))
        {
            url = BuildMavenUrl(library.Url, path);
        }

        var target = Path.Combine(_paths.LibrariesDir, PathEx.Rel(path));
        plan.Files.Add(new DownloadTask
        {
            TargetPath = target,
            Kind = ArtifactKind.Library,
            Context = new SourceContext(
                VersionId: versionId,
                MavenPath: path,
                FileName: Path.GetFileName(path),
                Sha1: sha1,
                OriginalUrl: url),
            Sha1 = sha1,
            Size = size,
            DownloadAllowed = allowDownload,
            DisplayName = library.Name,
        });

        // Legacy (<1.19) natives are separate classifier jars unpacked into versions/<id>/natives.
        if (library.Natives is not { Count: > 0 } natives ||
            !natives.TryGetValue(platform.OsName, out var classifierTemplate) ||
            string.IsNullOrWhiteSpace(classifierTemplate))
        {
            return;
        }

        var classifier = classifierTemplate.Replace(
            "${arch}",
            platform.Arch switch
            {
                "x86_64" => "64",
                "x86" => "32",
                _ => platform.Arch,
            },
            StringComparison.OrdinalIgnoreCase);

        Artifact? nativeArtifact = null;
        if (library.Downloads?.Classifiers is { } classifiers &&
            classifiers.TryGetValue(classifier, out var found) &&
            !string.IsNullOrWhiteSpace(found.Url))
        {
            nativeArtifact = found;
        }

        if (nativeArtifact is null && parsed)
        {
            var nativeCoordinate = new MavenCoordinate(coordinate.Group, coordinate.Artifact, coordinate.Version, classifier);
            var nativePath = nativeCoordinate.ToRepositoryPath();
            nativeArtifact = new Artifact
            {
                Path = nativePath,
                Url = BuildMavenUrl(library.Url, nativePath),
            };
        }

        if (nativeArtifact?.Path is null || string.IsNullOrWhiteSpace(nativeArtifact.Url))
        {
            plan.Warnings.Add($"无法确定 {library.Name} 的 {classifier} 下载地址");
            return;
        }

        var nativeTarget = Path.Combine(_paths.LibrariesDir, PathEx.Rel(nativeArtifact.Path));
        plan.Files.Add(new DownloadTask
        {
            TargetPath = nativeTarget,
            Kind = ArtifactKind.Library,
            Context = new SourceContext(
                VersionId: versionId,
                MavenPath: nativeArtifact.Path,
                FileName: Path.GetFileName(nativeArtifact.Path),
                Sha1: nativeArtifact.Sha1,
                OriginalUrl: nativeArtifact.Url),
            Sha1 = nativeArtifact.Sha1,
            Size = nativeArtifact.Size,
            DownloadAllowed = allowDownload,
            DisplayName = $"{library.Name} ({classifier})",
        });

        var excludes = library.Extract?.Exclude is { Count: > 0 } custom
            ? custom.Concat(ZipService.NativesExcludes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : ZipService.NativesExcludes;

        plan.Natives.Add(new NativesJob(nativeTarget, _paths.NativesDir(versionId), excludes));
    }

    private void PlanAssets(InstallPlan plan, AssetIndexFile indexFile, string indexId, bool allowDownload)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, asset) in indexFile.Objects)
        {
            if (string.IsNullOrWhiteSpace(asset.Hash) || asset.Hash.Length < 2)
            {
                continue;
            }

            var target = _paths.AssetObjectPath(asset.Hash);
            if (seen.Add(target))
            {
                plan.Files.Add(new DownloadTask
                {
                    TargetPath = target,
                    Kind = ArtifactKind.AssetObject,
                    Context = new SourceContext(
                        IndexId: indexId,
                        AssetHash: asset.Hash,
                        FileName: name,
                        OriginalUrl: $"https://resources.download.minecraft.net/{asset.Hash[..2]}/{asset.Hash}"),
                    Sha1 = asset.Hash,
                    Size = asset.Size,
                    DownloadAllowed = allowDownload,
                    DisplayName = name,
                });
            }

            if (indexFile.Virtual)
            {
                plan.AssetCopies.Add(new AssetCopyJob(target, PathEx.SafeCombine(_paths.VirtualAssetsDir(indexId), name)));
            }

            if (indexFile.MapToResources)
            {
                plan.AssetCopies.Add(new AssetCopyJob(target, PathEx.SafeCombine(_paths.LegacyResourcesDir, name)));
            }
        }
    }

    private int ExtractNatives(InstallPlan plan)
    {
        var count = 0;
        foreach (var job in plan.Natives)
        {
            if (!File.Exists(job.JarPath))
            {
                _reporter.Warn($"natives 压缩包不存在：{job.JarPath}", "natives");
                continue;
            }

            try
            {
                Directory.CreateDirectory(job.DestinationDirectory);
                var extracted = ZipService.ExtractAll(job.JarPath, job.DestinationDirectory, job.ExcludePrefixes);
                count += extracted;
                _reporter.Debug($"解压 {Path.GetFileName(job.JarPath)} → {job.DestinationDirectory}（{extracted} 个文件）", "natives");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _reporter.Warn($"解压 natives 失败 {Path.GetFileName(job.JarPath)}：{ex.Message}", "natives");
            }
        }

        return count;
    }

    private void CopyLegacyAssets(InstallPlan plan)
    {
        if (plan.AssetCopies.Count == 0)
        {
            return;
        }

        _pump.Phase = InstallPhase.DownloadingAssets;
        var copied = 0;
        foreach (var job in plan.AssetCopies)
        {
            try
            {
                if (!File.Exists(job.SourcePath))
                {
                    continue;
                }

                PathEx.EnsureParent(job.DestinationPath);
                File.Copy(job.SourcePath, job.DestinationPath, overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _reporter.Warn($"复制旧版资源失败 {job.DestinationPath}：{ex.Message}", "assets");
            }
        }

        _reporter.Info($"已生成旧版资源目录（{copied} 个文件）", "assets");
    }

    private static string BuildMavenUrl(string? baseUrl, string mavenPath)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://libraries.minecraft.net/" : baseUrl.TrimEnd('/');
        if (!root.EndsWith('/'))
        {
            root += "/";
        }

        return root + mavenPath;
    }
}
