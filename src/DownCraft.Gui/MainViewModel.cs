using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using DownCraft.Core;
using DownCraft.Core.Diagnostics;
using DownCraft.Core.Install;
using DownCraft.Core.Java;
using DownCraft.Core.Meta;
using DownCraft.Core.Native;
using DownCraft.Core.Net;
using DownCraft.Core.Sources;
using DownCraft.Core.Util;

namespace DownCraft.Gui;

/// <summary>One line in the log panel, coloured by severity.</summary>
public sealed class LogLine
{
    private static readonly Brush InfoBrush = Frozen("#344054");
    private static readonly Brush WarningBrush = Frozen("#B54708");
    private static readonly Brush ErrorBrush = Frozen("#B42318");
    private static readonly Brush DebugBrush = Frozen("#98A2B3");

    public LogLine(LogLevel level, string message, string? scope)
    {
        var tag = level switch
        {
            LogLevel.Error => "错误",
            LogLevel.Warning => "警告",
            LogLevel.Debug or LogLevel.Trace => "调试",
            _ => "信息",
        };
        var prefix = string.IsNullOrWhiteSpace(scope) ? string.Empty : $"[{scope}] ";
        Text = $"[{DateTime.Now:HH:mm:ss}] {tag} {prefix}{message}";
        Color = level switch
        {
            LogLevel.Error => ErrorBrush,
            LogLevel.Warning => WarningBrush,
            LogLevel.Debug or LogLevel.Trace => DebugBrush,
            _ => InfoBrush,
        };
    }

    public string Text { get; }

    public Brush Color { get; }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Combo-box item for a loader choice.</summary>
public sealed record LoaderOption(string Name, LoaderKind Kind);

/// <summary>Combo-box item for a mirror strategy.</summary>
public sealed record SourceModeOption(string Name, IReadOnlyList<string> Order, bool PreferPriority, bool Probe);

/// <summary>View model behind the single window; owns the installer run and all UI state.</summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public const string LatestLoaderVersionLabel = "最新（自动）";

    private readonly MinecraftCatalog _catalog;
    private readonly MinecraftInstaller _installer;

    private string _gameDirectory = string.Empty;
    private string? _selectedVersion;
    private LoaderOption _selectedLoader;
    private string? _selectedLoaderVersion = LatestLoaderVersionLabel;
    private int _selectedParallelism = 16;
    private SourceModeOption _selectedSourceMode;
    private bool _downloadAssets = true;
    private bool _writeLauncherProfile = true;
    private bool _autoJava = true;
    private bool _isBusy;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _statusText = "就绪";
    private string _progressDetail = "选择版本后点击“开始下载 / 安装”。";
    private string _speedText = string.Empty;
    private string _summaryText = string.Empty;
    private CancellationTokenSource? _cancellation;

