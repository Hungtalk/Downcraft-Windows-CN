# 下载源（Sources）

McDownload 的下载源是**纯数据**：`src/McDownload.Core/Sources/default-sources.json` 内置一份，
用户可以用 `%APPDATA%\McDownload\sources.json` 覆盖，或 `--sources-file <path>` 临时指定。
新增一个镜像不需要改代码、不需要重新编译。

## 模型

```jsonc
{
  "version": 1,
  "sources": [
    {
      "id": "bmclapi",                  // 唯一 ID，--source 用它排序
      "displayName": "BMCLAPI（国内镜像）",
      "region": "cn",                   // global / cn / custom，仅展示用
      "priority": 150,                  // 越小越优先（auto 模式下作为同延迟时的次序）
      "enabled": true,
      "useOriginalUrl": false,          // true：没写模板的文件类型直接使用元数据里的原始 URL
      "probeUrl": "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
      "homepage": "https://bmclapi2.bangbang93.com/",
      "endpoints": {
        "versionManifest": "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
        "library": { "url": "https://bmclapi2.bangbang93.com/maven/{path}", "priority": 250 }
      }
    }
  ]
}
```

- `endpoints` 的键是 `ArtifactKind` 的枚举名（大小写不敏感，加载时会规范化）。
- 端点值可以是字符串，也可以是 `{ "url": ..., "priority": ... }`，后者用于**同一源内**给某类文件单独排序。
  （例：BMCLAPI 的 `forgeInstaller` 优先级设成 900，因为该路径实际不被镜像，让官方 Forge Maven 先试。）
- 某个端点展开失败（模板占位符没有值）时，该源对该文件类型**自动不参与**，不会产生坏 URL。

## 占位符

| 占位符 | 含义 | 来源 |
| --- | --- | --- |
| `{version}` / `{minecraftVersion}` | 游戏版本，如 `1.20.1` | 版本清单 |
| `{loader}` | 加载器完整版本，如 `1.20.1-47.4.28`、`21.1.265` | 加载器解析 |
| `{path}` | Maven 相对路径，如 `net/minecraftforge/forge/1.20.1-47.4.28/forge-…-installer.jar` | 库/安装器 |
| `{hash}` / `{hash2}` | 资源对象 SHA-1 及其前两位 | 资源索引 |
| `{index}` | 资源索引 ID，如 `5` | 版本 JSON |
| `{component}` / `{platform}` | Java 运行时组件名 / 平台（`windows-x64`） | Java 管理 |
| `{file}` | 文件名 | 各类 |
| `{sha1}` | 文件 SHA-1 | client.jar 等 |
| `{url}` | 元数据里的 canonical URL | 各类 |

## 文件类型（ArtifactKind）与内置覆盖

| 类型 | mojang | bmclapi | fabric | quilt | forge | neoforge | aliyun-maven |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `VersionManifest` | ✅ | ✅ | | | | | |
| `VersionJson` | 原始 URL | ✅ `/version/{version}/json` | | | | | |
| `ClientJar` | ✅ | ✅ `/version/{version}/client` | | | | | |
| `AssetIndex` | 原始 URL | ✖（上游 404） | | | | | |
| `AssetObject` | ✅ | ✅ `/assets/{hash2}/{hash}` | | | | | |
| `Library` | ✅ | ✅ `/maven/{path}` | | | | | ✅ 中央仓兜底 |
| `LoggingConfig` | 原始 URL | | | | | | |
| `FabricLoaderList` / `FabricProfile` | | ✅ `/fabric-meta/…` | ✅ | | | | |
| `QuiltLoaderList` / `QuiltProfile` | | | | ✅ | | | |
| `ForgeInstaller` / `ForgeMavenMetadata` | | ⚠ 实测 403 | | | ✅ | | |
| `ForgePromotions` | | | | | ✅ | | |
| `NeoForgeInstaller` / `NeoForgeMavenMetadata` | | ✅ | | | | ✅ | |
| `NeoForgeLegacyMavenMetadata`（1.20.1） | | ✅ | | | | ✅ | |
| `JavaRuntimeManifest` | ✅ | ✅ | | | | | |
| `JavaRuntimeFile` | 原始 URL | | | | | | |

## 本机实测记录（2026-10，中国电信网络）

