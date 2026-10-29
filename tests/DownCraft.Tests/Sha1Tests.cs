using System.Text;
using DownCraft.Core.Util;

namespace DownCraft.Tests;

public class Sha1Tests
{
    private const string AbcHash = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string EmptyHash = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    [Fact]
    public void KnownVectorForAbc()
    {
        Assert.Equal(AbcHash, Sha1.OfBytes(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void KnownVectorForEmptyInput()
    {
        Assert.Equal(EmptyHash, Sha1.OfBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void OfFileMatchesTheInMemoryHash()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("abc.txt", "abc");

        Assert.Equal(AbcHash, Sha1.OfFile(path));
    }

    [Fact]
    public async Task OfFileAsyncMatchesTheInMemoryHash()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("abc.txt", "abc");

        Assert.Equal(AbcHash, await Sha1.OfFileAsync(path));
    }

    [Fact]
    public void MatchesIgnoresCase()
    {
        Assert.True(Sha1.Matches(AbcHash, AbcHash));
        Assert.True(Sha1.Matches(AbcHash.ToUpperInvariant(), AbcHash));
        Assert.True(Sha1.Matches(AbcHash, AbcHash.ToUpperInvariant()));
        Assert.False(Sha1.Matches(AbcHash, new string('0', 40)));
    }

    [Fact]
    public void MatchesRejectsNullAndEmptyValues()
    {
        Assert.False(Sha1.Matches(null, AbcHash));
        Assert.False(Sha1.Matches(AbcHash, null));
        Assert.False(Sha1.Matches(null, null));
        Assert.False(Sha1.Matches(string.Empty, AbcHash));
        Assert.False(Sha1.Matches(AbcHash, string.Empty));
    }
}