    public MainViewModel()
    {
        _catalog = new MinecraftCatalog();
        _installer = new MinecraftInstaller(_catalog.Sources);

        GameDirectory = ResolveDefaultGameDirectory();

        Loaders =
        [
            new LoaderOption("原版（Vanilla）", LoaderKind.Vanilla),
            new LoaderOption("Fabric", LoaderKind.Fabric),
            new LoaderOption("Quilt", LoaderKind.Quilt),
            new LoaderOption("Forge", LoaderKind.Forge),
            new LoaderOption("NeoForge", LoaderKind.NeoForge),
        ];
        _selectedLoader = Loaders[0];

        LoaderVersions = [LatestLoaderVersionLabel];
        ParallelismOptions = [8, 16, 32];
        SourceModes =
        [
            new SourceModeOption("自动测速（推荐）", [], PreferPriority: false, Probe: true),
            new SourceModeOption("按优先级（官方优先）", [], PreferPriority: true, Probe: false),
            new SourceModeOption("优先 BMCLAPI 镜像", ["bmclapi", "mojang"], PreferPriority: true, Probe: false),
            new SourceModeOption("仅官方源", ["mojang", "fabric", "quilt", "forge", "neoforge"], PreferPriority: true, Probe: false),
        ];
        _selectedSourceMode = SourceModes[0];

        NativeStatusText = "原生模块：" + NativeStatus.Describe();
        FooterText = "输出为标准的 .minecraft 目录结构，可被任意启动器直接识别。";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Versions { get; } = [];

    public ObservableCollection<string> LoaderVersions { get; }

    public ObservableCollection<LogLine> LogLines { get; } = [];

    public IReadOnlyList<LoaderOption> Loaders { get; }

    public IReadOnlyList<int> ParallelismOptions { get; }

    public IReadOnlyList<SourceModeOption> SourceModes { get; }

    public string NativeStatusText { get; }

    public string FooterText { get; }

    public string GameDirectory
    {
        get => _gameDirectory;
        set => SetField(ref _gameDirectory, value);
    }

    public string? SelectedVersion
    {
        get => _selectedVersion;
        set => SetField(ref _selectedVersion, value);
    }

    public LoaderOption SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (SetField(ref _selectedLoader, value))
            {
                OnPropertyChanged(nameof(LoaderVersionEnabled));
            }
        }
    }

    public string? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set => SetField(ref _selectedLoaderVersion, value);
    }

    public int SelectedParallelism
    {
        get => _selectedParallelism;
        set => SetField(ref _selectedParallelism, value <= 0 ? 16 : value);
    }

    public SourceModeOption SelectedSourceMode
    {
        get => _selectedSourceMode;
        set => SetField(ref _selectedSourceMode, value);
    }

    public bool DownloadAssets
    {
        get => _downloadAssets;
        set => SetField(ref _downloadAssets, value);
    }

    public bool WriteLauncherProfile
    {
        get => _writeLauncherProfile;
        set => SetField(ref _writeLauncherProfile, value);
    }

    public bool AutoJava
    {
        get => _autoJava;
        set => SetField(ref _autoJava, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public bool CanStart => !IsBusy;

    public bool LoaderVersionEnabled => SelectedLoader.Kind != LoaderKind.Vanilla;

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetField(ref _isProgressIndeterminate, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => SetField(ref _progressDetail, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetField(ref _speedText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    /// <summary>Loads the version manifest and selects the newest release.</summary>
    public async Task InitializeAsync()
    {
        AppendLog(LogLevel.Info, "正在获取版本清单…", "启动");
        await RefreshVersionsAsync(force: false).ConfigureAwait(true);
    }

    /// <summary>Reloads the version list from the manifest.</summary>
    public async Task RefreshVersionsAsync(bool force)
    {
        var reporter = new UiReporter(this);
        try
        {
            var manifest = await _catalog.GetManifestAsync(null, force, reporter).ConfigureAwait(true);
            Versions.Clear();
            foreach (var version in manifest.Versions.Where(v => v.IsRelease).Select(v => v.Id))
            {
                Versions.Add(version);
            }

            if (string.IsNullOrWhiteSpace(SelectedVersion))
            {
                SelectedVersion = manifest.Latest.Release ?? Versions.FirstOrDefault();
            }

            AppendLog(LogLevel.Info, $"版本清单就绪，共 {Versions.Count} 个正式版（最新 {manifest.Latest.Release}）", "启动");
        }
        catch (Exception ex)
        {
            AppendLog(LogLevel.Error, "无法获取版本清单：" + ex.Message, "启动");
        }
    }

    /// <summary>Loads available loader builds for the selected Minecraft version.</summary>
    public async Task RefreshLoaderVersionsAsync()
    {
        if (SelectedLoader.Kind == LoaderKind.Vanilla)
        {
            LoaderVersions.Clear();
            LoaderVersions.Add(LatestLoaderVersionLabel);
            SelectedLoaderVersion = LatestLoaderVersionLabel;
            return;
        }

        var version = SelectedVersion?.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        try
        {
            var reporter = new UiReporter(this);
            var versions = await _catalog
                .GetLoaderVersionsAsync(SelectedLoader.Kind, version, reporter)
                .ConfigureAwait(true);

            LoaderVersions.Clear();
            LoaderVersions.Add(LatestLoaderVersionLabel);
            foreach (var item in versions)
            {
                LoaderVersions.Add(item);
            }

            SelectedLoaderVersion = LatestLoaderVersionLabel;
            AppendLog(LogLevel.Info, $"{SelectedLoader.Name} 可用版本 {versions.Count} 个（最新 {(versions.Count > 0 ? versions[0] : "无")}）", "Loader");
        }
        catch (Exception ex)
        {
            LoaderVersions.Clear();
            LoaderVersions.Add(LatestLoaderVersionLabel);
            SelectedLoaderVersion = LatestLoaderVersionLabel;
            AppendLog(LogLevel.Warning, $"无法查询 {SelectedLoader.Name} 版本列表（{ex.Message}），将使用最新稳定版", "Loader");
        }
    }

    /// <summary>Runs install (or verify) in the background and mirrors progress into the UI.</summary>
    public async Task RunAsync(bool verifyOnly)
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedVersion))
        {
            AppendLog(LogLevel.Warning, "请先选择或输入 Minecraft 版本。", "安装");
            return;
        }

        IsBusy = true;
        SummaryText = string.Empty;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        LogLines.Clear();
        _cancellation = new CancellationTokenSource();
        var reporter = new UiReporter(this);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var options = BuildOptions(verifyOnly);
            AppendLog(LogLevel.Info,
                verifyOnly
                    ? $"开始校验 {options.MinecraftVersion}（{SelectedLoader.Name}）"
                    : $"开始安装 {options.MinecraftVersion}（{SelectedLoader.Name}）到 {options.GameDir}",
                "安装");

            var result = await _installer.InstallAsync(options, reporter, _cancellation.Token).ConfigureAwait(true);

            ProgressValue = 100;
            IsProgressIndeterminate = false;
            StatusText = result.Success ? "完成" : "部分失败";
            ProgressDetail = verifyOnly
                ? "文件校验结束"
                : $"启动版本 ID：{result.VersionId}";
            SummaryText =
                $"版本 {result.VersionId} · 下载 {result.Downloaded} 个 / 跳过 {result.Skipped} 个 / 失败 {result.Failed} 个 · " +
                $"{PathEx.FormatBytes(result.Bytes)} · 用时 {result.Elapsed:hh\\:mm\\:ss}";

            foreach (var warning in result.Warnings.Distinct().Take(8))
            {
                AppendLog(LogLevel.Warning, warning, "提示");
            }

            foreach (var error in result.Errors.Distinct().Take(8))
            {
                AppendLog(LogLevel.Error, error, "错误");
            }

            if (result.Success && !verifyOnly)
            {
                AppendLog(LogLevel.Info, "安装完成，现在可以在启动器里选择该版本启动。", "完成");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            ProgressDetail = "已取消；已经下载的文件会保留，下次继续。";
            AppendLog(LogLevel.Warning, "用户取消了本次操作。", "安装");
        }
        catch (Exception ex)
        {
            StatusText = "失败";
            ProgressDetail = ex.Message;
            SummaryText = ex.Message;
            AppendLog(LogLevel.Error, ex.Message, "安装");
            if (ex is InstallFailedException failed)
            {
                foreach (var detail in failed.Details.Take(8))
                {
                    AppendLog(LogLevel.Error, detail, "细节");
                }
            }
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
            _ = stopwatch;
        }
    }

    public void Cancel()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Releases the cancellation source when the window closes.</summary>
    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    /// <summary>Called by <see cref="UiReporter"/>; safe from any thread.</summary>
    public void AppendLog(LogLevel level, string message, string? scope = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => AppendLog(level, message, scope));
            return;
        }

        LogLines.Add(new LogLine(level, message, scope));
        // Drop a chunk at once instead of one line per event to keep the UI collection quiet.
        if (LogLines.Count > 3000)
        {
            while (LogLines.Count > 2500)
            {
                LogLines.RemoveAt(0);
            }
        }
    }

    /// <summary>Called by <see cref="UiReporter"/>; safe from any thread.</summary>
    public void ApplyProgress(ProgressUpdate update)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyProgress(update));
            return;
        }

        StatusText = PhaseText(update.Phase);

        if (update.BytesTotal > 0)
        {
            IsProgressIndeterminate = false;
            ProgressValue = Math.Clamp(update.BytesDone * 100.0 / update.BytesTotal, 0, 100);
        }
        else
        {
            IsProgressIndeterminate = true;
        }

        var items = update.ItemsTotal > 0 ? $" · {update.ItemsDone}/{update.ItemsTotal} 个文件" : string.Empty;
        var total = update.BytesTotal > 0 ? $" / {PathEx.FormatBytes(update.BytesTotal)}" : string.Empty;
        var current = string.IsNullOrWhiteSpace(update.CurrentItem) ? string.Empty : $" · {update.CurrentItem}";
        ProgressDetail = $"{PathEx.FormatBytes(update.BytesDone)}{total}{items}{current}";
        SpeedText = update.BytesPerSecond > 0 ? PathEx.FormatBytes((long)update.BytesPerSecond) + "/s" : string.Empty;
    }

    private InstallOptions BuildOptions(bool verifyOnly)
    {
        var loaderVersion = SelectedLoaderVersion;
        if (string.IsNullOrWhiteSpace(loaderVersion) ||
            string.Equals(loaderVersion, LatestLoaderVersionLabel, StringComparison.Ordinal))
        {
            loaderVersion = null;
        }

        var mode = SelectedSourceMode;
        return new InstallOptions
        {
            GameDir = GameDirectory,
            MinecraftVersion = SelectedVersion!.Trim(),
            Loader = SelectedLoader.Kind,
            LoaderVersion = loaderVersion,
            DownloadAssets = DownloadAssets && !verifyOnly,
            MaxParallel = SelectedParallelism,
            SourceOrder = mode.Order,
            PreferSourcePriority = mode.PreferPriority,
            ProbeSources = mode.Probe && !verifyOnly,
            CreateLauncherProfile = WriteLauncherProfile && !verifyOnly,
            EnsureJava = AutoJava && !verifyOnly && SelectedLoader.Kind != LoaderKind.Vanilla,
            VerifyOnly = verifyOnly,
        };
    }

    /// <summary>
    /// Picks the default game directory. Some locked-down Windows profiles refuse writes under
    /// %APPDATA%; in that case a portable folder next to the executable is used instead.
    /// </summary>
    private static string ResolveDefaultGameDirectory()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            candidates.Add(Path.Combine(roaming, ".minecraft"));
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            candidates.Add(Path.Combine(documents, ".minecraft"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, ".minecraft"));

        foreach (var candidate in candidates)
        {
            if (IsWritable(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".downcraft-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string PhaseText(InstallPhase phase) => phase switch    {
        InstallPhase.ResolvingSources => "正在测速下载源…",
        InstallPhase.ReadingManifest => "正在读取版本信息…",
        InstallPhase.DownloadingClient => "正在下载客户端…",
        InstallPhase.DownloadingLibraries => "正在下载依赖库…",
        InstallPhase.ExtractingNatives => "正在解压 natives…",
        InstallPhase.DownloadingAssets => "正在下载资源文件…",
        InstallPhase.ResolvingLoader => "正在查询加载器版本…",
        InstallPhase.InstallingLoader => "正在安装加载器…",
        InstallPhase.ProvisioningJava => "正在准备 Java 运行时…",
        InstallPhase.WritingProfiles => "正在写入启动器配置…",
        InstallPhase.Verifying => "正在校验文件…",
        InstallPhase.Done => "完成",
        _ => "处理中…",
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Bridges the core reporter callbacks onto the WPF view model.</summary>
internal sealed class UiReporter : Reporter
{
    private readonly MainViewModel _viewModel;

    public UiReporter(MainViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    protected override void OnLog(LogEntry entry) => _viewModel.AppendLog(entry.Level, entry.Message, entry.Scope);

    protected override void OnProgress(ProgressUpdate update) => _viewModel.ApplyProgress(update);
}
