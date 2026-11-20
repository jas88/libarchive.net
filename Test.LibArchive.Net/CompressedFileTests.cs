using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static LibArchive.Net.LibArchiveReader;

namespace Test.LibArchive.Net;

// Reads single files that are compressed but are not archives, such as a plain .gz, which
// libarchive presents as an archive holding one entry. The fixtures are written by the
// reference tools from the same text: GNU gzip 1.15, bzip2 1.0.8, XZ Utils 5.8.4, zstd 1.5.7,
// lz4 1.11.1, lzop 1.04 and the bsdtar of macOS:
//
//     seq -f 'Line %g' 1 1000 > rawtest.txt
//     touch -t 202601010000.00 rawtest.txt
//     gzip -9 -n -c rawtest.txt > rawtest.txt.gz
//     gzip -9 -c rawtest.txt > rawtest-named.txt.gz
//     bzip2 -9 -c rawtest.txt > rawtest.txt.bz2
//     xz -9 -c rawtest.txt > rawtest.txt.xz
//     xz --format=lzma -9 -c rawtest.txt > rawtest.txt.lzma
//     zstd -19 -q -c rawtest.txt > rawtest.txt.zst
//     lz4 -9 -q -c rawtest.txt > rawtest.txt.lz4
//     lzop -9 -c rawtest.txt > rawtest.txt.lzo
//     COPYFILE_DISABLE=1 tar --format=ustar --no-xattrs --no-mac-metadata --no-acls \
//         --uid 0 --gid 0 --numeric-owner -cf - rawtest.txt | gzip -9 -n > rawtest.tar.gz
//     COPYFILE_DISABLE=1 tar --format=pax --no-xattrs --no-mac-metadata --no-acls \
//         --uid 0 --gid 0 --numeric-owner -cf - rawtest.txt | head -c 520 | gzip -9 -n > rawtest-truncated.tar.gz
//     : > rawtest-empty.txt
//     gzip -9 -n -c rawtest-empty.txt > rawtest-empty.txt.gz
//
// gzip -n stores neither the file name nor the time; without it gzip stores both, and
// libarchive then names the entry after the stored name. rawtest-truncated.tar.gz ends eight
// bytes into the extended header of its first entry, so that header cannot be read.
// rawtest-empty.txt.gz decompresses to nothing (rawtest-empty.txt itself is not kept).
// rawtest.txt itself is the uncompressed file that is not an archive. Bare "seq 1 1000" would
// not do for it: libarchive's mtree reader claims text whose lines each hold a single word,
// which then opens and is read as mtree, each word an entry with a warning (see
// MtreeTextTests.cs).
[TestFixture]
public class CompressedFileTests
{
    private const string PlainFile = "rawtest.txt";

    // The name libarchive's raw format gives an entry whose compressed file records no name
    private const string RawEntryName = "data";

    private const string UnrecognizedFormat = "Unrecognized archive format";

