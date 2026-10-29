using System.Text;
using System.Text.Json;
using McDownload.Core.Sources;

namespace McDownload.Tests;

public class SourceCatalogTests
{
    private const string CamelCaseCatalog = """
    {
      // Endpoint keys are written in camelCase in the shipped catalog.
      "version": 1,
      "sources": [
        {
          "id": "mirror",
          "displayName": "Mirror",
          "priority": 100,
          "probeUrl": "https://mirror.example/probe",
          "endpoints": {
            "versionManifest": "https://mirror.example/manifest.json",
            "versionJson": "https://mirror.example/{version}.json",
            "library": { "url": "https://mirror.example/maven/{path}", "priority": 250 }
          }
        }
      ]
    }
    """;

    [Fact]
    public void CamelCaseEndpointKeysResolveCaseInsensitivelyAfterJsonLoad()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(CamelCaseCatalog));

        var catalog = SourceCatalog.Load(stream);

        var source = Assert.Single(catalog.All);
        Assert.Same(StringComparer.OrdinalIgnoreCase, source.Endpoints.Comparer);
        Assert.True(source.Endpoints.ContainsKey("versionjson"));

        var manifest = source[ArtifactKind.VersionManifest];
        Assert.NotNull(manifest);
        Assert.Equal("https://mirror.example/manifest.json", manifest!.Url);

        var library = source[ArtifactKind.Library];
        Assert.NotNull(library);
        Assert.Equal(250, library!.Priority);

        Assert.True(source.Supports(ArtifactKind.VersionJson));
        Assert.NotNull(source[ArtifactKind.VersionJson]);
    }

    [Fact]
    public void EndpointLookupToleratesACaseSensitiveDictionary()
    {
        // Simulates a DownloadSource whose dictionary comparer was lost by JSON binding.
        var source = new DownloadSource
        {
            Id = "manual",
            Endpoints = new Dictionary<string, EndpointTemplate>
            {
                ["versionJson"] = new("https://manual.example/{version}.json"),
            },
        };

        Assert.NotNull(source[ArtifactKind.VersionJson]);
        Assert.True(source.Supports(ArtifactKind.VersionJson));
        Assert.Null(source[ArtifactKind.ClientJar]);
    }

    [Fact]
    public void NormalizeRewritesEndpointKeysToArtifactKindNames()
    {
        var source = new DownloadSource
        {
            Id = "manual",
            Endpoints = new Dictionary<string, EndpointTemplate>
            {
                ["versionjson"] = new("https://manual.example/{version}.json"),
                ["not-a-kind"] = new("https://manual.example/ignored"),
            },
        };

        source.Normalize();

        Assert.Contains("VersionJson", source.Endpoints.Keys);
        Assert.DoesNotContain("versionJson", source.Endpoints.Keys);
        Assert.Single(source.Endpoints);
    }

    [Fact]
    public void SupportsHonoursUseOriginalUrlExceptForDirect()
    {
        var source = new DownloadSource { Id = "official", UseOriginalUrl = true };

        Assert.True(source.Supports(ArtifactKind.ClientJar));
        Assert.True(source.Supports(ArtifactKind.Library));
        Assert.False(source.Supports(ArtifactKind.Direct));
    }

    [Fact]
    public void WithOrderPutsRequestedSourcesFirstAndRenumbersPriorities()
    {
        var catalog = new SourceCatalog(
        [
            TestHelpers.Source("alpha", 100),
            TestHelpers.Source("beta", 200),
            TestHelpers.Source("gamma", 300),
        ]);

        var ordered = catalog.WithOrder(["gamma", "alpha"]);

        Assert.Equal(new[] { "gamma", "alpha", "beta" }, ordered.All.Select(s => s.Id));
        Assert.Equal(new[] { 0, 10, 20 }, ordered.All.Select(s => s.Priority));

        // The original catalog is untouched: WithOrder works on clones.
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, catalog.All.Select(s => s.Id));
        Assert.Equal(new[] { 100, 200, 300 }, catalog.All.Select(s => s.Priority));
    }

    [Fact]
    public void WithOrderThrowsForAnUnknownSourceId()
    {
        var catalog = new SourceCatalog([TestHelpers.Source("alpha", 100)]);

        var exception = Assert.Throws<ArgumentException>(() => catalog.WithOrder(["missing"]));

        Assert.Contains("missing", exception.Message);
        Assert.Contains("alpha", exception.Message);
    }

    [Fact]
    public void WithOrderWithAnEmptyListReturnsTheSameCatalog()
    {
        var catalog = new SourceCatalog([TestHelpers.Source("alpha", 100)]);

        Assert.Same(catalog, catalog.WithOrder([]));
    }

    [Fact]
    public void CatalogSurvivesSaveAndReload()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("sources.json");

        var defaults = SourceCatalog.LoadDefault();
        defaults.Save(path);

        Assert.True(File.Exists(path));
        var reloaded = SourceCatalog.LoadFile(path);
        Assert.Equal(defaults.All.Select(s => s.Id), reloaded.All.Select(s => s.Id));

        var bmclapi = Assert.Single(reloaded.All, s => s.Id == "bmclapi");
        Assert.NotNull(bmclapi[ArtifactKind.VersionJson]);
        Assert.True(bmclapi.Endpoints.ContainsKey("VersionJson"));
        Assert.Equal("https://bmclapi2.bangbang93.com/version/{version}/json", bmclapi[ArtifactKind.VersionJson]!.Url);
    }

    [Fact]
    public void LoadRejectsDuplicateSourceIds()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{ "sources": [ { "id": "duplicate" }, { "id": "duplicate" } ] }"""));

        Assert.Throws<JsonException>(() => SourceCatalog.Load(stream));
    }

    [Fact]
    public void LoadRejectsASourceWithoutAnId()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{ "sources": [ { "displayName": "no id" } ] }"""));

        Assert.Throws<JsonException>(() => SourceCatalog.Load(stream));
    }
}
