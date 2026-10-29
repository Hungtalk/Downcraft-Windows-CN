using System.Text.Json;
using McDownload.Core.Diagnostics;
using McDownload.Core.Install;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;

namespace McDownload.Core;

/// <summary>
/// Read-only queries used by the CLI and the GUI: version list, loader list and mirror
/// latency. Nothing here writes to the game directory except the metadata cache.
/// </summary>
public sealed class MinecraftCatalog
{
    private readonly SourceCatalog _sources;

    public MinecraftCatalog(SourceCatalog? sources = null)
    {
        _sources = sources ?? SourceCatalog.LoadDefault();
    }

    public SourceCatalog Sources => _sources;

    /// <summary>Cache used when no game directory is known.</summary>
    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "McDownload",
        "cache");

    /// <summary>Fetches the version manifest (through the mirror chain).</summary>
    public async Task<VersionManifest> GetManifestAsync(
        string? cacheDirectory = null,
        bool refresh = false,
        IReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        reporter ??= NullReporter.Instance;
        var pump = new ProgressPump(reporter);
        var router = new SourceRouter(_sources.Enabled, health: null, preferPriorityOrder: true);
        using var downloader = new Downloader(router, new DownloaderOptions(), reporter, pump);
        var service = new VersionManifestService(downloader, reporter);
        return await service
            .GetManifestAsync(cacheDirectory ?? DefaultCacheDirectory, refresh, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Measures latency of every enabled mirror.</summary>
    public async Task<IReadOnlyList<SourceProbeResult>> ProbeAsync(IReporter? reporter = null, CancellationToken cancellationToken = default)
    {
        reporter ??= NullReporter.Instance;
        using var http = McdHttp.Create(8, "McDownload/1.0");
        return await SourceProber.ProbeAsync(_sources.Enabled, http, health: null, reporter, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists loader versions available for a Minecraft version, newest first.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLoaderVersionsAsync(
        LoaderKind loader,
        string minecraftVersion,
        IReporter? reporter = null,
        CancellationToken cancellationToken = default)
    {
        reporter ??= NullReporter.Instance;

        if (loader == LoaderKind.Vanilla)
        {
            return [];
        }

        var pump = new ProgressPump(reporter);
        var router = new SourceRouter(_sources.Enabled, health: null, preferPriorityOrder: true);
        using var downloader = new Downloader(router, new DownloaderOptions { MaxParallel = 4 }, reporter, pump);

        switch (loader)
        {
            case LoaderKind.Fabric:
            case LoaderKind.Quilt:
            {
                var kind = loader == LoaderKind.Fabric ? ArtifactKind.FabricLoaderList : ArtifactKind.QuiltLoaderList;
                var bytes = await downloader
                    .GetBytesAsync(kind, new SourceContext(VersionId: minecraftVersion), required: true, cancellationToken)
                    .ConfigureAwait(false);
                var entries = MinecraftJson.Deserialize<List<FabricLoaderListEntry>>(bytes!);
                return entries?
                    .Where(e => !string.IsNullOrWhiteSpace(e.Loader?.Version))
                    .OrderByDescending(e => e.Loader!.Stable)
                    .Select(e => e.Loader!.Version!)
                    .ToList() ?? [];
            }

            case LoaderKind.Forge:
            {
                var bytes = await downloader
                    .GetBytesAsync(ArtifactKind.ForgeMavenMetadata, new SourceContext(VersionId: minecraftVersion), required: true, cancellationToken)
                    .ConfigureAwait(false);
                var versions = ParseMavenVersions(bytes!);
                return versions
                    .Where(v => v.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v[(minecraftVersion.Length + 1)..])
                    .OrderByDescending(v => v, LoaderVersionComparer.Instance)
                    .ToList();
            }

            case LoaderKind.NeoForge:
            {
                var legacy = minecraftVersion == "1.20.1";
                var kind = legacy ? ArtifactKind.NeoForgeLegacyMavenMetadata : ArtifactKind.NeoForgeMavenMetadata;
                var bytes = await downloader
                    .GetBytesAsync(kind, new SourceContext(VersionId: minecraftVersion), required: true, cancellationToken)
                    .ConfigureAwait(false);
                var versions = ParseMavenVersions(bytes!);

                if (legacy)
                {
                    return versions
                        .Where(v => v.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(v => v, LoaderVersionComparer.Instance)
                        .ToList();
                }

                var prefix = LoaderInstaller.NeoForgePrefix(minecraftVersion);
                return versions
                    .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !v.Contains('-', StringComparison.Ordinal))
                    .OrderByDescending(v => v, LoaderVersionComparer.Instance)
                    .ToList();
            }

            default:
                return [];
        }
    }

    private static List<string> ParseMavenVersions(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        return System.Text.RegularExpressions.Regex
            .Matches(text, "<version>([^<]+)</version>")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds the newest release id (used as GUI default).</summary>
    public static string? LatestRelease(VersionManifest manifest) => manifest.Latest.Release;
}
