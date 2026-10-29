using DownCraft.Core.Sources;

namespace DownCraft.Tests;

/// <summary>Small builders shared by the source-router and catalog tests.</summary>
internal static class TestHelpers
{
    public static DownloadSource Source(
        string id,
        int priority,
        bool enabled = true,
        bool useOriginalUrl = false,
        params (ArtifactKind Kind, string Url)[] endpoints)
    {
        var source = new DownloadSource
        {
            Id = id,
            DisplayName = id,
            Priority = priority,
            Enabled = enabled,
            UseOriginalUrl = useOriginalUrl,
        };

        foreach (var (kind, url) in endpoints)
        {
            source.Endpoints[kind.ToString()] = new EndpointTemplate(url);
        }

        return source;
    }

    public static SourceContext VersionContext(string? originalUrl = "https://original.example/1.20.1.json") =>
        new(VersionId: "1.20.1", OriginalUrl: originalUrl);

    /// <summary>A context with every placeholder populated; used to prove default templates are complete.</summary>
    public static SourceContext FullContext() => new(
        VersionId: "1.20.1",
        LoaderVersion: "0.16.9",
        MavenPath: "net/example/lib/1.0/lib-1.0.jar",
        AssetHash: "abcdef0123456789abcdef0123456789abcdef01",
        IndexId: "17",
        Component: "java-runtime-gamma",
        Platform: "windows-x64",
        FileName: "client.jar",
        Sha1: "0123456789abcdef0123456789abcdef01234567",
        OriginalUrl: "https://original.example/file");
}
