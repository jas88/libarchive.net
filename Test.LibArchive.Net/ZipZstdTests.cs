using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static Test.LibArchive.Net.ArchiveLayout;

namespace Test.LibArchive.Net;

// Reads Zstandard (method 93) zip entries whose data ends just as libarchive's zip reader fills
// its buffer of ZSTD_DStreamOutSize() bytes, 128 KiB: entries of 128 KiB or of a multiple of it.
// Unpatched, libarchive 3.8.11 does not see the end of such an entry, and the next read fails with
// "Truncated zstd file body" (see native/libarchive-zip-zstd-end-of-entry.patch). The entries are
// in a fixture that another tool wrote, and in archives that LibArchiveWriter writes.
//
// The fixture is written by Python 3.15's zipfile, which writes each entry's sizes in its local
// header, so that every way of reading it knows them. It was created on macOS with this script,
// run by python3 -I:
//
//     import zipfile
//     from compression import zstd
//
//     class FramePerWrite:  # in place of zipfile's compressor: a Zstandard frame for each write
//         def compress(self, data): return zstd.compress(data)
//         def flush(self): return b""
//
//     def text(name, size):
//         line = (name + "\n").encode("ascii")
//         return (line * (size // len(line) + 1))[:size]
//
//     def info(name):
//         zinfo = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
//         zinfo.compress_type = zipfile.ZIP_ZSTANDARD
//         zinfo.external_attr = 0o100644 << 16
//         return zinfo
//
//     with zipfile.ZipFile("zstdtest-python.zip", "w") as zf:
//         for size in (131071, 131072, 131073, 262144):
//             zf.writestr(info(f"{size}.txt"), text(f"{size}.txt", size))
//         name = "65536+131072+131072.txt"
//         with zf.open(info(name), "w") as dest:
//             dest._compressor = FramePerWrite()
//             data = text(name, 327680)
//             for start, end in ((0, 65536), (65536, 196608), (196608, 327680)):
//                 dest.write(data[start:end])
//
// Each entry holds its name and a newline, repeated and cut to its size. zipfile compresses each
// entry as one Zstandard frame; the last entry is three frames, of the sizes its name gives, as the
// script swaps zipfile's compressor for one that makes a frame of each write. 7-Zip (p7zip 17.05,
// with its zstd codec) and the zstd program read every entry in full; Python's zipfile reads only
// the first frame of the last one, and reports a bad CRC-32 for it.
[TestFixture]
public class ZipZstdTests
{
    private const string Fixture = "zstdtest-python.zip";

    private static readonly (string Name, int Size)[] Expected =
    {
        ("131071.txt", 131071), ("131072.txt", 131072), ("131073.txt", 131073), ("262144.txt", 262144),
        ("65536+131072+131072.txt", 327680),
    };

    // What the writer compresses: two multiples of 128 KiB, and a size on either side of it
    private static readonly (string Name, byte[] Data)[] Written =
    {
        ("131072.txt", CompressibleText(131072, seed: 1)), ("262144.txt", CompressibleText(262144, seed: 2)),
        ("131071.txt", CompressibleText(131071, seed: 3)), ("131073.txt", CompressibleText(131073, seed: 4)),
    };

    public enum Source { Path, Stream, NonSeekableStream }

    private string testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), $"zip-zstd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void Teardown() => Directory.Delete(testDirectory, true);

    [Test]
    public void ReadsEveryEntryInFull([Values] Source source)
    {
        using var reader = Open(source, Fixture);

        AssertEntries(reader);
    }

    // One byte per read from a seekable stream: libarchive seeks to each entry and then reads it a
    // byte at a time, so that each frame ends where a read does. Unpatched, libarchive took the end
    // of the first frame of the last entry for the end of that entry, and reported a bad CRC. (From
    // a non-seekable stream, libarchive reads all of this small fixture while it finds its format,
    // so the reads end elsewhere.)
    [Test]
    public void ReadsEveryEntryInFullFromAStreamOneByteAtATime()
    {
        using var reader = Open(Source.Stream, Fixture, 1);

        AssertEntries(reader);
    }

    // What the writer compresses with Zstandard reads back in full. libarchive's zip writer puts each
    // entry's sizes in a data descriptor after its data, which a reader of a non-seekable stream meets
    // only once it has read the data: libarchive then ends the entry with its frame, and so read these
    // entries from there even unpatched. From a path or a seekable stream it takes the sizes from the
    // central directory, and failed.
    [Test]
    public void ReadsBackWhatTheWriterCompresses([Values] Source source)
    {
        var path = Path.Combine(testDirectory, "written.zip");
        using (var writer = new LibArchiveWriter(path, ArchiveFormat.Zip, CompressionType.Zstd))
            AddEntries(writer, Written);
        Assert.That(ZipEntries(File.ReadAllBytes(path)).Select(e => (e.Name, e.Method, e.LocalMethod)),
            Is.EqualTo(Written.Select(e => (e.Name, (ushort)93, (ushort)93))), "Zstandard (method 93) in both headers");

        using var reader = Open(source, path);
        var entries = reader.Entries().Select(e => (e.Name, Data: e.ReadAllBytes())).ToList();

        Assert.That(entries.Select(e => e.Name), Is.EqualTo(Written.Select(e => e.Name)));
        for (var i = 0; i < Written.Length; i++)
            Assert.That(entries[i].Data, Is.EqualTo(Written[i].Data), Written[i].Name);
    }

    // The reader does not own a stream it is given; a MemoryStream needs no disposal.
    private static LibArchiveReader Open(Source source, string path, uint blockSize = 1 << 20) => source switch
    {
        Source.Path => new LibArchiveReader(path, blockSize),
        Source.Stream => new LibArchiveReader(new MemoryStream(File.ReadAllBytes(path)), blockSize: blockSize),
        Source.NonSeekableStream => new LibArchiveReader(
            new ReaderApiTests.NonSeekableStream(new MemoryStream(File.ReadAllBytes(path))), blockSize: blockSize),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static void AssertEntries(LibArchiveReader reader)
    {
        var entries = reader.Entries().Select(e => (e.Name, e.LengthBytes, Data: e.ReadAllBytes())).ToList();

        Assert.That(entries.Select(e => (e.Name, e.LengthBytes)),
            Is.EqualTo(Expected.Select(e => (e.Name, (long?)e.Size))));
        for (var i = 0; i < Expected.Length; i++)
            Assert.That(entries[i].Data, Is.EqualTo(Text(Expected[i].Name, Expected[i].Size)), Expected[i].Name);
    }

    // The name and a newline, repeated and cut to the size
    private static byte[] Text(string name, int size)
    {
        var line = Encoding.ASCII.GetBytes(name + "\n");
        var text = new byte[size];
        for (var i = 0; i < size; i++)
            text[i] = line[i % line.Length];
        return text;
    }
}
