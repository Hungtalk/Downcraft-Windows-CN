using System.Text.Json;
using System.Text.Json.Serialization;
using McDownload.Core.Diagnostics;
using McDownload.Core.Java;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;
using McDownload.Core.Util;

namespace McDownload.Core.Install;

/// <summary>Outcome of installing a mod loader on top of a vanilla version.</summary>
public sealed record LoaderInstallResult(
    string VersionId,
    string LoaderVersion,
    DownloadBatchResult? Downloads,
    IReadOnlyList<string> Warnings);

/// <summary>One entry of the Fabric/Quilt loader list.</summary>
internal sealed class FabricLoaderListEntry
{
    [JsonPropertyName("loader")]
    public FabricLoaderInfo? Loader { get; set; }

    [JsonPropertyName("intermediary")]
    public FabricLoaderInfo? Intermediary { get; set; }
}

internal sealed class FabricLoaderInfo
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }

    [JsonPropertyName("maven")]
    public string? Maven { get; set; }
}

/// <summary>A loader bundle: the full version string, its Maven path and canonical URL.</summary>
internal sealed record LoaderBundle(string FullVersion, string MavenPath, string OriginalUrl, string DisplayName);

/// <summary>
/// Installs Fabric, Quilt, Forge and NeoForge on top of an already installed vanilla version.
/// <para>
/// Fabric and Quilt are pure metadata merges: their profile JSON is written as a version that
/// <c>inheritsFrom</c> the vanilla one, exactly like official launchers do. Forge and NeoForge
/// need their official installer (binary patching of the client jar), which is downloaded from
/// a mirror chain and executed with a provisioned JVM; when no JVM can be used the profile is
/// extracted without running the processors and a warning explains what is missing.
/// </para>
/// </summary>
public sealed class LoaderInstaller
{
    private readonly GamePaths _paths;
    private readonly Downloader _downloader;
    private readonly IReporter _reporter;
    private readonly JavaRuntimeManager _java;

    public LoaderInstaller(GamePaths paths, Downloader downloader, IReporter reporter, JavaRuntimeManager java)
    {
        _paths = paths;
        _downloader = downloader;
        _reporter = reporter;
        _java = java;
    }

    public Task<LoaderInstallResult> InstallAsync(
        LoaderKind kind,
        string mcVersion,
        VersionMeta vanillaMeta,
        string? loaderVersion,
        InstallOptions options,
        CancellationToken cancellationToken = default)
        => kind switch
        {
            LoaderKind.Fabric => InstallFabricLikeAsync(quilt: false, mcVersion, loaderVersion, options, cancellationToken),
            LoaderKind.Quilt => InstallFabricLikeAsync(quilt: true, mcVersion, loaderVersion, options, cancellationToken),
            LoaderKind.Forge => InstallForgeLikeAsync(LoaderKind.Forge, mcVersion, vanillaMeta, loaderVersion, options, cancellationToken),
            LoaderKind.NeoForge => InstallForgeLikeAsync(LoaderKind.NeoForge, mcVersion, vanillaMeta, loaderVersion, options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Vanilla needs no loader."),
        };

    // ------------------------------------------------------------------ Fabric / Quilt

    private async Task<LoaderInstallResult> InstallFabricLikeAsync(
        bool quilt,
        string mcVersion,
        string? loaderVersion,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var name = quilt ? "Quilt" : "Fabric";
        var listKind = quilt ? ArtifactKind.QuiltLoaderList : ArtifactKind.FabricLoaderList;
        var profileKind = quilt ? ArtifactKind.QuiltProfile : ArtifactKind.FabricProfile;

        _reporter.Info($"查询 {name} Loader 版本（Minecraft {mcVersion}）…", name);
        var listBytes = await _downloader
            .GetBytesAsync(listKind, new SourceContext(VersionId: mcVersion), required: true, cancellationToken)
            .ConfigureAwait(false);

        var entries = MinecraftJson.Deserialize<List<FabricLoaderListEntry>>(listBytes!)
            ?? throw new InstallFailedException($"无法解析 {name} Loader 版本列表");

        entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Loader?.Version)).ToList();
        if (entries.Count == 0)
        {
            throw new InstallFailedException($"{name} 没有为 Minecraft {mcVersion} 提供 Loader（该版本可能尚未支持）");
        }