    // What "seq -f 'Line %g' 1 1000" prints: "Line 1" to "Line 1000", one per line
    private static readonly byte[] Expected =
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, 1000).Select(n => "Line " + n + "\n")));

    private static readonly string[] CompressedFiles =
    {
        "rawtest.txt.gz", "rawtest.txt.bz2", "rawtest.txt.xz", "rawtest.txt.lzma",
        "rawtest.txt.zst", "rawtest.txt.lz4", "rawtest.txt.lzo",
    };

    [Test]
    public void TestFixtureIsTheExpectedText()
    {
        Assert.That(File.ReadAllBytes(PlainFile), Is.EqualTo(Expected));
    }

    [TestCaseSource(nameof(CompressedFiles))]
    public void TestFirstEntryFromPath(string fixture)
    {
        using var reader = new LibArchiveReader(fixture);

        AssertSingleEntry(reader.FirstEntry(), RawEntryName);
        Assert.That(reader.FirstEntry(), Is.Null, "A compressed file holds exactly one entry");
    }

    [TestCaseSource(nameof(CompressedFiles))]
    public void TestEntriesFromPath(string fixture)
    {
        using var reader = new LibArchiveReader(fixture);

        AssertEntries(reader, RawEntryName);
    }

    [TestCaseSource(nameof(CompressedFiles))]
    public void TestFirstEntryFromStream(string fixture)
    {
        using var stream = File.OpenRead(fixture);
        using var reader = new LibArchiveReader(stream);

        AssertSingleEntry(reader.FirstEntry(), RawEntryName);
    }

    [TestCaseSource(nameof(CompressedFiles))]
    public void TestEntriesFromStream(string fixture)
    {
        using var stream = File.OpenRead(fixture);
        using var reader = new LibArchiveReader(stream);

        AssertEntries(reader, RawEntryName);
    }

    // A non-seekable stream cannot be rewound, so the bytes read while detecting the format
    // are replayed; a tiny block size makes that replay span many chunks.
    [Test]
    public void TestEntriesFromNonSeekableStream(
        [ValueSource(nameof(CompressedFiles))] string fixture, [Values(1u << 20, 16u)] uint blockSize)
    {
        using var stream = new ReaderApiTests.NonSeekableStream(new MemoryStream(File.ReadAllBytes(fixture)));
        using var reader = new LibArchiveReader(stream, blockSize: blockSize);

        AssertEntries(reader, RawEntryName);
    }

    [Test]
    public void TestSmallBlockSizeFromPath([Values(1u, 16u, 4096u)] uint blockSize)
    {
        using var reader = new LibArchiveReader("rawtest.txt.gz", blockSize);

        AssertEntries(reader, RawEntryName);
    }

    [Test]
    public void TestGzipStoredNameNamesTheEntry([Values] bool fromStream)
    {
        using var stream = fromStream ? File.OpenRead("rawtest-named.txt.gz") : null;
        using var reader = stream is null ? new LibArchiveReader("rawtest-named.txt.gz") : new LibArchiveReader(stream);

        AssertEntries(reader, PlainFile);
    }

    [Test]
    public void TestResetRereadsCompressedFile([Values] bool fromStream)
    {
        using var stream = fromStream ? File.OpenRead("rawtest.txt.xz") : null;
        using var reader = stream is null ? new LibArchiveReader("rawtest.txt.xz") : new LibArchiveReader(stream);

        AssertEntries(reader, RawEntryName);
        reader.Reset();
        AssertEntries(reader, RawEntryName);
    }

    [Test]
    public void TestMultiVolumeCompressedFile()
    {
        using var reader = new LibArchiveReader(new[] { "rawtest.txt.bz2" });

        AssertEntries(reader, RawEntryName);
    }

    [Test]
    public void TestTarGzIsStillReadAsTar([Values] bool fromStream)
    {
        using var stream = fromStream ? File.OpenRead("rawtest.tar.gz") : null;
        using var reader = stream is null ? new LibArchiveReader("rawtest.tar.gz") : new LibArchiveReader(stream);

        AssertTarEntries(reader);
    }

    // For compressed data, a non-seekable stream keeps the bytes it has given up until the first
    // header has been read (see MtreeTextTests.cs); an archive's entries read as before.
    [Test]
    public void TestTarGzFromNonSeekableStream([Values(1u << 20, 16u)] uint blockSize)
    {
        using var stream = new ReaderApiTests.NonSeekableStream(new MemoryStream(File.ReadAllBytes("rawtest.tar.gz")));
        using var reader = new LibArchiveReader(stream, blockSize: blockSize);

        AssertTarEntries(reader);
    }

    // Only text that libarchive's mtree reader claimed is read again as raw data when its first
    // header fails: a compressed archive whose first header fails reports its own error.
    [Test]
    public void TestTruncatedTarGzStillFailsAtFirstHeader([Values] bool fromStream)
    {
        const string fixture = "rawtest-truncated.tar.gz";
        using var stream = fromStream
            ? new ReaderApiTests.NonSeekableStream(new MemoryStream(File.ReadAllBytes(fixture)))
            : null;
        using var reader = stream is null ? new LibArchiveReader(fixture) : new LibArchiveReader(stream);

        var ex = Assert.Throws<ApplicationException>(() => reader.FirstEntry());
        Assert.That(ex!.Message, Is.EqualTo("Truncated tar archive detected while reading pax attribute name"));
    }

    // Non-empty data that is neither an archive nor compressed fails as it did before the raw
    // format was supported: in the constructor, with libarchive's own message.
    [Test]
    public void TestUncompressedNonArchiveStillThrows()
    {
        var fromPath = Assert.Throws<ApplicationException>(() => new LibArchiveReader(PlainFile).Dispose());
        Assert.That(fromPath!.Message, Is.EqualTo(UnrecognizedFormat));

        var fromVolumes = Assert.Throws<ApplicationException>(() => new LibArchiveReader(new[] { PlainFile }).Dispose());
        Assert.That(fromVolumes!.Message, Is.EqualTo(UnrecognizedFormat));

        using var stream = File.OpenRead(PlainFile);
        var fromStream = Assert.Throws<ApplicationException>(() => new LibArchiveReader(stream).Dispose());
        Assert.That(fromStream!.Message, Is.EqualTo(UnrecognizedFormat));

        using var nonSeekable = new ReaderApiTests.NonSeekableStream(new MemoryStream(Expected));
        var fromNonSeekable = Assert.Throws<ApplicationException>(() => new LibArchiveReader(nonSeekable).Dispose());
        Assert.That(fromNonSeekable!.Message, Is.EqualTo(UnrecognizedFormat));
    }

    // Empty data is not an unrecognized format: libarchive reads it as an archive with no
    // entries, from a stream (seekable or not), a file or a single volume alike.
    [Test]
    public void TestEmptyDataIsStillAnEmptyArchive()
    {
        using (var reader = new LibArchiveReader(new MemoryStream()))
        {
            Assert.That(reader.FirstEntry(), Is.Null);
            reader.Reset();
            Assert.That(reader.Entries(), Is.Empty, "after Reset");
        }

        using (var nonSeekable = new ReaderApiTests.NonSeekableStream(new MemoryStream()))
        using (var reader = new LibArchiveReader(nonSeekable))
            Assert.That(reader.Entries(), Is.Empty, "non-seekable stream");

        var emptyFile = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());
        try
        {
            using (var reader = new LibArchiveReader(emptyFile))
                Assert.That(reader.Entries(), Is.Empty, "file");
            using (var reader = new LibArchiveReader(new[] { emptyFile }))
                Assert.That(reader.Entries(), Is.Empty, "single volume");
        }
        finally
        {
            File.Delete(emptyFile);
        }
    }

    // Compressed data that decompresses to nothing has no entry either: libarchive's empty format
    // claims it, as it claims empty data (archive_read_support_format_empty.c:67-68), while the raw
    // format, which gives any other compressed file its one entry, needs at least one byte
    // (archive_read_support_format_raw.c:99-100).
    [Test]
    public void TestCompressedEmptyDataHasNoEntries()
    {
        const string fixture = "rawtest-empty.txt.gz";

        using (var reader = new LibArchiveReader(fixture))
        {
            Assert.That(reader.FirstEntry(), Is.Null);
            reader.Reset();
            Assert.That(reader.Entries(), Is.Empty, "after Reset");
        }

        var bytes = File.ReadAllBytes(fixture);
        using (var reader = new LibArchiveReader(new MemoryStream(bytes)))
            Assert.That(reader.Entries(), Is.Empty, "stream");
        using (var nonSeekable = new ReaderApiTests.NonSeekableStream(new MemoryStream(bytes)))
        using (var reader = new LibArchiveReader(nonSeekable))
            Assert.That(reader.Entries(), Is.Empty, "non-seekable stream");
    }

    private static void AssertEntries(LibArchiveReader reader, string expectedName)
    {
        var entries = reader.Entries().Select(e => (e.Name, e.Type, e.LengthBytes, Content: e.ReadAllBytes())).ToList();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo(expectedName));
        Assert.That(entries[0].Type, Is.EqualTo(EntryType.RegularFile));
        Assert.That(entries[0].LengthBytes, Is.Null, "The decompressed size is not known in advance");
        Assert.That(entries[0].Content, Is.EqualTo(Expected));
    }

    private static void AssertTarEntries(LibArchiveReader reader)
    {
        var entries = reader.Entries().Select(e => (e.Name, e.Type, e.LengthBytes, Content: e.ReadAllBytes())).ToList();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo(PlainFile));
        Assert.That(entries[0].Type, Is.EqualTo(EntryType.RegularFile));
        Assert.That(entries[0].LengthBytes, Is.EqualTo(Expected.Length));
        Assert.That(entries[0].Content, Is.EqualTo(Expected));
    }

    private static void AssertSingleEntry(Entry? entry, string expectedName)
    {
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Name, Is.EqualTo(expectedName));
        Assert.That(entry.IsRegularFile, Is.True);
        Assert.That(entry.ReadAllBytes(), Is.EqualTo(Expected));
    }
}
