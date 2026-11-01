# Changelog

All notable changes to DownCraft are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-29

First public release.

### Added

- **Vanilla installer** — writes a standard `.minecraft` layout: `client.jar`, `libraries`
  (Mojang `rules` filtering, legacy `<1.19` natives extraction), `assets` (index + hashed
  objects, plus legacy `virtual` / `map_to_resources` layouts) and the logging config.
- **Mod loaders**
  - Fabric and Quilt: official profile JSON merged as `versions/<id>/<id>.json` with
    `inheritsFrom`; no JVM required.
  - Forge and NeoForge: official installer executed with a provisioned JVM
    (`--installClient`), including NeoForge 1.20.1's legacy `net.neoforged:forge` coordinates.
  - Installer dependencies (`install_profile.json` libraries + processor classpath) are
    pre-seeded through the mirror chain, and the installer's scratch directory is isolated
    inside the game folder so locked-down `%TEMP%` locations cannot break post-processing.
- **Seven download sites** — Mojang, BMCLAPI, Fabric, Quilt, Forge Maven, NeoForge Maven and
  the Aliyun Maven mirror, described entirely by `sources.json` so new mirrors need no code.
- **Reliable transfers** — latency probing with automatic ordering, per-file mirror fallback,
  `.downcraft-part` resume, SHA-1 + size verification, corrupted-file requeue and a per-(source, kind)
  circuit breaker.
- **Java runtime provisioning** — Mojang Java runtimes (`jre-legacy` … `java-runtime-delta`)
  are downloaded into `runtime/<component>/<platform>/<component>` when no suitable JVM exists.
- **Launcher integration** — a merged `launcher_profiles.json` entry and standard version
  folders that HMCL, PCL2, PrismLauncher and the official launcher can pick up unchanged.
- **CLI `downcraft`** — `install`, `verify` (read-only integrity check), `list`, `loaders`,
  `sources`, `java`, `doctor`.
- **WPF GUI** — single window with live phase/byte/speed progress, coloured log, cancellation
  and one-click verification.
- **Native helper `downcraft_native.dll`** (C++20, static CRT) — SHA-1, CRC-32, ZIP
  parse/extract with zip-slip protection, free-space query; every call has a managed fallback,
  so the product runs without the DLL.
- **Tests and tooling** — 148 xunit.v3 tests on Microsoft.Testing.Platform, `scripts/build.ps1`,
  `scripts/build-native.ps1`, `scripts/publish.ps1`, `scripts/release.ps1`,
  `scripts/smoke-test.ps1`, and CI on `windows-latest`.

### Known limitations

- DownCraft only prepares files: it does not launch the game, manage accounts or install mods.
- Forge/NeoForge require running the official installers (they patch `client.jar`), therefore a
  JVM is needed; without one the tool degrades to placing the launch profile only.
- The native ZIP reader rejects ZIP64 and unusual compression methods; those archives are
  handled by the managed `ZipArchive` fallback instead.
- No built-in proxy UI; system / `HTTP_PROXY` / `HTTPS_PROXY` settings are honoured.

[1.0.0]: https://github.com/Hungtalk/Dawncraft-Windows-CN/releases/tag/v1.0.0
