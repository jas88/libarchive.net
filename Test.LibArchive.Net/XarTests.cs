using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static Test.LibArchive.Net.ArchiveLayout;

namespace Test.LibArchive.Net;

// libarchive reads XAR with libxml2 or expat, and writes it only with libxml2, which the native
// libraries of every platform link (see ArchiveFormat.Xar), so these tests read and write it
// everywhere. Every test that writes XAR does so through WriteOrIgnore.
//
// The fixture was written by the bsdtar of macOS (bsdtar 3.5.3, libarchive 3.7.4):
//
//     printf 'XAR test entry\n' > xartest.txt
//     touch -t 202601010000.00 xartest.txt
//     COPYFILE_DISABLE=1 tar --format=xar --uid 0 --gid 0 --numeric-owner --no-xattrs \
//         --no-acls --no-mac-metadata -cf xartest.xar xartest.txt
[TestFixture]
public class XarTests
{
    private const string Fixture = "xartest.xar";

    private static readonly byte[] Content = Encoding.ASCII.GetBytes("XAR test entry\n");

    private const string CannotWrite = "Xar not supported on this platform";

    /// <summary>
    /// Runs <paramref name="write"/>, which creates a XAR writer and writes with it, and ignores the test if
    /// the library cannot write XAR: a macOS libarchive without libxml2. Every test that writes XAR comes
    /// through here, so this is the one place that knows where XAR can be written.
    /// </summary>
    internal static void WriteOrIgnore(Action write)
    {
        try
        {
            write();
        }
        catch (ApplicationException ex) when (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && ex.Message == CannotWrite)
        {
            // The macOS libraries this project builds include libxml2; Homebrew's libarchive,
            // which can stand in for them locally, has only expat, which reads XAR but cannot write it.
            Assert.Ignore("This macOS libarchive was built without libxml2, so it cannot write XAR");
        }
    }

    /// <summary>Writes a XAR archive through <see cref="WriteOrIgnore"/>.</summary>
    /// <returns>The archive.</returns>
    internal static byte[] WriteArchiveOrIgnore(Func<byte[]> write)
    {
        byte[] archive = Array.Empty<byte>();
        WriteOrIgnore(() => archive = write());
        return archive;
    }

    [Test]
    public void TestReadFixture()
    {
        using var reader = new LibArchiveReader(Fixture);
        var entries = reader.Entries().Select(e => (e.Name, Data: e.ReadAllBytes())).ToList();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo("xartest.txt"));
        Assert.That(entries[0].Data, Is.EqualTo(Content));
    }

    [Test]
    public void TestWriteAndReadBack([Values] bool toStream)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xar-{Guid.NewGuid():N}.xar");
        using var output = new MemoryStream();

        LibArchiveWriter CreateWriter() => toStream
            ? new LibArchiveWriter(output, ArchiveFormat.Xar)
            : new LibArchiveWriter(path, ArchiveFormat.Xar);

        try
        {
            WriteOrIgnore(() =>
            {
                using var writer = CreateWriter();
                writer.AddEntry("entry.txt", Content);
            });

            using var reader = toStream ? new LibArchiveReader(new MemoryStream(output.ToArray())) : new LibArchiveReader(path);
            var entries = reader.Entries().Select(e => (e.Name, Data: e.ReadAllBytes())).ToList();

            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Name, Is.EqualTo("entry.txt"));
            Assert.That(entries[0].Data, Is.EqualTo(Content));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // An archive ends where its heap ends: at the end of the last item that its table of contents places there.
    // libarchive's XAR writer used to add the first bytes of the compressed table of contents after the heap,
    // as many as the checksum has (see native/libarchive-xar-heap-length.patch). The archive of a directory
    // alone has only the checksum in its heap. The large entry takes the heap past the writer's 64 KiB buffer.
    [Test]
    public void TestArchiveEndsWithItsHeap([Values] WriterKind kind, [Values] bool directoryOnly)
    {
        var archive = WriteArchiveOrIgnore(() => Write(kind, ArchiveFormat.Xar, compression: null, 0, writer =>
        {
            writer.AddDirectoryEntry("directory");
            if (!directoryOnly)
                AddEntries(writer, new[]
                {
                    ("directory/small.txt", Content),
                    ("large.bin", RandomBytes(200 * 1024)),
                    ("empty.txt", Array.Empty<byte>()),
                });
        }));

        // The default checksum of the table of contents is SHA-1 (1 in the header), the first 20 bytes of the heap
        var headerSize = (int)BigEndian(archive, 4, 2);
        var tocLength = (int)BigEndian(archive, 8, 8);
        Assert.That(BigEndian(archive, 24, 4), Is.EqualTo(1), "checksum algorithm");
        using var sha1 = SHA1.Create();
        Assert.That(archive.Skip(headerSize + tocLength).Take(20),
            Is.EqualTo(sha1.ComputeHash(archive, headerSize, tocLength)), "checksum");

        Assert.That(archive.LongLength, Is.EqualTo(XarHeapEnd(archive)), "archive length");
    }

    private static long BigEndian(byte[] d, int offset, int size) =>
        d.Skip(offset).Take(size).Aggregate(0L, (value, b) => value << 8 | b);

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(1).NextBytes(bytes);
        return bytes;
    }
}
