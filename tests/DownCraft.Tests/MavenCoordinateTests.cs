using DownCraft.Core.Meta;

namespace DownCraft.Tests;

public class MavenCoordinateTests
{
    [Fact]
    public void ParsesGroupArtifactAndVersion()
    {
        Assert.True(MavenCoordinate.TryParse("com.example:cool-lib:1.2.3", out var coordinate));

        Assert.Equal("com.example", coordinate.Group);
        Assert.Equal("cool-lib", coordinate.Artifact);
        Assert.Equal("1.2.3", coordinate.Version);
        Assert.Null(coordinate.Classifier);
        Assert.Equal("jar", coordinate.Extension);
    }

    [Fact]
    public void ParsesClassifier()
    {
        Assert.True(MavenCoordinate.TryParse("com.example:cool-lib:1.2.3:natives-windows", out var coordinate));

        Assert.Equal("1.2.3", coordinate.Version);
        Assert.Equal("natives-windows", coordinate.Classifier);
        Assert.Equal("jar", coordinate.Extension);
    }

    [Fact]
    public void ParsesExtensionFromAtSuffix()
    {
        Assert.True(MavenCoordinate.TryParse("com.example:cool-lib:1.2.3@zip", out var coordinate));

        Assert.Equal("zip", coordinate.Extension);
        Assert.Null(coordinate.Classifier);
    }

    [Fact]
    public void ParsesClassifierAndExtensionTogether()
    {
        Assert.True(MavenCoordinate.TryParse("com.example:cool-lib:1.2.3:natives-windows@zip", out var coordinate));

        Assert.Equal("com.example", coordinate.Group);
        Assert.Equal("cool-lib", coordinate.Artifact);
        Assert.Equal("1.2.3", coordinate.Version);
        Assert.Equal("natives-windows", coordinate.Classifier);
        Assert.Equal("zip", coordinate.Extension);
    }

    [Fact]
    public void TrailingColonMeansNoClassifier()
    {
        Assert.True(MavenCoordinate.TryParse("com.example:cool-lib:1.2.3:", out var coordinate));

        Assert.Null(coordinate.Classifier);
        Assert.Equal("jar", coordinate.Extension);
        Assert.Equal("com.example:cool-lib:1.2.3", coordinate.ToString());
    }

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        Assert.True(MavenCoordinate.TryParse("  com.example:cool-lib:1.2.3  ", out var coordinate));

        Assert.Equal("com.example", coordinate.Group);
        Assert.Equal("1.2.3", coordinate.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("single")]
    [InlineData("group:artifact")]
    [InlineData(":")]
    [InlineData("@zip")]
    public void RejectsMalformedValues(string value)
    {
        Assert.False(MavenCoordinate.TryParse(value, out var coordinate));
        Assert.Equal(string.Empty, coordinate.Group);
        Assert.Equal(string.Empty, coordinate.Artifact);
        Assert.Equal(string.Empty, coordinate.Version);
        Assert.Null(coordinate.Classifier);
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.False(MavenCoordinate.TryParse(null, out _));
    }

    [Theory]
    [InlineData("com.example:cool-lib:1.2.3", "com/example/cool-lib/1.2.3/cool-lib-1.2.3.jar")]
    [InlineData("com.example:cool-lib:1.2.3:natives-windows", "com/example/cool-lib/1.2.3/cool-lib-1.2.3-natives-windows.jar")]
    [InlineData("com.example:cool-lib:1.2.3@zip", "com/example/cool-lib/1.2.3/cool-lib-1.2.3.zip")]
    [InlineData("com.example:cool-lib:1.2.3:natives-windows@zip", "com/example/cool-lib/1.2.3/cool-lib-1.2.3-natives-windows.zip")]
    [InlineData("org.single:artifact:0.1", "org/single/artifact/0.1/artifact-0.1.jar")]
    public void RepositoryPathFollowsMavenLayout(string input, string expected)
    {
        Assert.True(MavenCoordinate.TryParse(input, out var coordinate));
        Assert.Equal(expected, coordinate.ToRepositoryPath());
    }

    [Theory]
    [InlineData("com.example:cool-lib:1.2.3")]
    [InlineData("com.example:cool-lib:1.2.3:natives-windows")]
    public void ToStringRoundTripsThroughTryParse(string text)
    {
        Assert.True(MavenCoordinate.TryParse(text, out var coordinate));
        Assert.Equal(text, coordinate.ToString());

        Assert.True(MavenCoordinate.TryParse(coordinate.ToString(), out var reparsed));
        Assert.Equal(coordinate, reparsed);
    }

    [Fact]
    public void ToStringOmitsTheDefaultExtension()
    {
        Assert.True(MavenCoordinate.TryParse("com.example:cool-lib:1.2.3@jar", out var coordinate));

        Assert.Equal("com.example:cool-lib:1.2.3", coordinate.ToString());
        Assert.Equal("jar", coordinate.Extension);
    }
}
