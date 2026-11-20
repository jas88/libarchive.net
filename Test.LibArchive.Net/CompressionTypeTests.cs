using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static Test.LibArchive.Net.ArchiveLayout;

namespace Test.LibArchive.Net;

// The compression types and levels a format cannot use are rejected before anything is written, by every
// way of creating a writer; and the formats that do not compress their own entries are compressed by filters.
[TestFixture]
public class CompressionTypeTests
{
    // The formats that a filter compresses as a whole
    public static readonly ArchiveFormat[] FilterFormats =
    {
        ArchiveFormat.Tar, ArchiveFormat.Ustar, ArchiveFormat.Pax, ArchiveFormat.Cpio, ArchiveFormat.Iso9660,
    };

    public static readonly CompressionType[] Filters =
    {
        CompressionType.Gzip, CompressionType.Bzip2, CompressionType.Xz, CompressionType.Lzma,
        CompressionType.Lz4, CompressionType.Zstd, CompressionType.Compress, CompressionType.Lzip,
    };

    private const string ZipTypes = "None (DEFLATE, the default), Deflate, Bzip2, Lzma, Xz and Zstd.";
    private const string SevenZipTypes = "None (LZMA, the default), Deflate, Bzip2, Lzma, Xz (as LZMA2) and Zstd.";
    private const string XarTypes = "None (gzip, the default), Gzip, Bzip2, Lzma and Xz.";

    // Every format and compression type that cannot go together, with what the message says instead
    public static IEnumerable<TestCaseData> UnsupportedCombinations()
    {
        foreach (var compression in new[] { CompressionType.Gzip, CompressionType.Lz4, CompressionType.Compress, CompressionType.Lzip })
        {
            yield return new TestCaseData(ArchiveFormat.Zip, compression, ZipTypes);
            yield return new TestCaseData(ArchiveFormat.SevenZip, compression, SevenZipTypes);
        }
        foreach (var compression in new[] { CompressionType.Deflate, CompressionType.Lz4, CompressionType.Zstd, CompressionType.Compress, CompressionType.Lzip })
            yield return new TestCaseData(ArchiveFormat.Xar, compression, XarTypes);
        foreach (var format in FilterFormats)
            yield return new TestCaseData(format, CompressionType.Deflate,
                "libarchive has no DEFLATE filter. Use Gzip, which compresses the archive with DEFLATE in the gzip format.");
    }

    /// <summary>
    /// Asserts that every way of creating a writer throws, without creating the file, truncating an existing
    /// one, or writing to the stream.
    /// </summary>
    private static void AssertThrowsBeforeWriting<T>(ArchiveFormat format, CompressionType compression, int level, Action<T> check)
        where T : Exception
    {
        var path = Path.Combine(Path.GetTempPath(), $"compression-{Guid.NewGuid():N}.bin");
        try
        {
            check(Assert.Throws<T>(() => new LibArchiveWriter(path, format, compression, level).Dispose())!);
            Assert.That(File.Exists(path), Is.False, "the file is not created");

            var existing = Encoding.ASCII.GetBytes("existing content");
            File.WriteAllBytes(path, existing);
            check(Assert.Throws<T>(() => new LibArchiveWriter(path, format, compression, level).Dispose())!);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(existing), "an existing file is not truncated");
        }
        finally
        {
            File.Delete(path);
        }

        using var output = new MemoryStream();
        check(Assert.Throws<T>(() => new LibArchiveWriter(output, format, compression, level).Dispose())!);
        Assert.That(output.Length, Is.Zero, "nothing is written to the stream");

