using LibArchive.Net;
using NUnit.Framework;
using static Test.LibArchive.Net.ArchiveLayout;

namespace Test.LibArchive.Net;

// 7-Zip compresses the entries itself: the compression type selects the coder that the archive's header
// records for its packed data, by 7-Zip method ID, and no filter wraps the archive, which still starts with
// the 7-Zip signature.
//
// The coder is read from the header because that is the archive's own record of how its data is packed, the
// one every 7-Zip reader goes by. libarchive leaves the header unencoded only when the archive has a single
// entry, as the 7z command does (archive_write_set_format_7zip.c:892-895), and LZMA-encodes it otherwise, so
// the archives whose coder is read hold one entry. The coder's properties show the level too: LZMA records the
// dictionary size, which each preset sets, LZMA2 the same in one byte, and Zstandard the level itself
// (archive_write_set_format_7zip.c:2419-2421).
[TestFixture]
public class SevenZipCompressionTests
{
    public static readonly CompressionType[] Types =
    {
        CompressionType.None, CompressionType.Lzma, CompressionType.Xz, CompressionType.Deflate,
        CompressionType.Bzip2, CompressionType.Zstd,
    };

    // 7-Zip method IDs, as libarchive writes them (archive_write_set_format_7zip.c:69-76)
    private static byte[] CoderOf(CompressionType compression) => compression switch
    {
        CompressionType.None or CompressionType.Lzma => new byte[] { 0x03, 0x01, 0x01 },
        CompressionType.Xz => new byte[] { 0x21 },
        CompressionType.Deflate => new byte[] { 0x04, 0x01, 0x08 },
        CompressionType.Bzip2 => new byte[] { 0x04, 0x02, 0x02 },
        CompressionType.Zstd => new byte[] { 0x04, 0xf7, 0x11, 0x01 },
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
    };

    private static readonly byte[] Text = CompressibleText(128 * 1024);

    [Test]
    public void TestCompressionSelectsTheCoder([Values] WriterKind kind, [ValueSource(nameof(Types))] CompressionType compression)
    {
        var archive = Write(kind, ArchiveFormat.SevenZip, compression, 6, w => w.AddEntry("text.txt", Text, MTime));

        Assert.That(StartsWith(archive, SevenZipSignature), Is.True, "the archive starts with the 7-Zip signature: no filter wraps it");
        Assert.That(SevenZipFirstCoder(archive).Id, Is.EqualTo(CoderOf(compression)));
        AssertFiles(archive, ("text.txt", Text));
    }

    // With more than one entry the header is encoded, so this only checks that the entries read back
    [Test]
    public void TestSeveralEntriesReadBack([ValueSource(nameof(Types))] CompressionType compression)
    {
        var entries = new[]
        {
            ("first.txt", CompressibleText(16 * 1024, seed: 1)),
            ("empty.txt", Array.Empty<byte>()),
            ("second.txt", CompressibleText(8 * 1024, seed: 2)),
        };
        var archive = Write(ArchiveFormat.SevenZip, compression, 6, entries);

        Assert.That(StartsWith(archive, SevenZipSignature), Is.True, "the archive starts with the 7-Zip signature: no filter wraps it");
        AssertFiles(archive, entries);
    }

    // What the coder records of the level
    private static void AssertCoder(byte[] archive, CompressionType compression, int level)
    {
        var coder = SevenZipFirstCoder(archive);
        Assert.That(coder.Id, Is.EqualTo(CoderOf(compression)), "coder");
        switch (compression)
        {
            case CompressionType.None:
            case CompressionType.Lzma:
                // lc, lp and pb in one byte, then the dictionary size
                Assert.That(coder.Properties, Has.Length.EqualTo(5));
                Assert.That(ReadUInt32(coder.Properties, 1), Is.EqualTo(1u << LzmaPresetDictionaryLog[level]), "dictionary size");
                break;
            case CompressionType.Xz:
                Assert.That(coder.Properties, Is.EqualTo(new[] { Lzma2DictionarySize(LzmaPresetDictionaryLog[level]) }), "dictionary size");
                break;
            case CompressionType.Zstd:
                // zstd's major and minor version, the level, and two reserved bytes
                Assert.That(coder.Properties, Has.Length.EqualTo(5));
                Assert.That(coder.Properties[2], Is.EqualTo(level), "level");
                break;
            default:
                Assert.That(coder.Properties, Is.Empty, "DEFLATE and BZIP2 coders have no properties");
                break;
        }
    }

