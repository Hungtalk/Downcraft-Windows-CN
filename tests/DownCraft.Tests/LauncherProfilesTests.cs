using System.Text.Json;
using System.Text.Json.Nodes;
using DownCraft.Core.Install;

namespace DownCraft.Tests;

public class LauncherProfilesTests
{
    private static JsonObject ReadJson(string path)
    {
        var node = JsonNode.Parse(
            File.ReadAllText(path),
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        return Assert.IsType<JsonObject>(node);
    }

    [Fact]
    public void UpsertCreatesTheFileWithAProfilePointingAtTheVersion()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);

        LauncherProfiles.Upsert(paths, "1.20.1");

        Assert.True(File.Exists(paths.LauncherProfilesPath));
        var root = ReadJson(paths.LauncherProfilesPath);
        var profile = Assert.IsType<JsonObject>(root["profiles"]!.AsObject()["1.20.1"]);
        Assert.Equal("1.20.1", profile["lastVersionId"]!.GetValue<string>());
        Assert.Equal("custom", profile["type"]!.GetValue<string>());
        Assert.Equal("1.20.1", profile["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(profile["created"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(profile["lastUsed"]!.GetValue<string>()));
        Assert.Equal(3, root["version"]!.GetValue<int>());
        Assert.Equal("zh_CN", root["settings"]!.AsObject()["language"]!.GetValue<string>());
    }

    [Fact]
    public void AUpsertForAnotherVersionPreservesTheFirstProfile()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);

        LauncherProfiles.Upsert(paths, "1.20.1", "Fabric 1.20.1");
        LauncherProfiles.Upsert(paths, "1.20.4");

        var profiles = ReadJson(paths.LauncherProfilesPath)["profiles"]!.AsObject();
        Assert.Equal(2, profiles.Count);
        Assert.Equal("1.20.1", profiles["1.20.1"]!.AsObject()["lastVersionId"]!.GetValue<string>());
        Assert.Equal("Fabric 1.20.1", profiles["1.20.1"]!.AsObject()["name"]!.GetValue<string>());
        Assert.Equal("1.20.4", profiles["1.20.4"]!.AsObject()["lastVersionId"]!.GetValue<string>());

        var ids = LauncherProfiles.ListVersionIds(paths);
        Assert.Equal(new[] { "1.20.1", "1.20.4" }, ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void UpsertPreservesExistingUserProfilesAndSettings()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);
        File.WriteAllText(paths.LauncherProfilesPath, """
        {
          "profiles": {
            "vanilla-user": {
              "name": "My World",
              "type": "custom",
              "lastVersionId": "1.7.10",
              "icon": "Grass",
              "created": "2020-01-01T00:00:00.000Z"
            }
          },
          "settings": {
            "language": "en_US",
            "enableSnapshots": true,
            "keepLauncherOpen": true
          },
          "selectedProfile": "vanilla-user",
          "version": 3
        }
        """);

        LauncherProfiles.Upsert(paths, "1.20.1");

        var root = ReadJson(paths.LauncherProfilesPath);
        var profiles = root["profiles"]!.AsObject();
        Assert.Equal(2, profiles.Count);

        var vanilla = profiles["vanilla-user"]!.AsObject();
        Assert.Equal("My World", vanilla["name"]!.GetValue<string>());
        Assert.Equal("1.7.10", vanilla["lastVersionId"]!.GetValue<string>());
        Assert.Equal("Grass", vanilla["icon"]!.GetValue<string>());
        Assert.Equal("2020-01-01T00:00:00.000Z", vanilla["created"]!.GetValue<string>());

