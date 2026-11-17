using System;
using System.IO;
using System.Linq;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net
{
/// <summary>
/// Archives whose file paths contain characters outside ASCII. None of these tests depends on the
/// machine's locale: .NET never calls setlocale, so libarchive always runs in the "C" locale.
/// </summary>
[TestFixture]
public class UnicodeNameTests
{
    // Latin-1, CJK and a character outside the Basic Multilingual Plane (a UTF-16 surrogate pair)
    private const string NonAsciiDirectoryName = "Ünïcödé 日本 \U0001F600";

    private string testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), $"libarchive-unicode-{Guid.NewGuid():N}", NonAsciiDirectoryName);
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void Teardown()
    {
        var root = Path.GetDirectoryName(testDirectory)!;
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private static string[] Names(LibArchiveReader reader) => reader.Entries().Select(e => e.Name).ToArray();

    [Test]
    public void WriterAndReaderOpenNonAsciiPath()
    {
        var path = Path.Combine(testDirectory, "Archiv-Größe-日本.zip");

        using (var writer = new LibArchiveWriter(path, ArchiveFormat.Zip))
        {
            writer.AddEntry("a.txt", Encoding.UTF8.GetBytes("first"));
            writer.AddEntry("b.txt", Encoding.UTF8.GetBytes("second"));
        }

        // The file must exist under its real (UTF-16) name, not a mis-converted one
        Assert.That(File.Exists(path), Is.True);
        Assert.That(Directory.GetFiles(testDirectory), Is.EqualTo(new[] { path }));

        using var reader = new LibArchiveReader(path);
        var entries = reader.Entries().Select(e => (e.Name, Text: e.ReadAllText())).ToArray();
        Assert.That(entries, Is.EqualTo(new[] { ("a.txt", "first"), ("b.txt", "second") }));

        reader.Reset();
        Assert.That(Names(reader), Is.EqualTo(new[] { "a.txt", "b.txt" }));
    }

    [Test]
    public void ReaderOpensMultiVolumeArchiveWithNonAsciiPaths()
    {
        var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Ustar);
        using (writer)
        {
            writer.AddEntry("one.txt", Encoding.UTF8.GetBytes(new string('1', 3000)));
            writer.AddEntry("two.txt", Encoding.UTF8.GetBytes(new string('2', 3000)));
        }
        var archive = writer.ToArray();

        // libarchive reads the volumes as one stream, so splitting the tar anywhere is enough
        var parts = new[] { 0, archive.Length / 3, 2 * archive.Length / 3, archive.Length };
        var volumes = new string[3];
        for (int i = 0; i < volumes.Length; i++)
        {
            volumes[i] = Path.Combine(testDirectory, $"Teil-ü-日-{i + 1}.tar");
            File.WriteAllBytes(volumes[i], archive.Skip(parts[i]).Take(parts[i + 1] - parts[i]).ToArray());
        }

        using var reader = new LibArchiveReader(volumes);
        var entries = reader.Entries().Select(e => (e.Name, e.ReadAllText().Length)).ToArray();
        Assert.That(entries, Is.EqualTo(new[] { ("one.txt", 3000), ("two.txt", 3000) }));

        reader.Reset();
        Assert.That(Names(reader), Is.EqualTo(new[] { "one.txt", "two.txt" }));
    }

    [Test]
    public void ReaderReportsMissingNonAsciiPath()
    {
        var path = Path.Combine(testDirectory, "fehlt-ä.zip");
        Assert.Throws<ApplicationException>(() => new LibArchiveReader(path).Dispose());
    }
}
}
