using System.Runtime.InteropServices;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

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
}