        var selected = SelectLoaderVersion(entries, loaderVersion, name, mcVersion);
        _reporter.Info($"{name} Loader 版本：{selected}", name);

        var profileBytes = await _downloader
            .GetBytesAsync(
                profileKind,
                new SourceContext(VersionId: mcVersion, LoaderVersion: selected),
                required: true,
                cancellationToken)
            .ConfigureAwait(false);

        var profile = MinecraftJson.Deserialize<VersionMeta>(profileBytes!)
            ?? throw new InstallFailedException($"{name} profile JSON 解析失败");

        var profileId = string.IsNullOrWhiteSpace(profile.Id)
            ? $"{(quilt ? "quilt" : "fabric")}-loader-{selected}-{mcVersion}"
            : profile.Id;

        if (!string.IsNullOrWhiteSpace(profile.InheritsFrom) &&
            !string.Equals(profile.InheritsFrom, mcVersion, StringComparison.OrdinalIgnoreCase))
        {
            _reporter.Warn($"{name} profile 继承自 {profile.InheritsFrom}，与请求的 {mcVersion} 不一致", name);
        }

        var targetDir = _paths.VersionDir(profileId);
        Directory.CreateDirectory(targetDir);
        var targetJson = Path.Combine(targetDir, profileId + ".json");
        await File.WriteAllBytesAsync(targetJson, profileBytes!, cancellationToken).ConfigureAwait(false);
        _reporter.Info($"已写入启动配置 versions/{profileId}/{profileId}.json", name);

        var tasks = BuildProfileLibraryTasks(profile, profileId, options);
        DownloadBatchResult? downloads = null;
        if (tasks.Count > 0)
        {
            _reporter.Info($"{name} 需要 {tasks.Count} 个库文件", name);
            downloads = await _downloader
                .RunAsync(tasks, InstallPhase.InstallingLoader, options.MaxParallel, cancellationToken)
                .ConfigureAwait(false);

            if (downloads.Failed > 0)
            {
                throw new InstallFailedException(
                    $"{name} 库文件下载失败（{downloads.Failed}/{downloads.Total}）",
                    downloads.Errors.Take(5).Select(e => $"{e.Task}: {e.Error}").ToList());
            }
        }

