using McDownload.Core.Sources;

namespace McDownload.Tests;

public class SourceRouterTests
{
    [Fact]
    public void ExpandReplacesEveryDocumentedPlaceholder()
    {
        var context = new SourceContext(
            VersionId: "1.20.1",
            LoaderVersion: "0.16.9",
            MavenPath: "com/example/lib/1.0/lib-1.0.jar",
            AssetHash: "abcdef0123456789",
            IndexId: "17",
            Component: "java-runtime-gamma",
            Platform: "windows-x64",
            FileName: "client.jar",
            Sha1: "0123456789abcdef",
            OriginalUrl: "https://original.example/file");

        const string template =
            "https://mirror/{version}/{loader}/{path}/{hash}/{hash2}/{index}/{component}/{platform}/{file}/{sha1}/{url}";

        Assert.Equal(
            "https://mirror/1.20.1/0.16.9/com/example/lib/1.0/lib-1.0.jar/abcdef0123456789/ab/17/java-runtime-gamma/windows-x64/client.jar/0123456789abcdef/https://original.example/file",
            SourceRouter.Expand(template, context));
    }

    [Fact]
    public void ExpandSupportsTheMinecraftVersionAlias()
    {
        Assert.Equal("https://mirror/1.20.1/json", SourceRouter.Expand("https://mirror/{minecraftVersion}/json", TestHelpers.VersionContext()));
    }

    [Fact]
    public void ExpandIsCaseInsensitiveForPlaceholderNames()
    {
        Assert.Equal("https://mirror/1.20.1", SourceRouter.Expand("https://mirror/{VERSION}", TestHelpers.VersionContext()));
        Assert.Equal("https://mirror/1.20.1", SourceRouter.Expand("https://mirror/{Version}", TestHelpers.VersionContext()));
    }

    [Fact]
    public void ExpandReturnsNullForAnUnknownPlaceholder()
    {
        Assert.Null(SourceRouter.Expand("https://mirror/{unknown}", TestHelpers.VersionContext()));
        Assert.Null(SourceRouter.Expand("https://mirror/{version}/{something_else}", TestHelpers.VersionContext()));
    }

    [Fact]
    public void ExpandReturnsNullForAnEmptyTemplate()
    {
        Assert.Null(SourceRouter.Expand(string.Empty, TestHelpers.VersionContext()));
        Assert.Null(SourceRouter.Expand("   ", TestHelpers.VersionContext()));
    }

    [Fact]
    public void ExpandReturnsNullWhenAKnownPlaceholderHasNoValue()
    {
        // A Fabric profile URL without a loader version must not become ".../1.20.1//profile/json".
        var context = new SourceContext(VersionId: "1.20.1");

        Assert.Null(SourceRouter.Expand(
            "https://meta.fabricmc.net/v2/versions/loader/{version}/{loader}/profile/json",
            context));
    }

