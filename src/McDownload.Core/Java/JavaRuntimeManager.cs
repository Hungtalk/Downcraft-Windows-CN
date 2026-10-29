using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using McDownload.Core.Diagnostics;
using McDownload.Core.Install;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;
using McDownload.Core.Util;

namespace McDownload.Core.Java;

/// <summary>A usable JVM installation.</summary>
public sealed record JavaInstallation(string Path, int MajorVersion, string? VersionText, bool IsBundled)
{
    public override string ToString() => $"Java {MajorVersion}（{(IsBundled ? "内置" : "系统")}）{Path}";
}

/// <summary>One build of a Mojang Java runtime component (<c>all.json</c> entry).</summary>
public sealed class JavaRuntimeEntry
{
    [JsonPropertyName("manifest")]
    public Artifact? Manifest { get; set; }

    [JsonPropertyName("version")]
    public JavaRuntimeVersion? Version { get; set; }
}

public sealed class JavaRuntimeVersion
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("released")]
    public string? Released { get; set; }
}

/// <summary>A file inside a Java runtime component manifest.</summary>
public sealed class JavaRuntimeFileEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("executable")]
    public bool Executable { get; set; }

    [JsonPropertyName("downloads")]
    public JavaRuntimeDownloads? Downloads { get; set; }
}

public sealed class JavaRuntimeDownloads
{
    [JsonPropertyName("raw")]
    public Artifact? Raw { get; set; }

    [JsonPropertyName("lzma")]
    public Artifact? Lzma { get; set; }
}

