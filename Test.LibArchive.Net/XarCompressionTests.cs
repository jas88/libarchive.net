using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static Test.LibArchive.Net.ArchiveLayout;

namespace Test.LibArchive.Net;

// XAR compresses each file itself: the compression type selects the encoding that the table of contents
// records for each file's data, and no filter wraps the archive, which still starts with its "xar!" header.
// The data itself shows the method, and with BZIP2, LZMA and XZ the level: "BZh" and the level, LZMA alone's
// properties with the dictionary size, which each preset sets, and the xz magic followed by a block header
// with LZMA2's dictionary size. XAR's gzip method writes zlib streams.
//
// Each test writes its first archive through XarTests.WriteArchiveOrIgnore, which knows where XAR can be
// written: on a macOS libarchive without libxml2 the test is ignored.
[TestFixture]
public class XarCompressionTests
{
    public static readonly CompressionType[] Types =
    {
        CompressionType.None, CompressionType.Gzip, CompressionType.Bzip2, CompressionType.Lzma, CompressionType.Xz,
    };

    // archive_write_set_format_xar.c:1041-1052
    private static string EncodingOf(CompressionType compression) => compression switch
    {
        CompressionType.None or CompressionType.Gzip => "application/x-gzip",
        CompressionType.Bzip2 => "application/x-bzip2",
        CompressionType.Lzma => "application/x-lzma",
        CompressionType.Xz => "application/x-xz",
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
    };

    // The last name has characters beyond Latin-1, which the table of contents holds in base64
    private static readonly (string Name, byte[] Data)[] Entries =
    {
        ("first.txt", CompressibleText(16 * 1024, seed: 1)),
        ("second.txt", CompressibleText(8 * 1024, seed: 2)),
        ("日本語.txt", CompressibleText(4 * 1024, seed: 3)),
    };

    private static readonly byte[] Text = CompressibleText(128 * 1024);

    // The start of each method's data: a zlib header for DEFLATE with a 32 KiB window, whose check bits make
    // it a multiple of 31; "BZh"; LZMA alone's properties byte for lc=3, lp=0, pb=2; and the xz magic
    private static void AssertMethodOf(byte[] data, CompressionType compression)
    {
        switch (compression)
        {
            case CompressionType.None:
            case CompressionType.Gzip:
                Assert.That(data[0], Is.EqualTo(0x78), "zlib header");
                Assert.That((data[0] << 8 | data[1]) % 31, Is.Zero, "zlib header check bits");
                break;
            case CompressionType.Bzip2:
                Assert.That(Encoding.ASCII.GetString(data, 0, 3), Is.EqualTo("BZh"), "bzip2 header");
                break;
            case CompressionType.Lzma:
                Assert.That(data[0], Is.EqualTo(0x5d), "LZMA alone properties");
                break;
            case CompressionType.Xz:
                Assert.That(StartsWith(data, 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00), Is.True, "xz magic");
                break;
        }
    }

    // What the data records of the level: BZIP2 its block size, from "BZh1" to "BZh9"; LZMA alone its
    // dictionary size; xz, in the header of its first block after the 12-byte stream header, the block flags
    // (one filter, no sizes), the LZMA2 filter ID and properties size, and LZMA2's dictionary size. zlib
    // records a class of level: fastest (0 and 1), fast (2 to 5), default (6) or maximum (7 to 9).
    private static void AssertLevel(byte[] data, CompressionType compression, int level)
    {
        AssertMethodOf(data, compression);
        var dictionaryLog = LzmaPresetDictionaryLog[level];
        switch (compression)
        {
            case CompressionType.Bzip2:
                Assert.That((char)data[3], Is.EqualTo((char)('0' + level)), "bzip2 block size");
                break;
            case CompressionType.Lzma:
                Assert.That(ReadUInt32(data, 1), Is.EqualTo(1u << dictionaryLog), "dictionary size");
                break;
            case CompressionType.Xz:
                Assert.That(data.Skip(13).Take(3), Is.EqualTo(new byte[] { 0x00, 0x21, 0x01 }), "xz block header");
                Assert.That(data[16], Is.EqualTo(Lzma2DictionarySize(dictionaryLog)), "dictionary size");
                break;
            default:
                Assert.That(data[1] >> 6, Is.EqualTo(level < 2 ? 0 : level < 6 ? 1 : level == 6 ? 2 : 3), "zlib level");
                break;
        }
    }

