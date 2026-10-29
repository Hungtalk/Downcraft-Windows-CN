# 架构说明

## 设计目标

1. **产物可用**：装出来的目录必须能被官方启动器与主流第三方启动器直接识别，不做「私有格式」。
2. **网络多源**：同一份文件尽量能从多个站点取；单个站点失败/被墙/被限速时自动切换。
3. **可诊断**：任何失败都要能定位到「哪个站、哪个文件、什么错误」，而不是一句「下载失败」。
4. **C++ 与 .NET 各司其职**：原生代码只出现在真正值得的地方（大文件 SHA-1、ZIP 解压、磁盘查询），
   且永远有托管回退，不把「必须编译 C++」当成使用门槛。

## 分层

```
┌─────────────────────────────┐   ┌─────────────────────────────┐
│ DownCraft.Gui (WPF)        │   │ DownCraft.Cli (downcraft)       │
│ MainViewModel / UiReporter  │   │ ConsoleReporter / Commands  │
└──────────────┬──────────────┘   └──────────────┬──────────────┘
               │  IReporter (日志 + 进度)          │
┌──────────────▼──────────────────────────────────▼──────────────┐
│ DownCraft.Core                                                 │
│  MinecraftInstaller ── 编排：清单→版本→原版→加载器→启动器配置     │
│  VanillaInstaller    ── 生成/执行 InstallPlan（client/库/资源）   │
│  LoaderInstaller     ── Fabric/Quilt 合并；Forge/NeoForge 安装器  │
│  JavaRuntimeManager  ── 定位/下载 Mojang Java 运行时             │
│  VersionManifestService ── 元数据获取 + 本地缓存                  │
│  Downloader          ── 多源路由、重试、续传、哈希校验、并发      │
│  SourceRouter/Catalog/Health/Breaker ── 下载源与排序             │
│  Meta / Util / Native ── 模型、规则、路径、SHA-1、ZIP            │
└──────────────┬──────────────────────────────────────────────────┘
               │ DllImport("downcraft_native.dll", Cdecl)
┌──────────────▼──────────────┐
│ downcraft_native.dll (C++20, /MT)  │  SHA-1(文件/内存)、CRC32、ZIP 中央目录解析 + 解压、磁盘空间
└─────────────────────────────┘
```

## 关键类型

| 类型 | 职责 |
| --- | --- |
| `MinecraftInstaller` | 唯一对外的安装入口；负责探测源、构建 `Downloader`、串起原版与加载器、写 `launcher_profiles.json` |
| `InstallOptions` / `InstallResult` | 输入参数（不可变）与结果统计（下载/跳过/失败数、字节数、警告） |
| `VanillaInstaller` | 把 `VersionMeta` 转成 `InstallPlan`，再分批下载、解压 natives、复制旧版资源 |
| `InstallPlan` | 纯数据：`DownloadTask` 列表 + `NativesJob` + `AssetCopyJob`，便于推理与测试 |
| `Downloader` | HTTP 执行器：单文件多候选 URL、Rang 续传、SHA-1 校验、失败换源、熔断 |
| `SourceRouter` | `ArtifactKind + SourceContext → 有序 URL 列表`（模板展开、评分、去重） |
| `SourceCatalog` | 下载源集合：内置 JSON、用户覆盖、`WithOrder` |
| `SourceHealthStore` | 各源实测延迟与失败次数，落盘 `<gameDir>/.downcraft/source-health.json` |
| `SourceBreaker` | 会话内熔断：某源对某类文件连续失败 3 次后本轮跳过 |
| `LoaderInstaller` | Fabric/Quilt 元数据合并；Forge/NeoForge 安装器下载、依赖预置、执行、结果识别 |
| `JavaRuntimeManager` | `JAVA_HOME`/PATH/常见安装目录/自带 runtime 探测；缺失时按 Mojang 清单下载 |
| `VersionManifestService` | 清单与版本 JSON 的获取、校验、缓存（失败时回退旧缓存） |

## 安装流水线