    // The level reaches each method: level 9 compresses this text better than level 1
    [Test]
    public void TestLevelsOneAndNineDiffer([ValueSource(nameof(Types))] CompressionType compression)
    {
        var fastest = Write(ArchiveFormat.SevenZip, compression, 1, ("text.txt", Text));
        var smallest = Write(ArchiveFormat.SevenZip, compression, 9, ("text.txt", Text));

        AssertCoder(fastest, compression, 1);
        AssertCoder(smallest, compression, 9);
        Assert.That(smallest.Length, Is.LessThan(fastest.Length));
        AssertFiles(fastest, ("text.txt", Text));
        AssertFiles(smallest, ("text.txt", Text));
    }

    // Level 0 is LZMA's and LZMA2's preset 0, their fastest, which still compresses
    [TestCase(CompressionType.None)]
    [TestCase(CompressionType.Lzma)]
    [TestCase(CompressionType.Xz)]
    public void TestLevelZeroIsTheFastestPreset(CompressionType compression)
    {
        var archive = Write(ArchiveFormat.SevenZip, compression, 0, ("text.txt", Text));

        AssertCoder(archive, compression, 0);
        Assert.That(archive, Has.Length.LessThan(Text.Length / 2), "compressed");
        AssertFiles(archive, ("text.txt", Text));
    }

    // Level 0 is DEFLATE's level 0, which stores the data in uncompressed blocks
    [Test]
    public void TestLevelZeroDeflateStoresTheData()
    {
        var archive = Write(ArchiveFormat.SevenZip, CompressionType.Deflate, 0, ("text.txt", Text));

        AssertCoder(archive, CompressionType.Deflate, 0);
        Assert.That(archive, Has.Length.GreaterThan(Text.Length), "not compressed");
        Assert.That(Contains(archive, Text.Take(4096).ToArray()), Is.True, "the text is in the archive as it is");
        AssertFiles(archive, ("text.txt", Text));
    }

    // Level 0 is Zstandard's level 0, which zstd takes as its default level, 3: the data is packed as at level 3
    [Test]
    public void TestLevelZeroZstdIsLevelThree()
    {
        var zero = Write(ArchiveFormat.SevenZip, CompressionType.Zstd, 0, ("text.txt", Text));
        var three = Write(ArchiveFormat.SevenZip, CompressionType.Zstd, 3, ("text.txt", Text));

        AssertCoder(zero, CompressionType.Zstd, 0);
        AssertCoder(three, CompressionType.Zstd, 3);
        Assert.That(SevenZipPackedData(zero), Is.EqualTo(SevenZipPackedData(three)));
        AssertFiles(zero, ("text.txt", Text));
    }

    // The default call writes what LZMA, 7-Zip's default method, writes at level 6, libarchive's default level
    [Test]
    public void TestDefaultIsLzmaAtLevelSix([Values] WriterKind kind)
    {
        void Add(LibArchiveWriter writer) =>
            AddEntries(writer, new[] { ("first.txt", CompressibleText(16 * 1024, seed: 1)), ("second.txt", Text) });
        var defaults = Write(kind, ArchiveFormat.SevenZip, compression: null, 0, Add);

        Assert.That(StartsWith(defaults, SevenZipSignature), Is.True);
        Assert.That(Write(kind, ArchiveFormat.SevenZip, CompressionType.Lzma, 6, Add), Is.EqualTo(defaults), "Lzma, level 6");
        Assert.That(Write(kind, ArchiveFormat.SevenZip, CompressionType.None, 6, Add), Is.EqualTo(defaults), "None, level 6");
    }
}
