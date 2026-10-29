using McDownload.Core.Diagnostics;
using McDownload.Core.Meta;
using McDownload.Core.Net;
using McDownload.Core.Sources;

namespace McDownload.Tests;

public class JsonModelTests
{
    private const string VersionJson = """
    {
      // third-party launchers emit comments and trailing commas
      "id": "1.20.1",
      "type": "release",
      "mainClass": "net.minecraft.client.main.Main",
      "assets": "17",
      "complianceLevel": "1",
      "minimumLauncherVersion": 21,
      "releaseTime": "2023-06-12T13:25:51+00:00",
      "time": "2023-06-12T13:25:51+00:00",
      "assetIndex": {
        "id": "17",
        "sha1": "abcdef0123456789abcdef0123456789abcdef01",
        "size": "1234",
        "totalSize": 5678,
        "url": "https://piston-meta.mojang.com/v1/packages/17.json"
      },
      "downloads": {
        "client": {
          "sha1": "0123456789abcdef0123456789abcdef01234567",
          "size": "2345",
          "url": "https://piston-data.mojang.com/v1/objects/client.jar"
        }
      },
      "libraries": [
        {
          "name": "com.example:example-lib:1.0.0",
          "downloads": {
            "artifact": {
              "path": "com/example/example-lib/1.0.0/example-lib-1.0.0.jar",
              "sha1": "fedcba9876543210fedcba9876543210fedcba98",
              "size": 42,
              "url": "https://libraries.minecraft.net/example.jar"
            }
          },
          "rules": [
            { "action": "allow", "os": { "name": "windows" } },
          ]
        },
        {
          "name": "com.example:mac-lib:2.0.0",
          "rules": [ { "action": "disallow", "os": { "name": "windows" } } ]
        }
      ],
      "javaVersion": { "component": "java-runtime-gamma", "majorVersion": 17 },
      "arguments": { "game": [], "jvm": [] },
    }
    """;

    private const string ManifestJson = """
    {
      "latest": { "release": "1.21.4", "snapshot": "25w02a" },
      "versions": [
        {
          "id": "1.21.4",
          "type": "release",
          "url": "https://piston-meta.mojang.com/v1/packages/1.21.4.json",
          "sha1": "1111111111111111111111111111111111111111",
          "time": "2024-12-03T10:12:57+00:00",
          "releaseTime": "2024-12-03T10:12:57+00:00",
          "complianceLevel": 1
        },
        {
          "id": "25w02a",
          "type": "snapshot",
          "url": "https://piston-meta.mojang.com/v1/packages/25w02a.json",
          "sha1": "2222222222222222222222222222222222222222",
          "releaseTime": "2025-01-08T12:00:00+00:00"
        },
        {
          "id": "1.21.3",
          "type": "release",
          "url": "https://piston-meta.mojang.com/v1/packages/1.21.3.json",
          "sha1": "3333333333333333333333333333333333333333"
        }
      ]
    }
    """;

    [Fact]
    public void ParsesVersionJsonWithRulesDownloadsAssetsAndJavaVersion()
    {
        var meta = MinecraftJson.Deserialize<VersionMeta>(VersionJson);

        Assert.NotNull(meta);
        Assert.Equal("1.20.1", meta!.Id);
        Assert.Equal("release", meta.Type);
        Assert.Equal("net.minecraft.client.main.Main", meta.MainClass);
        Assert.Equal("17", meta.Assets);
        Assert.Equal(1, meta.ComplianceLevel);
        Assert.Equal(21, meta.MinimumLauncherVersion);
        Assert.Equal(new DateTimeOffset(2023, 6, 12, 13, 25, 51, TimeSpan.Zero), meta.ReleaseTime!.Value);

        Assert.Equal("17", meta.AssetIndex!.Id);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", meta.AssetIndex.Sha1);
        Assert.Equal(1234L, meta.AssetIndex.Size);
        Assert.Equal(5678L, meta.AssetIndex.TotalSize);
        Assert.Equal("https://piston-meta.mojang.com/v1/packages/17.json", meta.AssetIndex.Url);

        Assert.Equal(2345L, meta.Downloads!.Client!.Size);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", meta.Downloads.Client.Sha1);
        Assert.Equal("https://piston-data.mojang.com/v1/objects/client.jar", meta.Downloads.Client.Url);

        Assert.Equal(2, meta.Libraries.Count);
        var exampleLibrary = meta.Libraries[0];
        Assert.Equal("com.example:example-lib:1.0.0", exampleLibrary.Name);
        Assert.Equal("com/example/example-lib/1.0.0/example-lib-1.0.0.jar", exampleLibrary.Downloads!.Artifact!.Path);
        Assert.Equal(42L, exampleLibrary.Downloads.Artifact.Size);
        Assert.Equal("https://libraries.minecraft.net/example.jar", exampleLibrary.Downloads.Artifact.Url);

        var allowRule = Assert.Single(exampleLibrary.Rules!);
        Assert.Equal("allow", allowRule.Action);
        Assert.Equal("windows", allowRule.Os!.Name);

        var macLibrary = meta.Libraries[1];
        Assert.Equal("disallow", macLibrary.Rules![0].Action);
        Assert.Equal("windows", macLibrary.Rules[0].Os!.Name);

        Assert.Equal(17, meta.JavaVersion!.MajorVersion);
        Assert.Equal("java-runtime-gamma", meta.JavaVersion.Component);

        Assert.NotNull(meta.Arguments);
        Assert.Empty(meta.Arguments!.Game!);
        Assert.Empty(meta.Arguments.Jvm!);
    }