```
1. 源探测（可选）      SourceProber 并发测延迟 → 写 SourceHealthStore
2. 版本清单            version_manifest_v2.json（缓存 1h；网络失败用旧缓存）
3. 版本 JSON           versions/<id>/<id>.json（校验清单里的 SHA-1；本地优先）
4. 生成计划            client.jar / logging / libraries(+natives) / asset index + objects
5. 下载（分批）        非资源文件 → 资源对象；并发、续传、坏文件换源
6. 后处理              解压旧版 natives；旧版 virtual / map_to_resources 复制
7. 加载器              Fabric/Quilt: 下载 profile JSON + 库；Forge/NeoForge: 安装器
8. 启动器配置          launcher_profiles.json（保留用户已有条目）
9. 收尾                保存源健康数据；输出统计
```

### 原版细节

- **rules 求值**（`RuleEvaluator`）：空规则=允许；否则「最后一条匹配的规则生效，默认拒绝」，
  与官方启动器一致；`os.arch` 的 `x86` 同时匹配 `x86_64`，`arm64` 不匹配。
- **库坐标**（`MavenCoordinate`）：`group:artifact:version[:classifier][@ext]` → Maven 相对路径。
  优先用 `downloads.artifact` 的 path/url；Fabric/Quilt profile 只有 `url` 根地址时按坐标拼。
- **旧版 natives**：`natives` + `downloads.classifiers` 的版本（≤1.18）下载对应 classifier，
  解压到 `versions/<id>/natives`，遵守 `extract.exclude`；1.19+ 的 natives 是普通库，只下载不解压。
- **资源文件**：索引写入 `assets/indexes/<id>.json`，对象按 SHA-1 前两位分桶；
  `virtual` → `assets/virtual/<index>/<name>`，`map_to_resources` → `<gameDir>/resources/<name>`。

### 加载器细节

| 加载器 | 机制 | 是否需要 Java |
| --- | --- | --- |
| Fabric | 取 `meta.fabricmc.net` 的 profile JSON，原样写入 `versions/<id>/<id>.json`（`inheritsFrom`），再下载其 `libraries` | 否 |
| Quilt | 同上，走 `meta.quiltmc.org` v3 | 否 |
| Forge | 下载官方 installer jar → 用 Java 跑 `--installClient <dir>`（对 client.jar 做 binpatch） | 是 |
| NeoForge | 同上；1.20.1 用旧坐标 `net.neoforged:forge`，1.20.2+ 用 `net.neoforged:neoforge` | 是 |

Forge/NeoForge 的两个工程化处理：

1. **依赖预置**：读取安装器内 `install_profile.json`，把 `libraries` 与 `processors[].classpath`
   先用镜像下到 `libraries/`，安装器发现本地已有就不会再去连（常常不可达的）官方 Maven。
2. **临时目录隔离**：给安装器传 `-Djava.io.tmpdir=<gameDir>/.downcraft/tmp`。
   某些安全软件会阻止「D 盘程序写 C 盘 TEMP」，这曾导致 Forge 处理器 `AccessDeniedException`；
   隔离后稳定复现成功。

安装完成后不一定能靠「预测 ID」找目录（安装器可能复用已有目录），所以用
「期望 ID → 新增目录 → `inheritsFrom` 匹配的最新 profile」三级策略识别结果。

## 多源路由

```
DownloadTask(Kind, SourceContext{VersionId, MavenPath, AssetHash, OriginalUrl, Sha1…})
        │
        ├─ SourceRouter.Build(kind, ctx)
        │     ├─ 每个启用源：有该 kind 的模板 → 展开占位符
        │     │                无模板但 useOriginalUrl → 用 ctx.OriginalUrl
        │     ├─ 评分：auto = 实测延迟×10 + 优先级；priority = 优先级×100000 + 延迟
        │     └─ 追加 canonical URL 作为最后兜底；按 URL 去重
        │
        ├─ SourceBreaker：跳过本轮已连续失败 3 次的 (源, Kind)（全部被跳过时解除）
        └─ 逐个尝试：成功→记录健康；403/404→换源；5xx/超时→同 URL 重试一次后换源
```

