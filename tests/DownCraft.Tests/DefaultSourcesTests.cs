using DownCraft.Core.Sources;

namespace DownCraft.Tests;

public class DefaultSourcesTests
{
    private static readonly string[] ExpectedIds =
    [
        "mojang",
        "bmclapi",
        "fabric",
        "quilt",
        "forge",
        "neoforge",
        "aliyun-maven",
    ];

    [Fact]
    public void EmbeddedCatalogContainsTheSevenExpectedSources()
    {
        var catalog = SourceCatalog.LoadDefault();

        Assert.Equal(7, catalog.All.Count);
        Assert.Equal(
            ExpectedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            catalog.All.Select(s => s.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(7, catalog.Enabled.Count);
    }

    [Fact]
    public void DefaultSourcesAreOrderedByAscendingPriority()
    {
        var catalog = SourceCatalog.LoadDefault();

        Assert.Equal(
            catalog.All.OrderBy(s => s.Priority).Select(s => s.Id),
            catalog.All.Select(s => s.Id));
    }

    [Fact]
    public void EveryEnabledSourceHasAProbeUrl()
    {
        var catalog = SourceCatalog.LoadDefault();

        Assert.All(catalog.Enabled, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.ProbeUrl), $"{source.Id} has no probeUrl");
            Assert.StartsWith("https://", source.ProbeUrl, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void EveryEnabledSourceExpandsAllOfItsDeclaredTemplates()
    {
        var context = TestHelpers.FullContext();
        var catalog = SourceCatalog.LoadDefault();

        foreach (var source in catalog.Enabled)
        {
            foreach (var (key, endpoint) in source.Endpoints)
            {
                Assert.False(string.IsNullOrWhiteSpace(endpoint.Url), $"{source.Id}/{key} has an empty template");

                var expanded = SourceRouter.Expand(endpoint.Url, context);

                Assert.NotNull(expanded);
                Assert.StartsWith("https://", expanded);
                Assert.DoesNotContain("{", expanded);
                Assert.True(Enum.TryParse<ArtifactKind>(key, ignoreCase: true, out var kind));
                Assert.True(source.Supports(kind), $"{source.Id} does not support its declared kind {key}");
            }
        }
    }

    [Theory]
    [InlineData(ArtifactKind.VersionManifest)]
    [InlineData(ArtifactKind.Library)]
    [InlineData(ArtifactKind.AssetObject)]
    [InlineData(ArtifactKind.ForgeInstaller)]
    [InlineData(ArtifactKind.NeoForgeInstaller)]
    [InlineData(ArtifactKind.FabricProfile)]
    public void AtLeastOneEnabledSourceCoversEachImportantKind(ArtifactKind kind)
    {
        var catalog = SourceCatalog.LoadDefault();

        Assert.Contains(catalog.Enabled, source => source[kind] is not null);
    }

    [Fact]
    public void LoadEffectiveFallsBackToTheDefaultsWhenTheUserFileIsMissing()
    {
        using var temp = new TempDirectory();

        var catalog = SourceCatalog.LoadEffective(temp.Combine("does-not-exist.json"), out var warning);

        Assert.Null(warning);
        Assert.Equal(7, catalog.All.Count);
    }

    [Fact]
    public void LoadEffectiveFallsBackToTheDefaultsWhenTheUserFileIsCorrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("sources.json", "{ this is not a catalog");

        var catalog = SourceCatalog.LoadEffective(path, out var warning);

        Assert.NotNull(warning);
        Assert.Equal(7, catalog.All.Count);
    }
}