        var settings = root["settings"]!.AsObject();
        Assert.Equal("en_US", settings["language"]!.GetValue<string>());
        Assert.True(settings["enableSnapshots"]!.GetValue<bool>());
        Assert.True(settings["keepLauncherOpen"]!.GetValue<bool>());
        Assert.Equal("vanilla-user", root["selectedProfile"]!.GetValue<string>());
        Assert.Equal("1.20.1", profiles["1.20.1"]!.AsObject()["lastVersionId"]!.GetValue<string>());
    }

    [Fact]
    public void UpsertReplacesCorruptJsonGracefully()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);
        File.WriteAllText(paths.LauncherProfilesPath, "{ this is not valid json");

        LauncherProfiles.Upsert(paths, "1.20.1");

        var root = ReadJson(paths.LauncherProfilesPath);
        var profiles = root["profiles"]!.AsObject();
        Assert.True(profiles.ContainsKey("1.20.1"));
        Assert.Equal("1.20.1", profiles["1.20.1"]!.AsObject()["lastVersionId"]!.GetValue<string>());
        Assert.Equal(new[] { "1.20.1" }, LauncherProfiles.ListVersionIds(paths));
    }

    [Fact]
    public void ListVersionIdsReturnsEmptyWhenTheFileIsMissing()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);

        Assert.Empty(LauncherProfiles.ListVersionIds(paths));
    }

    [Fact]
    public void ListVersionIdsSkipsProfilesWithoutALastVersionId()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);
        File.WriteAllText(paths.LauncherProfilesPath, """
        {
          "profiles": {
            "a": { "name": "A", "lastVersionId": "1.20.1" },
            "b": { "name": "B" },
            "c": { "name": "C", "lastVersionId": "" }
          }
        }
        """);

        var ids = LauncherProfiles.ListVersionIds(paths);

        Assert.Equal(new[] { "1.20.1" }, ids);
    }

    [Fact]
    public void ListVersionIdsDeduplicatesRepeatedVersions()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);
        File.WriteAllText(paths.LauncherProfilesPath, """
        {
          "profiles": {
            "first": { "name": "First", "lastVersionId": "1.20.1" },
            "second": { "name": "Second", "lastVersionId": "1.20.1" },
            "third": { "name": "Third", "lastVersionId": "1.20.4" }
          }
        }
        """);

        var ids = LauncherProfiles.ListVersionIds(paths);

        Assert.Equal(2, ids.Count);
        Assert.Equal(new[] { "1.20.1", "1.20.4" }, ids.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void UpsertUsesTheDisplayNameAndIcon()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);

        LauncherProfiles.Upsert(paths, "fabric-loader-0.16.9-1.20.1", "Fabric 1.20.1", "Furnace");

        var profile = ReadJson(paths.LauncherProfilesPath)["profiles"]!
            .AsObject()["fabric-loader-0.16.9-1.20.1"]!
            .AsObject();
        Assert.Equal("Fabric 1.20.1", profile["name"]!.GetValue<string>());
        Assert.Equal("Furnace", profile["icon"]!.GetValue<string>());
        Assert.Equal("fabric-loader-0.16.9-1.20.1", profile["lastVersionId"]!.GetValue<string>());
    }

    [Fact]
    public void UpsertSanitizesTheProfileKeyButKeepsTheVersionId()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);

        LauncherProfiles.Upsert(paths, "weird|id");

        var profiles = ReadJson(paths.LauncherProfilesPath)["profiles"]!.AsObject();
        Assert.True(profiles.ContainsKey("weird_id"));
        Assert.Equal("weird|id", profiles["weird_id"]!.AsObject()["lastVersionId"]!.GetValue<string>());
        Assert.Equal(new[] { "weird|id" }, LauncherProfiles.ListVersionIds(paths));
    }

    [Fact]
    public void EnsureExistsWritesTheMinimalLauncherSchema()
    {
        using var temp = new TempDirectory();
        var paths = new GamePaths(temp.Path);

        LauncherProfiles.EnsureExists(paths);

        Assert.True(File.Exists(paths.LauncherProfilesPath));
        var root = ReadJson(paths.LauncherProfilesPath);
        Assert.Equal(3, root["version"]!.GetValue<int>());
        Assert.Empty(root["profiles"]!.AsObject());
        Assert.Equal("zh_CN", root["settings"]!.AsObject()["language"]!.GetValue<string>());
    }
}