失败语义分层：

- `HashMismatchException`：内容不对，删 `.part`，立即换源；
- `HttpRequestException`：按状态码区分「该源没有」与「暂时性错误」；
- `DownloadFailedException`：所有候选都失败；
- `InstallFailedException`：安装级失败，携带可展示的细节列表（CLI/GUI 都直接显示）。

## 原生模块

`native/include/downcraft_native.h` 定义 9 个 C ABI：`dc_version`、`dc_sha1_file`、
`dc_sha1_bytes`、`dc_crc32_file`、`dc_zip_entry_size`、`dc_zip_read`、
`dc_zip_extract`、`dc_zip_extract_all`、`dc_disk_free`。

- 路径用 UTF-16，ZIP 条目名用 UTF-8（ZIP 规范）；所有函数捕获异常并返回负错误码。
- `dc_zip_extract_all` 拒绝 `..`、绝对路径、盘符与 ADS，防 zip-slip。
- 实现细节：CNG `bcrypt` 做 SHA-1/CRC32（本机 Windows 10 无 CRC32 算法时回退查表实现）；
  ntdll `RtlDecompressBufferEx` 做 deflate（本机不支持时回退自带 RFC 1951 实现）；
  `/MT` 静态 CRT，只依赖 `KERNEL32`/`bcrypt`/`ntdll`。
- 托管侧 `NativeInterop` 每次调用都可能失败 → `Sha1` / `ZipService` 自动回退到
  `System.Security.Cryptography` / `ZipArchive`。`downcraft doctor` 会显示 `downcraft_native x.y (msvc)`
  或回退原因。

## 并发与进度

- `Parallel.ForEachAsync` + `SemaphoreSlim` 由 `MaxDegreeOfParallelism` 控制并发；
  连接池 `MaxConnectionsPerServer` 与之对齐。
- `ProgressPump` 把字节/条目计数聚合成结构体快照，**限流 ~8 次/秒**，避免刷屏或压垮 UI 线程。
- 单文件超时 = `100s + 预期大小 / 64KB/s`（封顶 30 分钟），避免大文件被固定超时误杀。
- 下载先写 `<file>.downcraft-part`，校验通过后原子改名；失败回滚进度计数。

## 失败与恢复

| 场景 | 行为 |
| --- | --- |
| 版本清单获取失败但有缓存 | 用旧缓存继续，并警告 |
| 版本 JSON 写盘失败（权限/杀软） | 警告，继续用内存数据 |
| 元数据缓存目录不可写 | 警告，不使用缓存，不阻断安装 |
| 单文件全部源失败 | 记录到结果 `Errors`，原始关键文件失败则整体失败；资源文件失败只警告 |
| 中断（Ctrl+C） | 保留 `.part`，下次续传；退出码 130 |
| 重复执行 | 先比大小与 SHA-1，命中即跳过（幂等） |

## 扩展点

- **加下载站**：编辑 `%APPDATA%\DownCraft\sources.json`（或 `downcraft sources export` 后改），
  新增一个 `DownloadSource`，写模板即可，无需改代码。见 [SOURCES.md](SOURCES.md)。
- **加文件类型**：`ArtifactKind` 加枚举 + 对应源端点。
- **换进度呈现**：实现 `IReporter`（CLI `ConsoleReporter`、GUI `UiReporter` 都是这样做的）。
- **加加载器**：在 `LoaderInstaller` 里加分支；纯元数据的加载器（如 Fabric 系）几乎零成本。

## 测试策略

- 单元测试（xunit.v3，148 个）覆盖规则求值、Maven 路径、模板展开/排序、源目录完整性、
  JSON 容错（含 Fabric 的无冒号时区）、路径安全、SHA-1、ZIP 解压与 zip-slip、启动器配置合并。
- 真实网络验证靠 `scripts/smoke-test.ps1` 与手工矩阵（原版/4 种加载器/最新版/幂等/校验）。
- 测试不依赖固定的公网资源内容，也不写用户真实 `.minecraft`（`TempDirectory` 建在测试输出目录下）。