    [Fact]
    public void BuildOrdersCandidatesByConfiguredPriorityWithoutHealthData()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("high", 300, endpoints: (ArtifactKind.VersionJson, "https://high.example/{version}.json")),
            TestHelpers.Source("medium", 200, endpoints: (ArtifactKind.VersionJson, "https://medium.example/{version}.json")),
            TestHelpers.Source("low", 100, endpoints: (ArtifactKind.VersionJson, "https://low.example/{version}.json")),
        ]);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.Equal(new[] { "low", "medium", "high", "original" }, candidates.Select(c => c.SourceId));
        Assert.Equal("https://low.example/1.20.1.json", candidates[0].Url);
    }

    [Fact]
    public void BuildAppendsTheCanonicalUrlLast()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("mirror", 100, endpoints: (ArtifactKind.VersionJson, "https://mirror.example/{version}.json")),
        ]);
        var context = TestHelpers.VersionContext();

        var candidates = router.Build(ArtifactKind.VersionJson, context);

        Assert.Equal(2, candidates.Count);
        var original = candidates[^1];
        Assert.Equal("original", original.SourceId);
        Assert.Equal(context.OriginalUrl, original.Url);
        Assert.Equal(long.MaxValue, original.Score);
        Assert.Equal(int.MaxValue, original.EffectivePriority);
    }

    [Fact]
    public void BuildDeduplicatesTheSameMirrorUrlAcrossSources()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("first", 100, endpoints: (ArtifactKind.VersionJson, "https://same.example/{version}")),
            TestHelpers.Source("second", 200, endpoints: (ArtifactKind.VersionJson, "https://same.example/{version}")),
        ]);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.Equal(2, candidates.Count);
        Assert.Equal("first", candidates[0].SourceId);
        Assert.Equal("https://same.example/1.20.1", candidates[0].Url);
        Assert.Single(candidates, c => c.Url == "https://same.example/1.20.1");
    }

    [Fact]
    public void BuildAppendsTheOriginalUrlOnlyOnceWhenMirrorsReuseIt()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("mirror-a", 100, useOriginalUrl: true),
            TestHelpers.Source("mirror-b", 200, useOriginalUrl: true),
        ]);
        var context = TestHelpers.VersionContext();

        var candidates = router.Build(ArtifactKind.ClientJar, context);

        var candidate = Assert.Single(candidates);
        Assert.Equal("mirror-a", candidate.SourceId);
        Assert.Equal(context.OriginalUrl, candidate.Url);
    }

    [Fact]
    public void BuildCanExcludeTheOriginalUrl()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("mirror", 100, endpoints: (ArtifactKind.VersionJson, "https://mirror.example/{version}.json")),
        ]);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext(), includeOriginal: false);

        var candidate = Assert.Single(candidates);
        Assert.Equal("mirror", candidate.SourceId);
    }

    [Fact]
    public void BuildReturnsOnlyTheOriginalUrlForAnUnknownKind()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("manifest-only", 100, endpoints: (ArtifactKind.VersionManifest, "https://mirror.example/manifest.json")),
        ]);
        var context = TestHelpers.VersionContext();

        var candidates = router.Build(ArtifactKind.ClientJar, context);

        var candidate = Assert.Single(candidates);
        Assert.Equal("original", candidate.SourceId);
        Assert.Equal(context.OriginalUrl, candidate.Url);
    }

    [Fact]
    public void BuildReturnsNothingForAnUnknownKindWithoutAnOriginalUrl()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("manifest-only", 100, endpoints: (ArtifactKind.VersionManifest, "https://mirror.example/manifest.json")),
        ]);

        var candidates = router.Build(ArtifactKind.ClientJar, new SourceContext(VersionId: "1.20.1"));

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildFallsBackToTheOriginalUrlWithNoSources()
    {
        var context = TestHelpers.VersionContext();

        var candidates = new SourceRouter([]).Build(ArtifactKind.ClientJar, context);

        var candidate = Assert.Single(candidates);
        Assert.Equal("original", candidate.SourceId);
        Assert.Equal(context.OriginalUrl, candidate.Url);
    }

    [Fact]
    public void BuildSkipsDisabledSources()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("enabled", 100, endpoints: (ArtifactKind.VersionJson, "https://enabled.example/{version}")),
            TestHelpers.Source("disabled", 50, enabled: false, endpoints: (ArtifactKind.VersionJson, "https://disabled.example/{version}")),
        ]);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.Equal(new[] { "enabled", "original" }, candidates.Select(c => c.SourceId));
    }

    [Fact]
    public void BuildIgnoresAnEndpointWithAnEmptyTemplate()
    {
        var router = new SourceRouter(
        [
            TestHelpers.Source("blank", 100, endpoints: (ArtifactKind.VersionJson, "   ")),
        ]);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        var candidate = Assert.Single(candidates);
        Assert.Equal("original", candidate.SourceId);
    }

    [Fact]
    public void BuildUsesTheEndpointPriorityWhenItIsSet()
    {
        var highPrioritySource = TestHelpers.Source("source-a", 100, endpoints: (ArtifactKind.VersionJson, "https://a.example/{version}"));
        highPrioritySource.Endpoints[ArtifactKind.VersionJson.ToString()].Priority = 500;

        var router = new SourceRouter(
        [
            highPrioritySource,
            TestHelpers.Source("source-b", 200, endpoints: (ArtifactKind.VersionJson, "https://b.example/{version}")),
        ]);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.Equal(new[] { "source-b", "source-a", "original" }, candidates.Select(c => c.SourceId));
        Assert.Equal(200, candidates[0].EffectivePriority);
        Assert.Equal(500, candidates[1].EffectivePriority);
    }

    [Fact]
    public void BuildUsesMeasuredLatencyToOrderMirrors()
    {
        var health = SourceHealthStore.Load(null);
        health.RecordSuccess("slow", 4000);
        health.RecordSuccess("fast", 10);

        var router = new SourceRouter(
        [
            TestHelpers.Source("slow", 100, endpoints: (ArtifactKind.VersionJson, "https://slow.example/{version}")),
            TestHelpers.Source("fast", 900, endpoints: (ArtifactKind.VersionJson, "https://fast.example/{version}")),
        ],
        health);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.Equal(new[] { "fast", "slow", "original" }, candidates.Select(c => c.SourceId));
    }

    [Fact]
    public void BuildPlacesAFailingSourceAfterHealthySources()
    {
        var health = SourceHealthStore.Load(null);
        health.RecordFailure("broken");
        health.RecordSuccess("healthy", 500);

        var router = new SourceRouter(
        [
            TestHelpers.Source("broken", 100, endpoints: (ArtifactKind.VersionJson, "https://broken.example/{version}")),
            TestHelpers.Source("healthy", 900, endpoints: (ArtifactKind.VersionJson, "https://healthy.example/{version}")),
        ],
        health);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.Equal(new[] { "healthy", "broken", "original" }, candidates.Select(c => c.SourceId));
    }

    [Fact]
    public void BuildPrefersPriorityOverMeasuredLatencyWhenConfigured()
    {
        var health = SourceHealthStore.Load(null);
        health.RecordSuccess("priority-first", 5000);
        health.RecordSuccess("fast-but-late", 5);

        var router = new SourceRouter(
        [
            TestHelpers.Source("priority-first", 100, endpoints: (ArtifactKind.VersionJson, "https://priority.example/{version}")),
            TestHelpers.Source("fast-but-late", 300, endpoints: (ArtifactKind.VersionJson, "https://fast.example/{version}")),
        ],
        health,
        preferPriorityOrder: true);

        var candidates = router.Build(ArtifactKind.VersionJson, TestHelpers.VersionContext());

        Assert.True(router.PreferPriorityOrder);
        Assert.Equal(new[] { "priority-first", "fast-but-late", "original" }, candidates.Select(c => c.SourceId));
    }

    [Fact]
    public void HasMirrorDetectsEndpointAndOriginalUrlSources()
    {
        Assert.True(new SourceRouter(
        [
            TestHelpers.Source("library", 100, endpoints: (ArtifactKind.Library, "https://mirror.example/{path}")),
        ]).HasMirror(ArtifactKind.Library));

        Assert.True(new SourceRouter(
        [
            TestHelpers.Source("original", 100, useOriginalUrl: true),
        ]).HasMirror(ArtifactKind.ClientJar));

        Assert.False(new SourceRouter([]).HasMirror(ArtifactKind.Library));
    }
}
