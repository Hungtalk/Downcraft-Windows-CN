using System.IO.Compression;
using System.Text;
using McDownload.Core.Util;

namespace McDownload.Tests;

public class ZipServiceTests
{
    private static string CreateZip(TempDirectory temp, string fileName, params (string Name, string Content)[] entries)
    {
        var path = temp.Combine(fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
        }

        return path;
    }

    [Fact]
    public void ExtractAllExtractsFilesAndSkipsMetaInf()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(
            temp,
            "natives.jar",
            ("a.txt", "alpha"),
            ("sub/b.txt", "beta"),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0"));
        var destination = temp.Combine("natives");

        var extracted = ZipService.ExtractAll(zip, destination);

        Assert.Equal(2, extracted);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(destination, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "META-INF", "MANIFEST.MF")));
        Assert.False(Directory.Exists(Path.Combine(destination, "META-INF")));
    }

    [Fact]
    public void ExtractAllHonoursCustomExcludePrefixes()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, "lib.jar", ("a.txt", "alpha"), ("sub/b.txt", "beta"));
        var destination = temp.Combine("out");

        var extracted = ZipService.ExtractAll(zip, destination, ["sub/"]);

        Assert.Equal(1, extracted);
        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "sub", "b.txt")));
    }

    [Fact]
    public void ReadEntryReturnsTheEntryContent()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, "read.jar", ("a.txt", "alpha"), ("sub/b.txt", "beta"));

        var bytes = ZipService.ReadEntry(zip, "sub/b.txt");

        Assert.NotNull(bytes);
        Assert.Equal("beta", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void ReadEntryReturnsNullForAMissingEntry()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, "read.jar", ("a.txt", "alpha"));

        Assert.Null(ZipService.ReadEntry(zip, "does/not/exist.txt"));
    }

    [Fact]
    public void ListEntriesListsEveryEntry()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, "list.jar", ("a.txt", "alpha"), ("sub/b.txt", "beta"), ("META-INF/MANIFEST.MF", "x"));

        var entries = ZipService.ListEntries(zip);

        Assert.Contains("a.txt", entries);
        Assert.Contains("sub/b.txt", entries);
        Assert.Contains("META-INF/MANIFEST.MF", entries);
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void ExtractAllRejectsZipSlipEntriesAndWritesNothingOutsideTheDestination()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, "evil.zip", ("../evil.txt", "pwned"), ("good.txt", "ok"));
        var destination = temp.Combine("natives");

        Assert.Throws<InvalidDataException>(() => ZipService.ExtractAll(zip, destination));

        Assert.False(File.Exists(temp.Combine("evil.txt")));
        Assert.Empty(Directory.GetFiles(temp.Path, "evil.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void ExtractAllCreatesTheDestinationDirectoryForAnEmptyArchive()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, "empty.zip");
        var destination = temp.Combine("empty");

        var extracted = ZipService.ExtractAll(zip, destination);

        Assert.Equal(0, extracted);
        Assert.True(Directory.Exists(destination));
    }
}
