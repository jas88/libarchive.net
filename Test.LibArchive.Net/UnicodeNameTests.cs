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
/// process locale (see NameEncodingTests).
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

    // Entry names: Latin-1 (precomposed, NFC), CJK, an astral-plane emoji, and a directory
    private static readonly string[] EntryNames =
    {
        "Größe.txt",
        "日本語.txt",
        "\U0001F600.txt",
        "Ordner-é/中文-ä.txt",
    };

    private static string ContentOf(string name) => "content of " + name;

    public enum WriterKind { File, Stream, Memory }

    // The formats whose writers keep each entry's path as it is given, nested ones included. The
    // ISO 9660 and XAR writers build a directory tree, adding the parent directories themselves,
    // so NameEncodingTests covers them, with names that have no directory part, as it covers the
    // bytes every format stores.
    private static readonly ArchiveFormat[] PathFormats =
    {
        ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.Ustar,
        ArchiveFormat.Pax, ArchiveFormat.Cpio,
    };

    // Those that store names as UTF-8 (7-Zip stores them as UTF-16LE)
    private static readonly ArchiveFormat[] Utf8Formats =
    {
        ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.Ustar, ArchiveFormat.Pax, ArchiveFormat.Cpio,
    };

    private byte[] WriteArchive(ArchiveFormat format, WriterKind kind, out string path)
    {
        path = Path.Combine(testDirectory, $"names-{format}-{kind}.bin");
        void AddEntries(LibArchiveWriter w)
        {
            foreach (var name in EntryNames)
                w.AddEntry(name, Encoding.UTF8.GetBytes(ContentOf(name)));
        }

        switch (kind)
        {
            case WriterKind.File:
                using (var writer = new LibArchiveWriter(path, format))
                    AddEntries(writer);
                return File.ReadAllBytes(path);
            case WriterKind.Stream:
                using (var stream = File.Create(path))
                using (var writer = new LibArchiveWriter(stream, format))
                    AddEntries(writer);
                return File.ReadAllBytes(path);
            default:
                var memoryWriter = LibArchiveWriter.CreateMemoryWriter(format);
                using (memoryWriter)
                    AddEntries(memoryWriter);
                var bytes = memoryWriter.ToArray();
                File.WriteAllBytes(path, bytes);
                return bytes;
        }
    }

    private static (string Name, string Text)[] ReadEntries(LibArchiveReader reader) =>
        reader.Entries().Select(e => (e.Name, e.ReadAllText())).ToArray();

    [Test]
    public void NonAsciiEntryNamesRoundTrip(
        [ValueSource(nameof(PathFormats))] ArchiveFormat format,
        [Values] WriterKind kind)
    {
        var bytes = WriteArchive(format, kind, out var path);
        var expected = EntryNames.Select(n => (n, ContentOf(n))).ToArray();

        using (var reader = new LibArchiveReader(path))
        {
            Assert.That(ReadEntries(reader), Is.EqualTo(expected), "read from path");
            reader.Reset();
            Assert.That(ReadEntries(reader), Is.EqualTo(expected), "after Reset");
        }

        using (var reader = new LibArchiveReader(new MemoryStream(bytes)))
            Assert.That(ReadEntries(reader), Is.EqualTo(expected), "read from stream");
    }

    [Test]
    public void NonAsciiEntryNamesAreStoredAsUtf8Bytes([ValueSource(nameof(Utf8Formats))] ArchiveFormat format)
    {
        // Each name must be in the archive as its UTF-8 encoding, not as '?' or another code page
        var bytes = WriteArchive(format, WriterKind.Memory, out _);
        foreach (var name in EntryNames)
            Assert.That(IndexOf(bytes, Encoding.UTF8.GetBytes(name)), Is.GreaterThanOrEqualTo(0), name);
    }

    [Test]
    public void AddFileUsesNonAsciiSourceFileName([ValueSource(nameof(PathFormats))] ArchiveFormat format)
    {
        var source = Path.Combine(testDirectory, "Quelle-ß-日本-\U0001F600.txt");
        File.WriteAllText(source, "from disk");
        var archive = Path.Combine(testDirectory, $"addfile-{format}.bin");

        using (var writer = new LibArchiveWriter(archive, format))
            writer.AddFile(source);

        using var reader = new LibArchiveReader(archive);
        Assert.That(ReadEntries(reader), Is.EqualTo(new[] { (Path.GetFileName(source), "from disk") }));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                return i;
        }
        return -1;
    }
}
}
