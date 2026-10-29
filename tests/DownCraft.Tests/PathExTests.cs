using System.Globalization;
using DownCraft.Core.Util;

namespace DownCraft.Tests;

public class PathExTests
{
    [Fact]
    public void RelConvertsForwardSlashesToNativeSeparators()
    {
        Assert.Equal(Path.Combine("a", "b", "c.txt"), PathEx.Rel("a/b/c.txt"));
    }

    [Fact]
    public void ToUnixConvertsBackslashesToForwardSlashes()
    {
        Assert.Equal("a/b/c", PathEx.ToUnix(@"a\b\c"));
        Assert.Equal("a/b/c", PathEx.ToUnix("a/b/c"));
        Assert.Equal("C:/game/versions/1.20.1/1.20.1.json", PathEx.ToUnix(@"C:\game\versions\1.20.1\1.20.1.json"));
    }

    [Fact]
    public void SafeCombineKeepsPathsInsideTheRoot()
    {
        using var temp = new TempDirectory();

        var combined = PathEx.SafeCombine(temp.Path, "sub/dir/file.txt");

        Assert.Equal(Path.Combine(temp.Path, "sub", "dir", "file.txt"), combined);
        Assert.StartsWith(temp.Path, combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafeCombineNormalizesParentSegmentsThatStayInsideTheRoot()
    {
        using var temp = new TempDirectory();

        var combined = PathEx.SafeCombine(temp.Path, "sub/../file.txt");

        Assert.Equal(Path.Combine(temp.Path, "file.txt"), combined);
    }

    [Fact]
    public void SafeCombineRejectsParentTraversal()
    {
        using var temp = new TempDirectory();

        Assert.Throws<InvalidOperationException>(() => PathEx.SafeCombine(temp.Path, "../evil.txt"));
        Assert.Throws<InvalidOperationException>(() => PathEx.SafeCombine(temp.Path, "sub/../../evil.txt"));
    }

    [Fact]
    public void SafeCombineRejectsAbsolutePaths()
    {
        using var temp = new TempDirectory();
        var absolute = Path.Combine(Path.GetPathRoot(temp.Path)!, "downcraft-evil.txt");

        Assert.Throws<InvalidOperationException>(() => PathEx.SafeCombine(temp.Path, absolute));
        Assert.Throws<InvalidOperationException>(() => PathEx.SafeCombine(temp.Path, "/etc/passwd"));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("sub/file.txt")]
    [InlineData("sub\\file.txt")]
    [InlineData(".hidden")]
    [InlineData("a b/c d.txt")]
    public void IsSafeRelativeAcceptsPlainRelativePaths(string relative)
    {
        Assert.True(PathEx.IsSafeRelative(relative));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("./a.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("\\absolute.txt")]
    [InlineData("C:/windows.txt")]
    [InlineData("C:\\windows.txt")]
    public void IsSafeRelativeRejectsTraversalAndRootedPaths(string relative)
    {
        Assert.False(PathEx.IsSafeRelative(relative));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1280L, "1.25 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(10240L, "10 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1572864L, "1.5 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1 TB")]
    [InlineData(1125899906842624L, "1024 TB")]
    public void FormatBytesUsesCompactUnits(long bytes, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, PathEx.FormatBytes(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void PartPathForAppendsThePartialSuffixNextToTheTarget()
    {
        var target = Path.Combine("libraries", "example.jar");

        Assert.Equal(target + ".downcraft-part", PathEx.PartPathFor(target));
    }

    [Fact]
    public void EnsureParentCreatesTheMissingDirectoryTree()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("versions", "1.20.1", "1.20.1.json");

        PathEx.EnsureParent(file);

        Assert.True(Directory.Exists(temp.Combine("versions", "1.20.1")));
        Assert.False(File.Exists(file));
    }
}