        return new LoaderInstallResult(profileId, selected, downloads, []);
    }

    private static string SelectLoaderVersion(List<FabricLoaderListEntry> entries, string? requested, string name, string mcVersion)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = entries.FirstOrDefault(e => string.Equals(e.Loader!.Version, requested, StringComparison.OrdinalIgnoreCase));
            return match?.Loader!.Version
                ?? throw new InstallFailedException($"{name} 没有版本 {requested}（Minecraft {mcVersion}）。可用：" +
                                                     string.Join(", ", entries.Take(8).Select(e => e.Loader!.Version)));
        }

        var stable = entries.FirstOrDefault(e => e.Loader!.Stable);
        return stable?.Loader!.Version ?? entries[0].Loader!.Version!;
    }

    private List<DownloadTask> BuildProfileLibraryTasks(VersionMeta profile, string versionId, InstallOptions options)
    {
        var platform = PlatformContext.Current;
        var tasks = new List<DownloadTask>();

        foreach (var library in profile.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, platform))
            {
                continue;
            }

            if (!MavenCoordinate.TryParse(library.Name, out var coordinate))
            {
                _reporter.Warn($"跳过无法解析的库：{library.Name}", versionId);
                continue;
            }

            var artifact = library.Downloads?.Artifact;
            var path = string.IsNullOrWhiteSpace(artifact?.Path) ? coordinate.ToRepositoryPath() : artifact!.Path!;
            var url = artifact?.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = string.IsNullOrWhiteSpace(library.Url)
                    ? "https://libraries.minecraft.net/" + path
                    : library.Url.TrimEnd('/') + "/" + path;
            }

            var sha1 = artifact?.Sha1 ?? library.Sha1;
            var size = artifact?.Size ?? library.Size;

            tasks.Add(new DownloadTask
            {
                TargetPath = Path.Combine(_paths.LibrariesDir, PathEx.Rel(path)),
                Kind = ArtifactKind.Library,
                Context = new SourceContext(
                    VersionId: versionId,
                    MavenPath: path,
                    FileName: Path.GetFileName(path),
                    Sha1: sha1,
                    OriginalUrl: url),
                Sha1 = sha1,
                Size = size,
                DownloadAllowed = !options.VerifyOnly,
                DisplayName = library.Name,
            });
        }

        return tasks;
    }

    // ------------------------------------------------------------------ Forge / NeoForge

    private async Task<LoaderInstallResult> InstallForgeLikeAsync(
        LoaderKind kind,
        string mcVersion,
        VersionMeta vanillaMeta,
        string? loaderVersion,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var name = kind == LoaderKind.Forge ? "Forge" : "NeoForge";
        var bundle = await ResolveForgeBundleAsync(kind, mcVersion, loaderVersion, cancellationToken).ConfigureAwait(false);
        _reporter.Info($"{name} 版本：{bundle.FullVersion}", name);

        var installerPath = Path.Combine(_paths.InstallersDir, Path.GetFileName(bundle.MavenPath));
        var kindOfInstaller = kind == LoaderKind.Forge ? ArtifactKind.ForgeInstaller : ArtifactKind.NeoForgeInstaller;
        var task = new DownloadTask
        {
            TargetPath = installerPath,
            Kind = kindOfInstaller,
            Context = new SourceContext(
                VersionId: mcVersion,
                LoaderVersion: bundle.FullVersion,
                MavenPath: bundle.MavenPath,
                FileName: Path.GetFileName(bundle.MavenPath),
                OriginalUrl: bundle.OriginalUrl),
            DisplayName = $"{name} 安装器 {bundle.FullVersion}",
        };

        var download = await _downloader
            .RunAsync([task], InstallPhase.InstallingLoader, 1, cancellationToken)
            .ConfigureAwait(false);
        if (download.Failed > 0)
        {
            throw new InstallFailedException(
                $"{name} 安装器下载失败：{download.Errors.FirstOrDefault().Error}",
                [$"安装器地址：{bundle.OriginalUrl}"]);
        }

        _reporter.Info($"安装器：{installerPath}", name);

        // The Forge/NeoForge installer expects launcher_profiles.json to exist.
        LauncherProfiles.EnsureExists(_paths);

        if (options.RunLoaderInstaller)
        {
            await PreSeedInstallerLibrariesAsync(installerPath, kind, name, mcVersion, options, cancellationToken)
                .ConfigureAwait(false);
        }

        var requiredMajor = vanillaMeta.JavaVersion?.MajorVersion ?? 8;
        JavaInstallation? java = null;
        if (options.RunLoaderInstaller)
        {
            java = await _java
                .EnsureAsync(vanillaMeta.JavaVersion?.Component, requiredMajor, options.EnsureJava, options.JavaPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!options.RunLoaderInstaller || java is null)
        {
            var warning = options.RunLoaderInstaller
                ? $"未找到可用的 Java {requiredMajor}+，已跳过 {name} 安装器的处理器（游戏可能无法启动）"
                : $"已按参数跳过 {name} 安装器执行，仅放置启动配置（游戏可能无法启动）";
            _reporter.Warn(warning, name);
            var extracted = await ExtractProfileAsync(installerPath, mcVersion, bundle, options, cancellationToken).ConfigureAwait(false);
            return extracted with { Warnings = [.. extracted.Warnings, warning] };
        }

        var before = SnapshotVersionIds();
        var installerTemp = Path.Combine(_paths.StateDir, "tmp");
        Directory.CreateDirectory(installerTemp);
        var arguments = new List<string>
        {
            "-Xmx1G",
            "-Djava.awt.headless=true",
            "-Duser.language=en",
            "-Duser.country=US",
            // Keep the installer's scratch files inside the game folder: the system TEMP can be
            // locked down (antivirus, roaming profiles) which makes the Forge processors fail.
            "-Djava.io.tmpdir=" + installerTemp,
            "-jar",
            installerPath,
            "--installClient",
            _paths.Root,
        };

        _reporter.Info($"运行 {name} 安装器（可能需要几分钟）…", name);
        var process = await ProcessRunner.RunAsync(
            java.Path,
            arguments,
            _paths.Root,
            line =>
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    _reporter.Log(new LogEntry(LogLevel.Info, trimmed, name));
                }
            },
            TimeSpan.FromMinutes(30),
            cancellationToken).ConfigureAwait(false);

        if (!process.Success)
        {
            var tail = Tail(process.Combined, 12);
            throw new InstallFailedException(
                $"{name} 安装器执行失败（退出码 {process.ExitCode}{(process.TimedOut ? "，超时" : "")}）",
                tail);
        }

        var created = DetectInstalledVersion(mcVersion, kind, bundle.FullVersion, before);
        if (created is null)
        {
            throw new InstallFailedException(
                $"{name} 安装器已结束，但没有找到对应的版本目录",
                Tail(process.Combined, 8));
        }

        _reporter.Info($"{name} 安装完成：{created}", name);

        // The installer fetches libraries itself; verify the profile exists and fix up the profile entry.
        var profileJson = _paths.VersionJsonPath(created);
        if (!File.Exists(profileJson))
        {
            throw new InstallFailedException($"{name} 安装后缺少 {profileJson}");
        }

        var loaderVersionInstalled = bundle.FullVersion;
        return new LoaderInstallResult(created, loaderVersionInstalled, download, []);
    }

    /// <summary>
    /// Downloads the libraries and processor classpath that the Forge/NeoForge installer itself
    /// needs, using the mirror chain. Installers otherwise fetch them from their own Maven hosts,
    /// which are unreachable in some networks (for example maven.neoforged.net from mainland China);
    /// pre-seeding makes the installer find valid local copies and skip those downloads.
    /// </summary>
    private async Task PreSeedInstallerLibrariesAsync(
        string installerPath,
        LoaderKind kind,
        string loaderName,
        string mcVersion,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        byte[]? profileBytes;
        try
        {
            profileBytes = ZipService.ReadEntry(installerPath, "install_profile.json");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _reporter.Debug($"无法读取安装器 install_profile.json：{ex.Message}", loaderName);
            return;
        }

        if (profileBytes is null || profileBytes.Length == 0)
        {
            return;
        }

        List<DownloadTask> tasks;
        try
        {
            using var document = JsonDocument.Parse(profileBytes);
            tasks = BuildPreSeedTasks(document.RootElement, mcVersion);
        }
        catch (JsonException ex)
        {
            _reporter.Debug($"install_profile.json 解析失败：{ex.Message}", loaderName);
            return;
        }

        if (tasks.Count == 0)
        {
            return;
        }

        _reporter.Info($"预置 {loaderName} 安装器依赖：{tasks.Count} 个库（走镜像下载，避免安装器直连官方 Maven）", loaderName);
        var result = await _downloader
            .RunAsync(tasks, InstallPhase.InstallingLoader, options.MaxParallel, cancellationToken)
            .ConfigureAwait(false);

        if (result.Failed > 0)
        {
            _reporter.Warn(
                $"{result.Failed}/{result.Total} 个安装器依赖未能预置，安装器会自行尝试下载",
                loaderName);
        }
        else
        {
            _reporter.Info($"已预置 {result.Downloaded} 个依赖（跳过 {result.Skipped} 个已存在）", loaderName);
        }

        _ = kind;
    }

    private List<DownloadTask> BuildPreSeedTasks(JsonElement profile, string versionId)
    {
        var tasks = new List<DownloadTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? coordinate, string? url, string? sha1, long? size)
        {
            if (string.IsNullOrWhiteSpace(coordinate) || !MavenCoordinate.TryParse(coordinate, out var parsed))
            {
                return;
            }

            var path = parsed.ToRepositoryPath();
            var target = Path.Combine(_paths.LibrariesDir, PathEx.Rel(path));
            if (!seen.Add(target))
            {
                return;
            }

            tasks.Add(new DownloadTask
            {
                TargetPath = target,
                Kind = ArtifactKind.Library,
                Context = new SourceContext(
                    VersionId: versionId,
                    MavenPath: path,
                    FileName: Path.GetFileName(path),
                    Sha1: sha1,
                    OriginalUrl: string.IsNullOrWhiteSpace(url) ? DeriveMavenUrl(parsed) : url),
                Sha1 = sha1,
                Size = size,
                DisplayName = coordinate,
            });
        }

        if (profile.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                var name = library.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                string? url = null;
                string? sha1 = null;
                long? size = null;

                if (library.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("artifact", out var artifact))
                {
                    url = artifact.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    sha1 = artifact.TryGetProperty("sha1", out var shaElement) ? shaElement.GetString() : null;
                    if (artifact.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize))
                    {
                        size = parsedSize;
                    }
                }

                Add(name, url, sha1, size);
            }
        }

        if (profile.TryGetProperty("processors", out var processors) && processors.ValueKind == JsonValueKind.Array)
        {
            foreach (var processor in processors.EnumerateArray())
            {
                if (!processor.TryGetProperty("classpath", out var classpath) || classpath.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in classpath.EnumerateArray())
                {
                    Add(entry.GetString(), null, null, null);
                }
            }
        }

        return tasks;
    }

    private static string DeriveMavenUrl(MavenCoordinate coordinate)
    {
        var path = coordinate.ToRepositoryPath();
        var group = coordinate.Group;

        if (group.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
        {
            return "https://maven.neoforged.net/releases/" + path;
        }

        if (group.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase) ||
            group.StartsWith("de.oceanlabs", StringComparison.OrdinalIgnoreCase) ||
            group.StartsWith("net.minecraft", StringComparison.OrdinalIgnoreCase) ||
            group.StartsWith("cpw.mods", StringComparison.OrdinalIgnoreCase))
        {
            return "https://maven.minecraftforge.net/" + path;
        }

        return "https://libraries.minecraft.net/" + path;
    }

    private async Task<LoaderInstallResult> ExtractProfileAsync(        string installerPath,
        string mcVersion,
        LoaderBundle bundle,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        VersionMeta? profile = null;
        try
        {
            var versionJson = ZipService.ReadEntry(installerPath, "version.json");
            if (versionJson is not null)
            {
                profile = MinecraftJson.Deserialize<VersionMeta>(versionJson);
            }

            if (profile?.Id is null)
            {
                var installProfile = ZipService.ReadEntry(installerPath, "install_profile.json");
                if (installProfile is not null)
                {
                    using var document = JsonDocument.Parse(installProfile);
                    if (document.RootElement.TryGetProperty("versionInfo", out var versionInfo))
                    {
                        profile = versionInfo.Deserialize<VersionMeta>(MinecraftJson.Options);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new InstallFailedException($"无法读取安装器内的版本配置：{ex.Message}");
        }

        if (profile?.Id is null)
        {
            throw new InstallFailedException("安装器内没有 version.json / install_profile.json，无法离线放置启动配置");
        }

        warnings.Add("未运行安装器的处理器，缺失的补丁文件需要该启动器自行处理");

        var targetDir = _paths.VersionDir(profile.Id);
        Directory.CreateDirectory(targetDir);
        var json = MinecraftJson.Serialize(profile);
        await File.WriteAllTextAsync(Path.Combine(targetDir, profile.Id + ".json"), json, Text.Utf8, cancellationToken)
            .ConfigureAwait(false);

        var tasks = BuildProfileLibraryTasks(profile, profile.Id, options);
        DownloadBatchResult? downloads = null;
        if (tasks.Count > 0)
        {
            downloads = await _downloader
                .RunAsync(tasks, InstallPhase.InstallingLoader, options.MaxParallel, cancellationToken)
                .ConfigureAwait(false);
        }

        return new LoaderInstallResult(profile.Id, bundle.FullVersion, downloads, warnings);
    }

    private HashSet<string> SnapshotVersionIds()
    {
        if (!Directory.Exists(_paths.VersionsDir))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateDirectories(_paths.VersionsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the version folder a loader installer produced. Installers may reuse an existing
    /// folder (re-install) or create a differently named one, so this checks the expected ids
    /// first and then falls back to the newest profile that inherits the vanilla version.
    /// </summary>
    private string? DetectInstalledVersion(string mcVersion, LoaderKind kind, string loaderVersion, HashSet<string> before)
    {
        var marker = kind == LoaderKind.NeoForge ? "neoforge" : "forge";
        var expected = ExpectedVersionIds(kind, mcVersion, loaderVersion);

        var candidates = new List<(string Id, DateTime Written)>();
        if (Directory.Exists(_paths.VersionsDir))
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.VersionsDir))
            {
                var id = Path.GetFileName(directory);
                if (!id.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var json = Path.Combine(directory, id + ".json");
                if (!File.Exists(json))
                {
                    continue;
                }

                var inherits = ReadInheritsFrom(json);
                var isExpected = expected.Contains(id, StringComparer.OrdinalIgnoreCase);
                if (!isExpected && !string.Equals(inherits, mcVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add((id, File.GetLastWriteTimeUtc(json)));
            }
        }

        var exact = candidates.FirstOrDefault(c => expected.Contains(c.Id, StringComparer.OrdinalIgnoreCase));
        if (exact.Id is not null)
        {
            return exact.Id;
        }

        var fresh = candidates
            .Where(c => !before.Contains(c.Id))
            .OrderByDescending(c => c.Written)
            .Select(c => c.Id)
            .FirstOrDefault();
        if (fresh is not null)
        {
            return fresh;
        }

        return candidates.OrderByDescending(c => c.Written).Select(c => c.Id).FirstOrDefault();
    }

    private static string[] ExpectedVersionIds(LoaderKind kind, string mcVersion, string loaderVersion)
    {
        var suffix = loaderVersion.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[(mcVersion.Length + 1)..]
            : loaderVersion;

        return kind switch
        {
            LoaderKind.Forge =>
            [
                $"{mcVersion}-forge-{suffix}",
                $"forge-{suffix}",
            ],
            LoaderKind.NeoForge =>
            [
                $"neoforge-{loaderVersion}",
                $"neoforge-{suffix}",
                $"{mcVersion}-neoforge-{suffix}",
                $"{mcVersion}-forge-{suffix}",
            ],
            _ => [],
        };
    }

    private static string? ReadInheritsFrom(string versionJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(versionJsonPath));
            return document.RootElement.TryGetProperty("inheritsFrom", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ version resolution

    internal async Task<LoaderBundle> ResolveForgeBundleAsync(
        LoaderKind kind,
        string mcVersion,
        string? requested,
        CancellationToken cancellationToken)
    {
        return kind == LoaderKind.Forge
            ? await ResolveForgeAsync(mcVersion, requested, cancellationToken).ConfigureAwait(false)
            : await ResolveNeoForgeAsync(mcVersion, requested, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LoaderBundle> ResolveForgeAsync(string mcVersion, string? requested, CancellationToken cancellationToken)
    {
        string? full = null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            full = NormalizeForgeVersion(mcVersion, requested);
        }
        else
        {
            var promotions = await TryGetPromotionsAsync(cancellationToken).ConfigureAwait(false);
            if (promotions is not null)
            {
                if (promotions.TryGetValue($"{mcVersion}-recommended", out var recommended))
                {
                    full = $"{mcVersion}-{recommended}";
                }
                else if (promotions.TryGetValue($"{mcVersion}-latest", out var latest))
                {
                    full = $"{mcVersion}-{latest}";
                }
            }

            if (full is null)
            {
                var versions = await GetMavenVersionsAsync(ArtifactKind.ForgeMavenMetadata, mcVersion, cancellationToken).ConfigureAwait(false);
                full = versions
                    .Where(v => v.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v, LoaderVersionComparer.Instance)
                    .FirstOrDefault();
            }
        }

        if (string.IsNullOrWhiteSpace(full))
        {
            throw new InstallFailedException($"没有找到 Minecraft {mcVersion} 的 Forge 版本");
        }

        var path = $"net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
        return new LoaderBundle(
            full,
            path,
            $"https://maven.minecraftforge.net/{path}",
            $"Minecraft Forge {full}");
    }

    private async Task<LoaderBundle> ResolveNeoForgeAsync(string mcVersion, string? requested, CancellationToken cancellationToken)
    {
        // NeoForge 1.20.1 shipped as net.neoforged:forge with "1.20.1-47.1.x" coordinates.
        var legacy = mcVersion == "1.20.1";

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var full = requested.Contains('-') ? requested : (legacy ? $"{mcVersion}-{requested}" : requested);
            var legacyPath = full.Contains('-');
            var path = legacyPath
                ? $"net/neoforged/forge/{full}/forge-{full}-installer.jar"
                : $"net/neoforged/neoforge/{full}/neoforge-{full}-installer.jar";
            return new LoaderBundle(
                full,
                path,
                $"https://maven.neoforged.net/releases/{path}",
                $"NeoForge {full}");
        }

        if (legacy)
        {
            var legacyVersions = await GetMavenVersionsAsync(ArtifactKind.NeoForgeLegacyMavenMetadata, mcVersion, cancellationToken)
                .ConfigureAwait(false);
            var legacyFull = legacyVersions
                .Where(v => v.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v, LoaderVersionComparer.Instance)
                .FirstOrDefault()
                ?? throw new InstallFailedException("NeoForge 没有提供 Minecraft 1.20.1 的版本（请改用 Forge）");

            var legacyPath = $"net/neoforged/forge/{legacyFull}/forge-{legacyFull}-installer.jar";
            return new LoaderBundle(
                legacyFull,
                legacyPath,
                $"https://maven.neoforged.net/releases/{legacyPath}",
                $"NeoForge {legacyFull}");
        }

        var prefix = NeoForgePrefix(mcVersion);
        var versions = await GetMavenVersionsAsync(ArtifactKind.NeoForgeMavenMetadata, mcVersion, cancellationToken)
            .ConfigureAwait(false);

        var match = versions
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !v.Contains('-', StringComparison.Ordinal))
            .OrderByDescending(v => v, LoaderVersionComparer.Instance)
            .FirstOrDefault()
            ?? throw new InstallFailedException($"NeoForge 没有为 Minecraft {mcVersion} 提供版本（该版本可能尚未支持）");

        var path2 = $"net/neoforged/neoforge/{match}/neoforge-{match}-installer.jar";
        return new LoaderBundle(
            match,
            path2,
            $"https://maven.neoforged.net/releases/{path2}",
            $"NeoForge {match}");
    }

    /// <summary>Maps a Minecraft version to the NeoForge version prefix (<c>1.21.1</c> → <c>21.1.</c>).</summary>
    internal static string NeoForgePrefix(string mcVersion)
    {
        if (mcVersion.StartsWith("1.", StringComparison.Ordinal))
        {
            var parts = mcVersion.Split('.');
            var minor = parts.Length > 1 ? parts[1] : "0";
            var patch = parts.Length > 2 ? parts[2] : "0";
            return $"{minor}.{patch}.";
        }

        return mcVersion + ".";
    }

    internal static string NormalizeForgeVersion(string mcVersion, string requested)
    {
        if (requested.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
        {
            return requested;
        }

        return requested.Contains('-') ? requested : $"{mcVersion}-{requested}";
    }

    private async Task<Dictionary<string, string>?> TryGetPromotionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _downloader
                .GetBytesAsync(ArtifactKind.ForgePromotions, new SourceContext(), required: false, cancellationToken)
                .ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("promos", out var promos))
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in promos.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        result[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }

                return result;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or DownloadFailedException)
        {
            _reporter.Debug($"无法读取 Forge promotions：{ex.Message}", "forge");
        }

        return null;
    }

    private async Task<List<string>> GetMavenVersionsAsync(ArtifactKind kind, string mcVersion, CancellationToken cancellationToken)
    {
        var bytes = await _downloader
            .GetBytesAsync(kind, new SourceContext(VersionId: mcVersion), required: true, cancellationToken)
            .ConfigureAwait(false);

        var xml = System.Text.Encoding.UTF8.GetString(bytes!);
        var versions = System.Text.RegularExpressions.Regex
            .Matches(xml, "<version>([^<]+)</version>")
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (versions.Count == 0)
        {
            throw new InstallFailedException("Maven 元数据中没有版本列表");
        }

        return versions;
    }

    private static IReadOnlyList<string> Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all.Length <= lines ? all : all[^lines..];
    }
}

/// <summary>Compares loader version strings numerically (<c>21.1.265</c> &gt; <c>21.1.209</c>).</summary>
internal sealed class LoaderVersionComparer : IComparer<string>
{
    public static readonly LoaderVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = Tokenize(x);
        var right = Tokenize(y);
        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var l = i < left.Count ? left[i] : "0";
            var r = i < right.Count ? right[i] : "0";
            var leftNumeric = int.TryParse(l, out var ln);
            var rightNumeric = int.TryParse(r, out var rn);

            int comparison;
            if (leftNumeric && rightNumeric)
            {
                comparison = ln.CompareTo(rn);
            }
            else if (leftNumeric != rightNumeric)
            {
                // A numeric segment outranks a pre-release word ("1.0" > "1.0-beta").
                comparison = leftNumeric ? 1 : -1;
            }
            else
            {
                comparison = string.CompareOrdinal(l, r);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static List<string> Tokenize(string value) =>
        System.Text.RegularExpressions.Regex.Split(value, @"[.\-_+]")
            .Where(token => token.Length > 0)
            .ToList();
}
