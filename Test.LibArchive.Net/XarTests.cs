using System.Runtime.InteropServices;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// XAR needs libarchive built with an XML library. The Linux native libraries are built
// without one, so there writing XAR throws and reading it reports an unrecognized format;
// the Windows and macOS libraries can do both (see ArchiveFormat.Xar).
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

    private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private const string CannotWrite = "Xar not supported on this platform";

    /// <summary>
    /// Runs <paramref name="write"/>, which creates a XAR writer and writes with it, and returns true, or
    /// returns false where XAR cannot be written: on Linux, after asserting that creating the writer throws,
    /// and on a macOS libarchive without libxml2, by ignoring the test. Every test that writes XAR comes
    /// through here, so this is the one place that knows where XAR can be written.
    /// </summary>
    internal static bool TryWrite(Action write)
    {
        if (IsLinux)
        {
            var ex = Assert.Throws<ApplicationException>(() => write());
            Assert.That(ex!.Message, Is.EqualTo(CannotWrite));
            return false;
        }

        try
        {
            write();
            return true;
        }
        catch (ApplicationException ex) when (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && ex.Message == CannotWrite)
        {
            // The macOS libraries this project builds include libxml2; Homebrew's libarchive,
            // which can stand in for them locally, has only expat, which reads XAR but cannot write it.
            Assert.Ignore("This macOS libarchive was built without libxml2, so it cannot write XAR");
            return false;
        }
    }

    /// <summary>Writes a XAR archive through <see cref="TryWrite"/>.</summary>
    /// <returns>The archive, or null where XAR cannot be written.</returns>
    internal static byte[]? TryWriteArchive(Func<byte[]> write)
    {
        byte[]? archive = null;
        return TryWrite(() => archive = write()) ? archive : null;
    }

    [Test]
    public void TestReadFixture()
    {
        if (IsLinux)
        {
            var ex = Assert.Throws<ApplicationException>(() => new LibArchiveReader(Fixture).Dispose());
            Assert.That(ex!.Message, Is.EqualTo("Unrecognized archive format"));
            return;
        }

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
            if (!TryWrite(() =>
                {
                    using var writer = CreateWriter();
                    writer.AddEntry("entry.txt", Content);
                }))
                return;

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