public sealed class JavaRuntimeComponentManifest
{
    [JsonPropertyName("files")]
    public Dictionary<string, JavaRuntimeFileEntry> Files { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Finds a usable JVM and, when none is installed, provisions Mojang's own Java runtime into
/// <c>&lt;gameDir&gt;/runtime/&lt;component&gt;/&lt;platform&gt;/&lt;component&gt;</c>. That layout is what
/// launchers expect and is what the Forge/NeoForge installers are executed with.
/// </summary>
public sealed partial class JavaRuntimeManager
{
    private readonly GamePaths _paths;
    private readonly Downloader _downloader;
    private readonly IReporter _reporter;
    private readonly Dictionary<string, JavaInstallation?> _probeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private Dictionary<string, List<JavaRuntimeEntry>>? _platformComponents;

    public JavaRuntimeManager(GamePaths paths, Downloader downloader, IReporter reporter)
    {
        _paths = paths;
        _downloader = downloader;
        _reporter = reporter;
    }

    /// <summary>Lists every JVM found on this machine (used by <c>mcdl java --detect</c> and doctor).</summary>
    public async Task<IReadOnlyList<JavaInstallation>> DetectAllAsync(int minimumMajor = 8, CancellationToken cancellationToken = default)
    {
        var found = new List<JavaInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in EnumerateCandidates(null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installation = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (installation is not null && installation.MajorVersion >= minimumMajor && seen.Add(installation.Path))
            {
                found.Add(installation);
            }
        }

        return found;
    }

    /// <summary>Searches bundled runtimes, JAVA_HOME, PATH and the usual install roots.</summary>
    public async Task<JavaInstallation?> FindAsync(int requiredMajor, string? explicitPath = null, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in EnumerateCandidates(explicitPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installation = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (installation is null)
            {
                continue;
            }

            if (installation.MajorVersion >= requiredMajor)
            {
                return installation;
            }

            _reporter.Debug($"跳过 Java {installation.MajorVersion}（需要 {requiredMajor}+）：{candidate}", "java");
        }

        return null;
    }

    /// <summary>
    /// Ensures a JVM of at least <paramref name="requiredMajor"/> exists, provisioning one when
    /// allowed. Returns <c>null</c> when nothing suitable is available and provisioning is off.
    /// </summary>
    public async Task<JavaInstallation?> EnsureAsync(
        string? component,
        int requiredMajor,
        bool allowProvision,
        string? explicitPath = null,
        CancellationToken cancellationToken = default)
    {
        var found = await FindAsync(requiredMajor, explicitPath, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            _reporter.Info($"使用 {found}", "java");
            return found;
        }

        if (!allowProvision)
        {
            _reporter.Warn($"未找到 Java {requiredMajor}+，且已禁用自动配置", "java");
            return null;
        }

        _reporter.Info($"未找到 Java {requiredMajor}+，开始下载 Mojang Java 运行时…", "java");
        return await ProvisionAsync(component, requiredMajor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads and unpacks a Mojang Java runtime component.</summary>
    public async Task<JavaInstallation> ProvisionAsync(string? component, int requiredMajor, CancellationToken cancellationToken = default)
    {
        var platform = PlatformContext.Current.RuntimePlatformKey;
        var components = await GetPlatformComponentsAsync(cancellationToken).ConfigureAwait(false);
        if (components.Count == 0)
        {
            throw new InstallFailedException($"Mojang Java 运行时清单里没有平台 {platform} 的组件");
        }

        var chosen = ChooseComponent(components, component, requiredMajor)
            ?? throw new InstallFailedException($"找不到满足 Java {requiredMajor}+ 的 Mojang 运行时组件");

        if (chosen.Manifest?.Url is null)
        {
            throw new InstallFailedException($"运行时 {chosen.Name} 缺少清单地址");
        }

        _reporter.Info($"准备 Java 运行时 {chosen.Name} {chosen.VersionName}（{platform}）", "java");

        var manifestBytes = await _downloader
            .GetBytesAsync(
                ArtifactKind.JavaRuntimeManifest,
                new SourceContext(
                    Component: chosen.Name,
                    Platform: platform,
                    Sha1: chosen.Manifest.Sha1,
                    OriginalUrl: chosen.Manifest.Url),
                required: true,
                cancellationToken)
            .ConfigureAwait(false);

        var manifest = MinecraftJson.Deserialize<JavaRuntimeComponentManifest>(manifestBytes!)
            ?? throw new InstallFailedException($"运行时 {chosen.Name} 清单解析失败");

        var baseDir = Path.Combine(_paths.RuntimeDir, chosen.Name, platform, chosen.Name);
        Directory.CreateDirectory(baseDir);

        var tasks = new List<DownloadTask>();
        foreach (var entry in manifest.Files)
        {
            var name = entry.Key;
            var file = entry.Value;
            if (string.Equals(file.Type, "directory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = file.Downloads?.Raw;
            if (raw?.Url is null || !PathEx.IsSafeRelative(name))
            {
                // "link" entries only exist on Linux/macOS runtime images.
                continue;
            }

            tasks.Add(new DownloadTask
            {
                TargetPath = PathEx.SafeCombine(baseDir, name),
                Kind = ArtifactKind.JavaRuntimeFile,
                Context = new SourceContext(
                    Component: chosen.Name,
                    Platform: platform,
                    FileName: name,
                    Sha1: raw.Sha1,
                    OriginalUrl: raw.Url),
                Sha1 = raw.Sha1,
                Size = raw.Size,
                DisplayName = "java/" + name,
            });
        }

        if (tasks.Count == 0)
        {
            throw new InstallFailedException($"运行时 {chosen.Name} 的清单中没有可下载文件");
        }

        var result = await _downloader
            .RunAsync(tasks, InstallPhase.ProvisioningJava, Math.Min(8, tasks.Count), cancellationToken)
            .ConfigureAwait(false);

        if (result.Failed > 0)
        {
            throw new InstallFailedException(
                $"Java 运行时下载失败（{result.Failed}/{result.Total} 个文件）",
                result.Errors.Take(5).Select(e => $"{e.Task}: {e.Error}").ToList());
        }

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var javaExe = Path.Combine(baseDir, "bin", executable);
        if (!File.Exists(javaExe))
        {
            var javaw = Path.Combine(baseDir, "bin", OperatingSystem.IsWindows() ? "javaw.exe" : "java");
            javaExe = File.Exists(javaw)
                ? javaw
                : throw new InstallFailedException($"运行时 {chosen.Name} 解压后缺少 bin/java");
        }

        var major = ParseMajor(chosen.VersionName) ?? requiredMajor;
        _reporter.Info($"Java 运行时就绪：{javaExe}", "java");
        return new JavaInstallation(javaExe, major, chosen.VersionName, IsBundled: true);
    }

    /// <summary>Reads the Mojang <c>all.json</c> runtime index (cached per manager instance).</summary>
    public async Task<IReadOnlyDictionary<string, List<JavaRuntimeEntry>>> GetPlatformComponentsAsync(CancellationToken cancellationToken = default)
    {
        if (_platformComponents is not null)
        {
            return _platformComponents;
        }

        var bytes = await _downloader
            .GetBytesAsync(ArtifactKind.JavaRuntimeManifest, new SourceContext(), required: true, cancellationToken)
            .ConfigureAwait(false);

        var document = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<JavaRuntimeEntry>>>>(
            bytes!, MinecraftJson.Options)
            ?? throw new InstallFailedException("Java 运行时清单解析失败");

        var platform = PlatformContext.Current.RuntimePlatformKey;
        if (!document.TryGetValue(platform, out var components) || components.Count == 0)
        {
            components = document.TryGetValue("windows-x64", out var win) ? win : document.Values.FirstOrDefault() ?? [];
            if (components.Count > 0)
            {
                _reporter.Warn($"运行时清单没有平台 {platform}，改用 {document.Keys.First()}", "java");
            }
        }

        _platformComponents = components;
        return components;
    }

    /// <summary>Lists every runtime component with its Java major version (used by <c>mcdl java --list</c>).</summary>
    public async Task<IReadOnlyList<(string Name, int Major, string? Released)>> ListComponentsAsync(CancellationToken cancellationToken = default)
    {
        var components = await GetPlatformComponentsAsync(cancellationToken).ConfigureAwait(false);
        return components
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => (Name: kv.Key, Major: ParseMajor(kv.Value[0].Version?.Name) ?? 0, Released: kv.Value[0].Version?.Released))
            .OrderBy(item => item.Major)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ChosenComponent(string Name, string? VersionName, Artifact? Manifest);

    private static ChosenComponent? ChooseComponent(
        IReadOnlyDictionary<string, List<JavaRuntimeEntry>> components,
        string? preferred,
        int requiredMajor)
    {
        var candidates = new List<ChosenComponent>();
        foreach (var (name, entries) in components)
        {
            if (entries.Count == 0 || entries[0].Manifest?.Url is null)
            {
                continue;
            }

            candidates.Add(new ChosenComponent(name, entries[0].Version?.Name, entries[0].Manifest));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var exact = candidates.FirstOrDefault(c =>
                string.Equals(c.Name, preferred, StringComparison.OrdinalIgnoreCase) &&
                (ParseMajor(c.VersionName) ?? 0) >= requiredMajor);
            if (exact is not null)
            {
                return exact;
            }
        }

        // The smallest runtime that still satisfies the requirement keeps the download small.
        var suitable = candidates
            .Select(c => (Component: c, Major: ParseMajor(c.VersionName) ?? 0))
            .Where(x => x.Major >= requiredMajor)
            .OrderBy(x => x.Major)
            .ToList();

        if (suitable.Count > 0)
        {
            return suitable[0].Component;
        }

        // Nothing new enough: newest available is the best effort.
        return candidates
            .Select(c => (Component: c, Major: ParseMajor(c.VersionName) ?? 0))
            .OrderByDescending(x => x.Major)
            .First()
            .Component;
    }

    private IEnumerable<string> EnumerateCandidates(string? explicitPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath) && seen.Add(explicitPath))
        {
            yield return explicitPath;
        }

        foreach (var bundled in EnumerateBundledRuntimes())
        {
            if (seen.Add(bundled))
            {
                yield return bundled;
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var path = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(path) && seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (var onPath in EnumerateOnPath())
        {
            if (seen.Add(onPath))
            {
                yield return onPath;
            }
        }

        foreach (var installed in EnumerateInstalled())
        {
            if (seen.Add(installed))
            {
                yield return installed;
            }
        }
    }

    private IEnumerable<string> EnumerateBundledRuntimes()
    {
        if (!Directory.Exists(_paths.RuntimeDir))
        {
            yield break;
        }

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        string[] found;
        try
        {
            found = Directory.GetFiles(_paths.RuntimeDir, executable, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in found)
        {
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BellSoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"),
        ];

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(root, "java.exe", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (string.Equals(Path.GetFileName(Path.GetDirectoryName(candidate)), "bin", StringComparison.OrdinalIgnoreCase))
                {
                    yield return candidate;
                }
            }
        }
    }

    private async Task<JavaInstallation?> ProbeAsync(string javaPath, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_probeCache.TryGetValue(javaPath, out var cached))
            {
                return cached;
            }
        }

        var result = await ProcessRunner.CaptureAsync(javaPath, ["-version"], TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        JavaInstallation? installation = null;
        if (result.Success)
        {
            var match = VersionRegex().Match(result.Combined);
            if (match.Success && ParseMajor(match.Value) is { } major)
            {
                installation = new JavaInstallation(
                    javaPath,
                    major,
                    match.Value,
                    IsBundled: javaPath.StartsWith(_paths.RuntimeDir, StringComparison.OrdinalIgnoreCase));
            }
        }

        lock (_cacheLock)
        {
            _probeCache[javaPath] = installation;
        }

        return installation;
    }

    private static int? ParseMajor(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var match = VersionRegex().Match(versionText);
        if (!match.Success)
        {
            return null;
        }

        var majorText = match.Groups[1].Value;
        // Legacy style "1.8.0_392" means Java 8.
        return majorText == "1" && match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value)
            : int.Parse(majorText);
    }

    [GeneratedRegex(@"(?<![\d.])(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();
}