        check(Assert.Throws<T>(() => LibArchiveWriter.CreateMemoryWriter(format, compression, level).Dispose())!);
        check(Assert.Throws<T>(() => LibArchiveWriter.CreateMemoryWriter(4096, format, compression, level).Dispose())!);
    }

    // Level 0 too, although it stores the entries whatever the method
    [TestCaseSource(nameof(UnsupportedCombinations))]
    public void TestUnsupportedCombinationThrowsBeforeWriting(ArchiveFormat format, CompressionType compression, string supported)
    {
        foreach (var level in new[] { 6, 0 })
            AssertThrowsBeforeWriting<NotSupportedException>(format, compression, level, ex =>
            {
                Assert.That(ex.Message, Does.StartWith($"{compression} compression is not supported for the {format} format: "));
                Assert.That(ex.Message, Does.Contain(supported));
            });
    }

    // The method that the format uses where the one asked for is another's name for it
    [TestCase(ArchiveFormat.Zip, CompressionType.Gzip, "Deflate is the method gzip uses.")]
    [TestCase(ArchiveFormat.SevenZip, CompressionType.Gzip, "Deflate is the method gzip uses.")]
    [TestCase(ArchiveFormat.Xar, CompressionType.Deflate, "libarchive calls XAR's DEFLATE method gzip: use Gzip.")]
    public void TestMessageNamesTheFormatsMethod(ArchiveFormat format, CompressionType compression, string hint) =>
        AssertThrowsBeforeWriting<NotSupportedException>(format, compression, 6, ex => Assert.That(ex.Message, Does.EndWith(hint)));

    // A level outside 0 to 9 is rejected wherever it is used: by the method of a ZIP, 7-Zip or XAR archive,
    // even with None, which keeps the format's default method, and by a filter
    [Test]
    public void TestLevelOutOfRangeThrowsBeforeWriting(
        [Values(ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Xar, ArchiveFormat.Tar)] ArchiveFormat format,
        [Values(-1, 10)] int level)
    {
        var compression = format == ArchiveFormat.Tar ? CompressionType.Gzip : CompressionType.None;
        AssertThrowsBeforeWriting<ArgumentOutOfRangeException>(format, compression, level, ex =>
        {
            Assert.That(ex.ParamName, Is.EqualTo("compressionLevel"));
            Assert.That(ex.ActualValue, Is.EqualTo(level));
        });
    }

    // Without a filter, a tar archive has nothing to give the level to, and does not check it
    [Test]
    public void TestLevelIsNotCheckedWithoutAFilter()
    {
        var data = CompressibleText(1024);
        var tar = Write(ArchiveFormat.Tar, CompressionType.None, 10, ("a.txt", data));

        AssertFiles(tar, ("a.txt", data));
    }

    // The start of each filter's output: the gzip, bzip2, xz, LZ4 frame, zstd frame, compress and lzip magic,
    // and for lzma, LZMA alone's properties: lc=3, lp=0, pb=2, the 8 MiB dictionary of preset 6, the level
    private static byte[] SignatureOf(CompressionType compression) => compression switch
    {
        CompressionType.Gzip => new byte[] { 0x1f, 0x8b, 0x08 },
        CompressionType.Bzip2 => Encoding.ASCII.GetBytes("BZh6"),
        CompressionType.Xz => new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00 },
        CompressionType.Lzma => new byte[] { 0x5d, 0x00, 0x00, 0x80, 0x00 },
        CompressionType.Lz4 => new byte[] { 0x04, 0x22, 0x4d, 0x18 },
        CompressionType.Zstd => new byte[] { 0x28, 0xb5, 0x2f, 0xfd },
        CompressionType.Compress => new byte[] { 0x1f, 0x9d },
        CompressionType.Lzip => Encoding.ASCII.GetBytes("LZIP"),
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
    };

    [Test]
    public void TestOtherFormatsAreCompressedByFilters([ValueSource(nameof(FilterFormats))] ArchiveFormat format,
        [ValueSource(nameof(Filters))] CompressionType compression)
    {
        var data = CompressibleText(16 * 1024);
        var archive = Write(format, compression, 6, ("a.txt", data));

        Assert.That(archive.Take(SignatureOf(compression).Length), Is.EqualTo(SignatureOf(compression)), "the filter's output");
        AssertFiles(archive, ("a.txt", data));
    }

    // What each filter does at level 0, as the write guide's table says. Where level 0 is another level, or a
    // level that the filter refuses or ignores, the output is that level's; otherwise the header that the
    // filter writes records the level. These filters' output does not depend on when it is written, except
    // gzip's, whose header holds the time.
    [Test]
    public void TestFilterLevelZero([ValueSource(nameof(Filters))] CompressionType compression)
    {
        var data = CompressibleText(16 * 1024);
        byte[] Tar(int level) => Write(ArchiveFormat.Tar, compression, level, ("a.txt", data));
        var zero = Tar(0);

        switch (compression)
        {
            case CompressionType.Gzip:
                Assert.That(Contains(zero, data), Is.True, "stored blocks hold the data as it is");
                break;
            case CompressionType.Bzip2:
                Assert.That(Encoding.ASCII.GetString(zero, 0, 4), Is.EqualTo("BZh1"), "bzip2 header");
                Assert.That(zero, Is.EqualTo(Tar(1)), "level 1");
                break;
            case CompressionType.Xz:
                // The xz stream header, then the first block's header: its size, flags (one filter, no sizes),
                // the LZMA2 filter ID and properties size, and LZMA2's dictionary size
                Assert.That(zero.Skip(13).Take(3), Is.EqualTo(new byte[] { 0x00, 0x21, 0x01 }), "xz block header");
                Assert.That(zero[16], Is.EqualTo(Lzma2DictionarySize(LzmaPresetDictionaryLog[0])), "preset 0's dictionary");
                break;
            case CompressionType.Lzma:
                // LZMA alone: the properties byte, then the dictionary size
                Assert.That(ReadUInt32(zero, 1), Is.EqualTo(1u << LzmaPresetDictionaryLog[0]), "preset 0's dictionary");
                break;
            case CompressionType.Lzip:
                // "LZIP", version 1, and the coded dictionary size, here a power of two, 2^16
                // (archive_write_add_filter_xz.c:132-143, 239-268)
                Assert.That(zero.Take(6), Is.EqualTo(new byte[] { 0x4c, 0x5a, 0x49, 0x50, 0x01, 16 }), "lzip header");
                break;
            case CompressionType.Zstd:
                Assert.That(zero, Is.EqualTo(Tar(3)), "Zstandard's default level, 3");
                break;
            case CompressionType.Lz4:
                Assert.That(zero, Is.EqualTo(Tar(1)), "LZ4's default level, 1, as it refuses level 0");
                break;
            case CompressionType.Compress:
                Assert.That(zero, Is.EqualTo(Tar(9)), "the level is ignored");
                break;
            default:
                Assert.Fail($"No assertion for {compression}");
                break;
        }
        AssertFiles(zero, ("a.txt", data));
    }
}
