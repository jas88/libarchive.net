using System.Globalization;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// The writers' blockSize sets the size of the blocks libarchive hands to the file, stream or
// memory buffer (archive_write_set_bytes_per_block). The last block is never padded, so the block size must
// not change an archive's size. The expected sizes follow from the formats' layouts:
//
// - ustar: a 512-byte header per entry, data rounded up to 512 bytes, then 1024 zero bytes;
// - cpio newc: a 110-byte header plus the NUL-terminated name, padded to 4 bytes, then the
//   data, padded to 4 bytes, for each entry, then a TRAILER!!! entry.
[TestFixture]
public class BlockSizeTests
{
    private const uint DefaultBlockSize = 1 << 20;

    private static readonly DateTime MTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly byte[] Small = new byte[11];

    // A multiple of 512, so that a ustar archive holds no padding before its end marker
    private static readonly byte[] Large = new byte[100 * 1024];

    private static readonly uint[] BlockSizes = { 0, 1, 512, 10240, DefaultBlockSize };

    private string _path = null!;

    [SetUp]
    public void CreatePath() => _path = Path.Combine(Path.GetTempPath(), $"blocksize-{Guid.NewGuid():N}.bin");

    [TearDown]
    public void DeletePath() => File.Delete(_path);

    [TestCase(ArchiveFormat.Ustar, 2048)]
    [TestCase(ArchiveFormat.Cpio, 252)]
    public void TestSmallArchiveIsNotPadded(ArchiveFormat format, long expectedSize)
    {
        foreach (var blockSize in BlockSizes)
        {
            using (var writer = new LibArchiveWriter(_path, format, blockSize: blockSize))
                writer.AddEntry("a.txt", Small, MTime);

            Assert.That(new FileInfo(_path).Length, Is.EqualTo(expectedSize), $"file, blockSize {blockSize}");
            Assert.That(WriteToStream(format, blockSize, large: false).Length, Is.EqualTo(expectedSize), $"stream, blockSize {blockSize}");
            Assert.That(WriteToMemory(format, blockSize, large: false).Length, Is.EqualTo(expectedSize), $"memory, blockSize {blockSize}");
        }

        using var memoryWriter = LibArchiveWriter.CreateMemoryWriter(format);
        memoryWriter.AddEntry("a.txt", Small, MTime);
        memoryWriter.Dispose();
        Assert.That(memoryWriter.ToArray(), Has.Length.EqualTo(expectedSize), "memory, default block size");
    }

    // The same entries make the same bytes whatever the block size, and whether they go to a file,
    // a stream or memory. Every entry has the same modification time; ISO 9660 images also record
    // the time they were written, which WithoutWriteTime takes out.
    [TestCase(ArchiveFormat.Tar)]
    [TestCase(ArchiveFormat.Pax)]
    [TestCase(ArchiveFormat.Ustar)]
    [TestCase(ArchiveFormat.Cpio)]
    [TestCase(ArchiveFormat.Zip)]
    [TestCase(ArchiveFormat.SevenZip)]
    [TestCase(ArchiveFormat.Iso9660)]
    public void TestBlockSizeDoesNotChangeTheArchive(ArchiveFormat format)
    {
        byte[] Comparable(byte[] archive) => format == ArchiveFormat.Iso9660 ? WithoutWriteTime(archive) : archive;

        var reference = Comparable(WriteToStream(format, 10240, large: true));

        foreach (var blockSize in BlockSizes)
        {
            using (var writer = new LibArchiveWriter(_path, format, blockSize: blockSize))
            {
                writer.AddEntry("a.txt", Small, MTime);
                writer.AddEntry("b.bin", Large, MTime);
            }

            Assert.That(Comparable(File.ReadAllBytes(_path)), Is.EqualTo(reference), $"file, blockSize {blockSize}");
            Assert.That(Comparable(WriteToStream(format, blockSize, large: true)), Is.EqualTo(reference), $"stream, blockSize {blockSize}");
            Assert.That(Comparable(WriteToMemory(format, blockSize, large: true)), Is.EqualTo(reference), $"memory, blockSize {blockSize}");
        }
    }

    // An ISO 9660 image records the time libarchive wrote it out (time() when the writer closes:
    // archive_write_set_format_iso9660.c:1857-1877) in three forms. Each volume descriptor's
    // creation, modification and effective dates hold it as 17 bytes ("YYYYMMDDhhmmsscc" and the
    // offset from GMT, ECMA-119 8.4.26.1), the directories that libarchive adds itself (the root
    // here) have it as their 7-byte recording date (9.1.5) and Rock Ridge TF times, and the
    // information block after the descriptors starts with "INFO " and its ctime() text. This zeroes
    // every occurrence of the image's own time in each form, taken from its primary volume
    // descriptor, so that images written at different times compare equal when every other byte
    // does.
    private static byte[] WithoutWriteTime(byte[] iso)
    {
        const int primaryVolumeDescriptor = 16 * 2048;
        Assert.That(Encoding.ASCII.GetString(iso, primaryVolumeDescriptor + 1, 5), Is.EqualTo("CD001"));

        var dateTime = iso.Skip(primaryVolumeDescriptor + 813).Take(17).ToArray();
        var digits = Encoding.ASCII.GetString(dateTime, 0, 14);
        byte Field(int offset, int length, int bias = 0) =>
            (byte)(int.Parse(digits.Substring(offset, length), NumberStyles.None, CultureInfo.InvariantCulture) - bias);
        var recordingDate = new[]
        {
            Field(0, 4, 1900), Field(4, 2), Field(6, 2), Field(8, 2), Field(10, 2), Field(12, 2), dateTime[16],
        };

        var result = (byte[])iso.Clone();
        Assert.That(ZeroEvery(result, dateTime), Is.EqualTo(6), "creation, modification and effective dates of two descriptors");
        Assert.That(ZeroEvery(result, recordingDate), Is.GreaterThan(0), "recording dates");

        // ctime() text: "Www Mmm dd hh:mm:ss yyyy\n", 25 characters
        var info = NameEncodingTests.IndexOf(result, Encoding.ASCII.GetBytes("INFO "));
        Assert.That(info, Is.GreaterThan(primaryVolumeDescriptor), "information block");
        Assert.That(result[info + 5 + 24], Is.EqualTo((byte)'\n'), "ctime() text");
        Array.Clear(result, info + 5, 25);
        return result;
    }