    [Test]
    public void TestCompressionSelectsTheEncoding([Values] WriterKind kind, [ValueSource(nameof(Types))] CompressionType compression)
    {
        var xar = XarTests.WriteArchiveOrIgnore(() => Write(kind, ArchiveFormat.Xar, compression, 6, w => AddEntries(w, Entries)));

        Assert.That(Encoding.ASCII.GetString(xar, 0, 4), Is.EqualTo("xar!"), "the archive starts with the XAR header: no filter wraps it");
        var files = XarFiles(xar);
        Assert.That(files.Select(f => f.Name), Is.EquivalentTo(Entries.Select(e => e.Name)));
        foreach (var file in files)
        {
            Assert.That(file.Encoding, Is.EqualTo(EncodingOf(compression)), $"encoding of {file.Name}");
            AssertLevel(file.Data, compression, 6);
        }
        AssertFiles(xar, Entries);
    }

    // The level reaches each method: level 9 compresses this text better than level 1
    [Test]
    public void TestLevelsOneAndNineDiffer([ValueSource(nameof(Types))] CompressionType compression)
    {
        var fastest = XarTests.WriteArchiveOrIgnore(() => Write(ArchiveFormat.Xar, compression, 1, ("text.txt", Text)));

        var smallest = Write(ArchiveFormat.Xar, compression, 9, ("text.txt", Text));

        AssertLevel(XarFiles(fastest).Single().Data, compression, 1);
        AssertLevel(XarFiles(smallest).Single().Data, compression, 9);
        Assert.That(smallest.Length, Is.LessThan(fastest.Length));
        AssertFiles(fastest, ("text.txt", Text));
        AssertFiles(smallest, ("text.txt", Text));
    }

    // Level 0 stores the files, whatever the method, with XAR's none method, which writes each file's data as
    // it is and records it as application/octet-stream (archive_write_set_format_xar.c:750-753, 1050-1051)
    [Test]
    public void TestLevelZeroStores([ValueSource(nameof(Types))] CompressionType compression)
    {
        var xar = XarTests.WriteArchiveOrIgnore(() => Write(ArchiveFormat.Xar, compression, 0, Entries));

        Assert.That(Encoding.ASCII.GetString(xar, 0, 4), Is.EqualTo("xar!"), "the archive starts with the XAR header: no filter wraps it");
        var files = XarFiles(xar);
        Assert.That(files.Select(f => f.Name), Is.EquivalentTo(Entries.Select(e => e.Name)));
        foreach (var file in files)
        {
            Assert.That(file.Encoding, Is.EqualTo("application/octet-stream"), $"encoding of {file.Name}");
            Assert.That(file.Data, Is.EqualTo(Entries.Single(e => e.Name == file.Name).Data), $"{file.Name} is stored as it is");
        }
        AssertFiles(xar, Entries);
    }

    // The default call writes what gzip, XAR's default method, writes at level 6, libarchive's default level,
    // but for the time it records the archive was written at
    [Test]
    public void TestDefaultIsGzipAtLevelSix([Values] WriterKind kind)
    {
        void Add(LibArchiveWriter writer) => AddEntries(writer, Entries);
        var defaults = XarTests.WriteArchiveOrIgnore(() => Write(kind, ArchiveFormat.Xar, compression: null, 0, Add));

        Assert.That(XarFiles(defaults).Select(f => f.Encoding), Has.All.EqualTo(EncodingOf(CompressionType.Gzip)));
        foreach (var (compression, archive) in new[]
                 {
                     (CompressionType.Gzip, Write(kind, ArchiveFormat.Xar, CompressionType.Gzip, 6, Add)),
                     (CompressionType.None, Write(kind, ArchiveFormat.Xar, CompressionType.None, 6, Add)),
                 })
        {
            Assert.That(XarTableOfContentsWithoutCreationTime(archive), Is.EqualTo(XarTableOfContentsWithoutCreationTime(defaults)),
                $"table of contents, {compression}, level 6");
            Assert.That(XarFileData(archive), Is.EqualTo(XarFileData(defaults)), $"file data, {compression}, level 6");
        }
    }
}