    [Fact]
    public void ParsesFabricStyleTimestampWithoutOffsetColon()
    {
        const string json = """
        {
          "id": "fabric-loader-0.16.9-1.20.1",
          "type": "release",
          "time": "2026-10-26T19:07:24+0000",
          "releaseTime": "2026-10-26T19:07:24+0000"
        }
        """;

        var meta = MinecraftJson.Deserialize<VersionMeta>(json);

        Assert.NotNull(meta);
        Assert.NotNull(meta!.Time);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 19, 7, 24, TimeSpan.Zero), meta.Time!.Value.ToUniversalTime());
        Assert.Equal(TimeSpan.Zero, meta.Time.Value.Offset);
        Assert.Equal(meta.Time, meta.ReleaseTime);
    }

    [Fact]
    public void ParsesEmptyAndNumericTimestamps()
    {
        const string json = """
        { "id": "odd-profile", "time": "", "releaseTime": 1750000000 }
        """;

        var meta = MinecraftJson.Deserialize<VersionMeta>(json);

        Assert.NotNull(meta);
        Assert.Null(meta!.Time);
        Assert.NotNull(meta.ReleaseTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1750000000), meta.ReleaseTime!.Value);
    }

    [Fact]
    public void ParsesManifestAndSelectsReleases()
    {
        var manifest = MinecraftJson.Deserialize<VersionManifest>(ManifestJson);

        Assert.NotNull(manifest);
        Assert.Equal("1.21.4", manifest!.Latest.Release);
        Assert.Equal("25w02a", manifest.Latest.Snapshot);
        Assert.Equal(3, manifest.Versions.Count);
        Assert.True(manifest.Versions[0].IsRelease);
        Assert.False(manifest.Versions[1].IsRelease);
        Assert.Equal("1111111111111111111111111111111111111111", manifest.Versions[0].Sha1);
        Assert.Equal(
            new DateTimeOffset(2024, 12, 3, 10, 12, 57, TimeSpan.Zero),
            manifest.Versions[0].ReleaseTime!.Value);

        var releases = VersionManifestService.Releases(manifest);

        Assert.Equal(new[] { "1.21.4", "1.21.3" }, releases.Select(v => v.Id));
        Assert.All(releases, v => Assert.True(v.IsRelease));
    }

    [Fact]
    public void FindVersionIsCaseInsensitiveAndReturnsNullForUnknownIds()
    {
        var manifest = MinecraftJson.Deserialize<VersionManifest>(ManifestJson)!;

        using var downloader = new Downloader(
            new SourceRouter(Array.Empty<DownloadSource>()),
            new DownloaderOptions(),
            NullReporter.Instance,
            new ProgressPump(NullReporter.Instance));
        var service = new VersionManifestService(downloader, NullReporter.Instance);

        var found = service.FindVersion(manifest, "25W02A");
        Assert.NotNull(found);
        Assert.Equal("25w02a", found!.Id);

        Assert.Same(manifest.Versions[2], service.FindVersion(manifest, "1.21.3"));
        Assert.Null(service.FindVersion(manifest, "does-not-exist"));
    }

    [Fact]
    public void ParsesAssetIndexWithLegacyLayoutFlags()
    {
        const string json = """
        {
          "objects": {
            "minecraft/lang/en_us.json": {
              "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "size": 1234
            },
            "icons/icon_16x16.png": {
              "hash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "size": 42
            }
          },
          "virtual": true,
          "map_to_resources": true
        }
        """;

        var index = MinecraftJson.Deserialize<AssetIndexFile>(json);

        Assert.NotNull(index);
        Assert.True(index!.Virtual);
        Assert.True(index.MapToResources);
        Assert.Equal(2, index.Objects.Count);

        var language = index.Objects["minecraft/lang/en_us.json"];
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", language.Hash);
        Assert.Equal(1234L, language.Size);
    }

    [Fact]
    public void AssetIndexDefaultsToModernLayout()
    {
        var index = MinecraftJson.Deserialize<AssetIndexFile>("""{ "objects": {} }""");

        Assert.NotNull(index);
        Assert.False(index!.Virtual);
        Assert.False(index.MapToResources);
        Assert.Empty(index.Objects);
    }

    [Fact]
    public void VersionSummaryWithoutATypeDefaultsToRelease()
    {
        const string json = """
        { "versions": [ { "id": "1.0", "url": "https://example/1.0.json" } ] }
        """;

        var manifest = MinecraftJson.Deserialize<VersionManifest>(json);

        Assert.NotNull(manifest);
        var version = Assert.Single(manifest!.Versions);
        Assert.Equal("release", version.Type);
        Assert.True(version.IsRelease);
        Assert.Null(manifest.Latest.Release);
        Assert.Null(manifest.Latest.Snapshot);
    }
}
