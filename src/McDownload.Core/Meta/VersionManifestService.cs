using McDownload.Core.Diagnostics;
using McDownload.Core.Install;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;
using McDownload.Core.Util;

namespace McDownload.Core.Meta;

/// <summary>
/// Fetches and caches <c>version_manifest_v2.json</c> and per-version JSON files through the
/// mirror router, falling back to cached copies when the network is unavailable.
/// </summary>
public sealed class VersionManifestService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly Downloader _downloader;
    private readonly IReporter _reporter;
    private VersionManifest? _cached;

    public VersionManifestService(Downloader downloader, IReporter reporter)
    {
        _downloader = downloader;
        _reporter = reporter;
    }

    /// <summary>Downloads (or reuses) the version manifest.</summary>
    public async Task<VersionManifest> GetManifestAsync(string cacheDirectory, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (_cached is not null && !refresh)
        {
            return _cached;
        }

        var cachePath = Path.Combine(cacheDirectory, "version_manifest_v2.json");
        var cacheFresh = File.Exists(cachePath) &&
                         DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < CacheTtl;

        if (cacheFresh && !refresh)
        {
            try
            {
                var cached = MinecraftJson.Deserialize<VersionManifest>(File.ReadAllBytes(cachePath));
                if (cached?.Versions.Count > 0)
                {
                    _cached = cached;
                    _reporter.Debug($"使用缓存的版本清单（{cached.Versions.Count} 个版本）", "manifest");
                    return cached;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                _reporter.Debug("缓存的版本清单已损坏，重新下载", "manifest");
            }
        }

        try
        {
            var bytes = await _downloader
                .GetBytesAsync(ArtifactKind.VersionManifest, new SourceContext(), required: true, cancellationToken)
                .ConfigureAwait(false);

            var manifest = MinecraftJson.Deserialize<VersionManifest>(bytes!)
                ?? throw new InvalidDataException("版本清单为空");

            try
            {
                PathEx.EnsureParent(cachePath);
                await File.WriteAllBytesAsync(cachePath, bytes!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked-down profile or an antivirus rule must not stop the download.
                _reporter.Warn($"无法写入版本清单缓存（{ex.Message}），本次运行不使用缓存", "manifest");
            }

            _cached = manifest;
            _reporter.Info($"已获取版本清单：{manifest.Versions.Count} 个版本，最新正式版 {manifest.Latest.Release}", "manifest");
            return manifest;
        }
        catch (Exception ex) when (File.Exists(cachePath) && ex is not OperationCanceledException)
        {
            _reporter.Warn($"无法获取版本清单（{Downloader.Describe(ex)}），改用本地缓存", "manifest");
            var cached = MinecraftJson.Deserialize<VersionManifest>(File.ReadAllBytes(cachePath))
                ?? throw new InvalidDataException("本地缓存的版本清单不可用");
            _cached = cached;
            return cached;
        }
    }

    /// <summary>Finds a version by id (case-insensitive); returns <c>null</c> when unknown.</summary>
    public VersionSummary? FindVersion(VersionManifest manifest, string versionId) =>
        manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>All release versions, newest first.</summary>
    public static IReadOnlyList<VersionSummary> Releases(VersionManifest manifest) =>
        manifest.Versions.Where(v => v.IsRelease).ToList();

    /// <summary>
    /// Loads a version JSON: local file first, then the network (verified against the manifest
    /// SHA-1). The downloaded document is stored as <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>.
    /// </summary>
    public async Task<VersionMeta> LoadVersionAsync(
        GamePaths paths,
        string versionId,
        VersionSummary? summary,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var localPath = paths.VersionJsonPath(versionId);

        if (File.Exists(localPath) && !refresh)
        {
            try
            {
                var local = MinecraftJson.Deserialize<VersionMeta>(File.ReadAllBytes(localPath));
                if (local is not null && !string.IsNullOrWhiteSpace(local.Id))
                {
                    if (!string.Equals(local.Id, versionId, StringComparison.OrdinalIgnoreCase))
                    {
                        _reporter.Warn($"{localPath} 的 id 为 {local.Id}，与目录名 {versionId} 不一致", "version");
                    }

                    _reporter.Debug($"使用本地版本文件 {localPath}", "version");
                    return local;
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _reporter.Warn($"本地版本文件损坏（{ex.Message}），将重新下载", "version");
            }
        }

        var context = new SourceContext(
            VersionId: versionId,
            Sha1: summary?.Sha1,
            OriginalUrl: summary?.Url);

        var bytes = await _downloader
            .GetBytesAsync(ArtifactKind.VersionJson, context, required: true, cancellationToken)
            .ConfigureAwait(false);

        var meta = MinecraftJson.Deserialize<VersionMeta>(bytes!)
            ?? throw new InvalidDataException($"{versionId}.json 解析失败");

        try
        {
            PathEx.EnsureParent(localPath);
            await File.WriteAllBytesAsync(localPath, bytes!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _reporter.Warn($"无法写入 {versionId}.json（{ex.Message}），继续使用内存中的数据", "version");
        }

        _reporter.Info($"已下载 {versionId}.json（{meta.Libraries.Count} 个库）", "version");
        return meta;
    }

    /// <summary>Loads an asset index document, caching it under <c>assets/indexes</c>.</summary>
    public async Task<AssetIndexFile> LoadAssetIndexAsync(
        GamePaths paths,
        AssetIndexRef index,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var indexId = index.Id ?? "legacy";
        var target = paths.AssetIndexPath(indexId);

        if (File.Exists(target) && !refresh)
        {
            try
            {
                var cached = MinecraftJson.Deserialize<AssetIndexFile>(File.ReadAllBytes(target));
                if (cached?.Objects.Count > 0)
                {
                    return cached;
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        var context = new SourceContext(
            VersionId: indexId,
            IndexId: indexId,
            Sha1: index.Sha1,
            OriginalUrl: index.Url);

        var bytes = await _downloader
            .GetBytesAsync(ArtifactKind.AssetIndex, context, required: true, cancellationToken)
            .ConfigureAwait(false);

        var document = MinecraftJson.Deserialize<AssetIndexFile>(bytes!)
            ?? throw new InvalidDataException($"资源索引 {indexId} 解析失败");

        try
        {
            PathEx.EnsureParent(target);
            await File.WriteAllBytesAsync(target, bytes!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _reporter.Warn($"无法写入资源索引 {indexId}（{ex.Message}），继续使用内存中的数据", "assets");
        }

        _reporter.Info($"已下载资源索引 {indexId}：{document.Objects.Count} 个文件，{PathEx.FormatBytes(document.Objects.Values.Sum(o => o.Size))}", "assets");
        return document;
    }
}