    // Zeroes each occurrence of the pattern, and returns how many there were
    private static int ZeroEvery(byte[] data, byte[] pattern)
    {
        var count = 0;
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (!data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                continue;
            Array.Clear(data, i, pattern.Length);
            count++;
            i += pattern.Length - 1;
        }
        return count;
    }

    // libarchive writes a block to the file as soon as it is full, so the bytes on disk before
    // the writer is closed show the block size: whole blocks of a ustar archive's 103936 bytes
    // of headers and data (512 + 512 + 512 + 102400), and the 1024-byte end marker on closing.
    [TestCase(512u, 103936)]
    [TestCase(10240u, 102400)]
    [TestCase(DefaultBlockSize, 0)]
    public void TestFileWriterWritesInBlocks(uint blockSize, long expectedBeforeClose)
    {
        using (var writer = new LibArchiveWriter(_path, ArchiveFormat.Ustar, blockSize: blockSize))
        {
            writer.AddEntry("a.txt", Small, MTime);
            writer.AddEntry("b.bin", Large, MTime);

            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.That(file.Length, Is.EqualTo(expectedBeforeClose));
        }

        Assert.That(new FileInfo(_path).Length, Is.EqualTo(103936 + 1024));
    }

    [Test]
    public void TestStreamWriterWritesInBlocks(
        [Values(ArchiveFormat.Ustar, ArchiveFormat.Cpio, ArchiveFormat.Zip, ArchiveFormat.SevenZip)] ArchiveFormat format,
        [Values(512u, 4096u)] uint blockSize)
    {
        using var output = new RecordingStream();
        using (var writer = new LibArchiveWriter(output, format, blockSize: blockSize))
        {
            writer.AddEntry("a.txt", Small, MTime);
            writer.AddEntry("b.bin", Large, MTime);
        }

        Assert.That(output.Writes, Is.Not.Empty);
        Assert.That(output.Writes.Take(output.Writes.Count - 1), Is.All.EqualTo((int)blockSize),
            "Every write but the last should be one whole block");
        Assert.That(output.Writes.Last(), Is.InRange(1, (int)blockSize));
    }

    // The memory writer's buffer shows the block size in its capacity. A MemoryStream created with no
    // capacity allocates what its first write needs, then at least doubles its capacity whenever a
    // write does not fit. So the 104960 bytes of a ustar archive leave a capacity of 131072 (512
    // doubled 8 times) when written in 512-byte blocks, 163840 (10240 doubled 4 times) in 10240-byte
    // blocks, and exactly 104960 in 1 MiB blocks, where the archive arrives in one write on closing.
    [TestCase(512u, 131072)]
    [TestCase(10240u, 163840)]
    [TestCase(DefaultBlockSize, 103936 + 1024)]
    public void TestMemoryWriterWritesInBlocks(uint blockSize, int expectedCapacity)
    {
        using var writer = LibArchiveWriter.CreateMemoryWriter(blockSize, ArchiveFormat.Ustar, initialCapacity: 0);
        writer.AddEntry("a.txt", Small, MTime);
        writer.AddEntry("b.bin", Large, MTime);
        writer.Dispose();

        var buffer = writer.ToMemoryStream();
        Assert.That(buffer.Length, Is.EqualTo(103936 + 1024));
        Assert.That(buffer.Capacity, Is.EqualTo(expectedCapacity));
    }

    // Without a block size, the memory writer writes in 1 MiB blocks, so this archive arrives in one write
    [Test]
    public void TestMemoryWriterWritesInOneMebibyteBlocksByDefault()
    {
        using var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Ustar, initialCapacity: 0);
        writer.AddEntry("a.txt", Small, MTime);
        writer.AddEntry("b.bin", Large, MTime);
        writer.Dispose();

        Assert.That(writer.ToMemoryStream().Capacity, Is.EqualTo(103936 + 1024));
    }

    private static byte[] WriteToStream(ArchiveFormat format, uint blockSize, bool large)
    {
        using var output = new MemoryStream();
        using (var writer = new LibArchiveWriter(output, format, blockSize: blockSize))
        {
            writer.AddEntry("a.txt", Small, MTime);
            if (large)
                writer.AddEntry("b.bin", Large, MTime);
        }
        return output.ToArray();
    }

    private static byte[] WriteToMemory(ArchiveFormat format, uint blockSize, bool large)
    {
        using var writer = LibArchiveWriter.CreateMemoryWriter(blockSize, format);
        writer.AddEntry("a.txt", Small, MTime);
        if (large)
            writer.AddEntry("b.bin", Large, MTime);
        writer.Dispose();
        return writer.ToArray();
    }

    // Records the length of every write the writer makes
    private sealed class RecordingStream : MemoryStream
    {
        public List<int> Writes { get; } = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes.Add(count);
            base.Write(buffer, offset, count);
        }
    }
}