这些是写这份代码时真实观察到的现象，直接决定了排序策略：

- **`maven.neoforged.net` 无法连接**（`connection refused`）。NeoForge 官方 Maven 在本网络不可用，
  而其 BMCLAPI 镜像 `/maven/net/neoforged/neoforge/…` 正常 → NeoForge 默认会走镜像。
- **BMCLAPI 的 `/maven/` 对部分路径返回 Cloudflare 403**：`com.fasterxml.jackson…`、`org.lwjgl…`、
  `net.minecraftforge…` 等（通常是它本身没缓存、又被上游镜像拒绝的构件）。
  这些文件会立刻回退到官方 `libraries.minecraft.net` / `maven.minecraftforge.net`，
  且连续 3 次失败后本轮不再尝试该组合（`SourceBreaker`），避免几千个资源逐个吃 403。
- **BMCLAPI 不镜像资源索引**（`/assets/indexes/5.json` → 404），索引走官方（文件很小）。
- **BMCLAPI 不镜像 Forge 安装器**（`/maven/net/minecraftforge/…-installer.jar` → 403），
  Forge 安装器因此以官方 Maven 为首选。
- 延迟参考（同机）：阿里云 Maven ≈ 45 ms、BMCLAPI ≈ 400–900 ms、Fabric/Quilt/Forge Maven ≈ 0.9–1.3 s、
  Mojang 官方 ≈ 1.0–4.0 s。`auto` 模式实测排序会自动把快的放前面。
- 资源对象从 BMCLAPI 下载约 3 MB/s（3575 个文件 / 621 MB / 约 3.5 分钟），受小文件开销限制。

## 排序与健康数据

- `--priority`（或 GUI 选「按配置优先级」）：完全按 `priority` / 端点 `priority`。
- 默认 `auto`：先对每个启用源的 `probeUrl` 发一个 Range 请求测延迟，评分 `延迟×10 + 优先级`；
  失败源分数降到最低。结果存 `<gameDir>/.mcdl/source-health.json`，下次免测也有参考。
- 无论哪种模式，**canonical URL 永远作为最后一个候选**，所以即使配置写错也不会 404 死。
- 运行期熔断：`(源, 文件类型)` 连续 3 次失败 → 本轮跳过；所有候选都被跳过后自动解除。

## 自定义镜像

```powershell
# 1) 导出当前内置配置
mcdl sources export -o D:\mirror\sources.json

# 2) 编辑：加一个自己的源（示例：内网缓存服务器）
#    {
#      "id": "intranet",
#      "displayName": "内网缓存",
#      "priority": 1,
#      "probeUrl": "http://mirror.local/mc/game/version_manifest_v2.json",
#      "endpoints": {
#        "versionManifest": "http://mirror.local/mc/game/version_manifest_v2.json",
#        "versionJson": "http://mirror.local/version/{version}/json",
#        "clientJar": "http://mirror.local/version/{version}/client",
#        "assetObject": "http://mirror.local/assets/{hash2}/{hash}",
#        "library": "http://mirror.local/maven/{path}"
#      }
#    }

# 3) 使用
mcdl install 1.20.1 --sources-file D:\mirror\sources.json
```

也可以把它放到用户配置位置，之后所有命令自动生效（`mcdl sources path` 显示该路径）：

```powershell
Copy-Item D:\mirror\sources.json "$env:APPDATA\McDownload\sources.json"
mcdl sources list
```

启用/禁用某个源：

```powershell
mcdl sources disable neoforge
mcdl sources enable neoforge
```

> 用户配置文件损坏时会**自动回退到内置配置**并给出警告，不会因为改错 JSON 而无法安装。

## 强制使用某类站点

```powershell
mcdl install 1.20.1 --source bmclapi,mojang     # 优先这两个（按给定顺序），其余仍作兜底
mcdl install 1.20.1 --source mojang --priority  # 优先官方，且按优先级而非延迟
mcdl doctor                                     # 先看各源当前是否可达、延迟多少
```

## 礼貌使用

镜像由社区/高校/企业无偿提供。请：

- 不要开超高并发「压测」镜像（默认 16 并发已经够快）；
- 不要频繁 `--refresh` 反复拉清单（默认缓存 1 小时）；
- 分发本工具时不要内置私有镜像的鉴权信息。
