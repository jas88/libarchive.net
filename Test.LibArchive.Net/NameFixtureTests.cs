using System.Runtime.InteropServices;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// Entry names outside ASCII in archives written by other tools. Each nametest-* fixture except the
// legacy ones holds three files whose names are NFC, created on macOS (APFS keeps a name's form)
// in an en_US.UTF-8 locale with COPYFILE_DISABLE=1, each holding a line of ASCII text:
//
//     printf 'latin\n' > Größe.txt; printf 'cjk\n' > 日本語.txt; printf 'emoji\n' > 😀.txt
//     touch -t 202601010000.00 Größe.txt 日本語.txt 😀.txt
//     owner="--uid 0 --gid 0 --uname root --gname root --numeric-owner --no-xattrs --no-acls --no-mac-metadata --no-fflags"
//
// and then, with the three names in that order (FILES):
//
// nametest-infozip.zip      /usr/bin/zip -q -X -D nametest-infozip.zip FILES
//                           (macOS Zip 3.0 by Info-ZIP: UTF-8 names without the UTF-8 flag)
// nametest-python.zip       Python 3.15 zipfile (python3 -I), ZIP_DEFLATED, each file added with
//                           writestr(info, content), info = ZipInfo(name, date_time=(2026, 1, 1,
//                           0, 0, 0)) and info.external_attr = 0o100644 << 16: UTF-8 names with
//                           the UTF-8 flag
// nametest-bsdtar-pax.tar   bsdtar 3.8.11 (Homebrew) --format=pax -b 1 $owner -cf ... FILES
//                           (pax path records in UTF-8)
// nametest-macos-ustar.tar  /usr/bin/tar (macOS bsdtar 3.5.3) --format=ustar -b 1 $owner -cf ... FILES
//                           (raw UTF-8 in the ustar name fields)
// nametest-bsdtar-newc.cpio bsdtar 3.8.11 --format=newc -b 1 $owner -cf ... FILES (raw UTF-8)
// nametest-gnutar-gnu.tar   GNU tar 1.35 (Homebrew gnu-tar) --format=gnu -b 1 --owner=0
//                           --group=0 --numeric-owner -cf ... FILES (raw UTF-8)
// nametest-gnutar-posix.tar GNU tar 1.35 --format=posix -b 1 --owner=0 --group=0 --numeric-owner
//                           --pax-option=delete=atime,delete=ctime,exthdr.name=%d/PaxHeaders/%f
//                           -cf ... FILES (pax path records in UTF-8)
// nametest-p7zip.7z         7z a -bd -bso0 -t7z -mx=0 -mhc=off nametest-p7zip.7z FILES
//                           (p7zip 17.05: UTF-16LE names, in a header left uncompressed)
// nametest-hdiutil.iso.gz   hdiutil makehybrid -quiet -iso -joliet -default-volume-name NAMES -o
//                           nametest-hdiutil.iso DIR (DIR holding the three files), then
//                           gzip -9n (GNU gzip 1.15): Joliet names in UTF-16BE, and no Rock Ridge
//
// The legacy fixtures hold Größe.txt alone, its name in a code page that the format cannot declare:
//
// nametest-legacy-cp437.zip  bsdtar 3.8.11 --format=zip --options zip:hdrcharset=CP437 $owner
//                            -cf ... Größe.txt (the name is 47 72 94 E1 65 2E 74 78 74, no UTF-8 flag)
// nametest-legacy-latin1.tar bsdtar 3.8.11 --format=ustar --options hdrcharset=ISO-8859-1 -b 1
//                            $owner -cf ... Größe.txt (the name is 47 72 F6 DF 65 2E 74 78 74)
//
// nametest-bad-unicode-path.zip holds a.txt ("bad unicode path\n"), written by Python 3.15 zipfile,
// ZIP_STORED, with info = ZipInfo("a.txt", date_time=(2026, 1, 1, 0, 0, 0)),
// info.external_attr = 0o100644 << 16 and info.extra = an Info-ZIP Unicode Path field: header ID
// 0x7075, size 10, version 1, the CRC-32 of "a.txt", then FF 2E 74 78 74, which is not UTF-8.
[TestFixture]
public class NameFixtureTests
{
    private static readonly (string Name, string Text)[] Expected =
    {
        ("Größe.txt", "latin\n"), ("日本語.txt", "cjk\n"), ("\U0001F600.txt", "emoji\n"),
    };

    [TestCase("nametest-infozip.zip")]
    [TestCase("nametest-python.zip")]
    [TestCase("nametest-bsdtar-pax.tar")]
    [TestCase("nametest-macos-ustar.tar")]
    [TestCase("nametest-bsdtar-newc.cpio")]
    [TestCase("nametest-gnutar-gnu.tar")]
    [TestCase("nametest-gnutar-posix.tar")]
    [TestCase("nametest-p7zip.7z")]
    [TestCase("nametest-hdiutil.iso.gz")]
    public void ReadsNamesWrittenByOtherTools(string fixture)
    {
        using var reader = new LibArchiveReader(fixture);
        var files = reader.Entries()
            .Where(e => e.IsRegularFile)
            .Select(e => (e.Name, Text: e.ReadAllText()))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(files, Is.EqualTo(Expected.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray()));
        Assert.That(files.Select(f => f.Name.IsNormalized(NormalizationForm.FormC)), Has.All.True, "composed");
    }

    // A name that is not valid UTF-8 and has no declared character set still gives its entry, under
    // the name decoded as UTF-8 with U+FFFD for each invalid sequence; on Windows, where libarchive
    // gives such a name in the same form as a decoded one, as Latin-1 (see Entry.Name).
    [TestCase("nametest-legacy-cp437.zip", new byte[] { 0x47, 0x72, 0x94, 0xE1, 0x65, 0x2E, 0x74, 0x78, 0x74 })]
    [TestCase("nametest-legacy-latin1.tar", new byte[] { 0x47, 0x72, 0xF6, 0xDF, 0x65, 0x2E, 0x74, 0x78, 0x74 })]
    public void LegacyCodePageNameKeepsItsEntry(string fixture, byte[] storedName)
    {
        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new string(storedName.Select(b => (char)b).ToArray())
            : "Gr��e.txt";

        using var reader = new LibArchiveReader(fixture);
        var files = reader.Entries().Select(e => (e.Name, Text: e.ReadAllText())).ToArray();

        Assert.That(files, Is.EqualTo(new[] { (expected, "latin\n") }));
    }

    // A Unicode Path field whose CRC-32 matches the name replaces it, and libarchive drops the name
    // altogether when that field's bytes are not UTF-8 (archive_string.c:4334-4347), though it
    // reads the header without a warning (archive_read_support_format_zip.c:1148-1156). The entry
    // is still returned, with an empty name, rather than left out.
    [Test]
    public void EntryWhoseNameLibarchiveDropsIsStillReturned()
    {
        using var reader = new LibArchiveReader("nametest-bad-unicode-path.zip");
        var files = reader.Entries().Select(e => (e.Name, Text: e.ReadAllText())).ToArray();

        Assert.That(files, Is.EqualTo(new[] { (string.Empty, "bad unicode path\n") }));
    }

    // Names written by other tools, read through a stream rather than from a file
    [Test]
    public void ReadsNamesFromAStream([Values("nametest-python.zip", "nametest-gnutar-posix.tar", "nametest-p7zip.7z")] string fixture)
    {
        using var reader = new LibArchiveReader(new MemoryStream(File.ReadAllBytes(fixture)));
        var names = reader.Entries().Where(e => e.IsRegularFile).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.That(names, Is.EqualTo(Expected.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)));
    }
}
