using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// What the reader makes of each result of libarchive's archive_read_next_header. An entry that
// libarchive reads with ARCHIVE_WARN is returned with its warning, and the entries after it
// follow; any other result but ARCHIVE_OK and ARCHIVE_EOF throws. The tar archives are built here,
// byte by byte, so that a header can hold what no tar writer would write, and the ISO 9660 image
// is written by LibArchiveWriter and then patched.
[TestFixture]
public class HeaderResultTests
{
    private const string First = "first\n";
    private const string Second = "second\n";
    private static readonly byte[] FirstData = Encoding.ASCII.GetBytes(First);
    private static readonly byte[] SecondData = Encoding.ASCII.GetBytes(Second);

    // A pax "path" record longer than 1 MiB is ignored, and the tar reader returns ARCHIVE_WARN
    // without setting a message (archive_read_support_format_tar.c:2972-2980), after
    // archive_read_next_header has cleared the last one (archive_read.c:664). The entry keeps the
    // name in its ustar header.
    [Test]
    public void OversizedPaxPathGivesAWarningWithoutAMessage()
    {
        var path = Encoding.ASCII.GetBytes(new string('p', (1 << 20) + 1));
        var archive = Tar.Concat(
            Tar.PaxEntry(NameEncodingTests.PaxRecord("path", path), "first.txt", FirstData),
            Tar.Entry("second.txt", SecondData),
            Tar.End);

        AssertEntries(archive,
            ("first.txt", "libarchive returned ARCHIVE_WARN without a message", First),
            ("second.txt", null, Second));
    }

    // A pax "path" record that is not valid UTF-8 cannot be converted to libarchive's current
    // character set, which is UTF-8 on every platform (native/libarchive-narrow-utf8.patch), so
    // the tar reader warns and keeps the bytes as they are (archive_read_support_format_tar.c:
    // 2213-2222 and 1199-1210). The name is then read as UTF-8, with U+FFFD for the invalid byte,
    // as for a name in a format that declares no character set.
    [Test]
    public void PaxPathThatIsNotUtf8GivesAWarning()
    {
        var path = new byte[] { (byte)'b', (byte)'a', (byte)'d', (byte)'-', 0xFF, (byte)'.', (byte)'t', (byte)'x', (byte)'t' };
        var archive = Tar.Concat(
            Tar.PaxEntry(NameEncodingTests.PaxRecord("path", path), "first.txt", FirstData),
            Tar.Entry("second.txt", SecondData),
            Tar.End);

        AssertEntries(archive,
            ("bad-\uFFFD.txt", "Pathname can't be converted from UTF-8 to current locale", First),
            ("second.txt", null, Second));
    }

    // A pax "mtime" record that is not a time is ignored, with a warning
    // (archive_read_support_format_tar.c:2959-2970 and 2416-2419).
    [Test]
    public void MalformedPaxMtimeGivesAWarning()
    {
        var archive = Tar.Concat(
            Tar.PaxEntry(NameEncodingTests.PaxRecord("mtime", Encoding.ASCII.GetBytes("abc")), "first.txt", FirstData),
            Tar.Entry("second.txt", SecondData),
            Tar.End);

        AssertEntries(archive,
            ("first.txt", "Ignoring malformed pax mtime", First),
            ("second.txt", null, Second));
    }

    // An ISO 9660 image whose Joliet name for first.txt starts with a lone high surrogate, which is
    // not valid UTF-16. The writer records both Rock Ridge and Joliet names, and the reader prefers
    // Rock Ridge (archive_read_support_format_iso9660.c:1183-1189), so the image is first patched
    // so that the reader finds no Rock Ridge: its SUSP "SP" entry, which must open the root
    // directory's system use area (archive_read_support_format_iso9660.c:1982-2033), is spoilt.
    // ISO 9660 has no checksum to fix up after that.
    //
    // libarchive normalizes a name that declares its character set, here UTF-16BE, to NFC as it
    // converts it (archive_string.c:1276-1284; NFC on macOS too with
    // native/libarchive-narrow-utf8.patch), and that replaces the lone surrogate with U+FFFD and
    // fails (archive_string.c:3100-3106). The ISO 9660 reader then warns, and the entry has no name
    // (archive_read_support_format_iso9660.c:1293-1310). This is the same on Windows, where the
    // conversion is made to UTF-16 instead of UTF-8, but through the same normalization
    // (archive_string.c:512-550), and the failure leaves no form of the name there either
    // (archive_string.c:4334-4341).
    [Test]
    public void JolietNameThatIsNotUtf16GivesAWarning()
    {
        var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Iso9660);
        using (writer)
        {
            writer.AddEntry("first.txt", FirstData);
            writer.AddEntry("second.txt", SecondData);
        }
        var image = writer.ToArray();

        var sp = new byte[] { (byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF };
        var spAt = NameEncodingTests.IndexOf(image, sp);
        Assert.That(spAt, Is.GreaterThan(0), "SUSP SP entry");
        image[spAt] = (byte)'X';

        // Read from the Joliet names, with Rock Ridge out of the way, the image is as written
        AssertEntries(image, (".", null, ""), ("first.txt", null, First), ("second.txt", null, Second));

        var joliet = Encoding.BigEndianUnicode.GetBytes("first.txt");
        var nameAt = NameEncodingTests.IndexOf(image, joliet);
        Assert.That(nameAt, Is.GreaterThan(0), "Joliet name");
        Assert.That(NameEncodingTests.IndexOf(image.Skip(nameAt + 1).ToArray(), joliet), Is.EqualTo(-1), "one Joliet name");
        image[nameAt] = 0xD8;
        image[nameAt + 1] = 0x00;

        AssertEntries(image,
            (".", null, ""),
            ("", "Pathname cannot be converted from UTF-16BE to current locale", First),
            ("second.txt", null, Second));
    }

    // ARCHIVE_RETRY reads no entry, and throws: the tar reader returns it for a header whose
    // checksum is wrong, having skipped the header's block (archive_read_support_format_tar.c:
    // 808-822). bsdtar would ask for the next header again, and read on a block at a time; the
    // reader does not, so the damaged entry is reported rather than lost without a word.
    [Test]
    public void DamagedHeaderThrowsRatherThanRetrying([Values] bool firstEntry)
    {
        var damaged = Tar.Header("damaged.txt", 7);
        damaged[0] = (byte)'D';
        var archive = Tar.Concat(
            Tar.Entry("first.txt", FirstData),
            damaged,
            Tar.Pad(Encoding.ASCII.GetBytes("damage\n")),
            Tar.Entry("third.txt", Encoding.ASCII.GetBytes("third\n")),
            Tar.End);

        Assert.That(ThrowsAtSecondHeader(archive, firstEntry).Message,
            Is.EqualTo("Damaged tar archive (bad header checksum)"));
    }

    // A pax extended header whose size field, in base-256, is beyond libarchive's limit for an
    // entry fails with ARCHIVE_FATAL and no message (archive_read_support_format_tar.c:1991-1994),
    // so the exception names the result instead.
    [Test]
    public void ErrorWithoutAMessageNamesTheResult([Values] bool firstEntry)
    {
        var archive = Tar.Concat(
            Tar.Entry("first.txt", FirstData),
            Tar.Header("PaxHeader/second.txt", 1L << 61, 'x', base256Size: true),
            Tar.End);

        Assert.That(ThrowsAtSecondHeader(archive, firstEntry).Message,
            Is.EqualTo("libarchive returned ARCHIVE_FATAL without a message"));
    }

    // Each entry's name, warning and data, from Entries() and from FirstEntry() called until it
    // returns null, each with a reader of its own
    private static void AssertEntries(byte[] archive, params (string Name, string? Warning, string Text)[] expected)
    {
        using (var reader = new LibArchiveReader(new MemoryStream(archive)))
        {
            var entries = reader.Entries().Select(e => (e.Name, e.Warning, e.ReadAllText())).ToList();
            Assert.That(entries, Is.EqualTo(expected), "Entries()");
        }

        using (var reader = new LibArchiveReader(new MemoryStream(archive)))
        {
            var entries = new List<(string, string?, string)>();
            while (reader.FirstEntry() is { } entry)
                entries.Add((entry.Name, entry.Warning, entry.ReadAllText()));
            Assert.That(entries, Is.EqualTo(expected), "FirstEntry()");
        }
    }

    // Reads first.txt, then the next header, with FirstEntry() or Entries(), and returns what that
    // throws
    private static ApplicationException ThrowsAtSecondHeader(byte[] archive, bool firstEntry)
    {
        using var reader = new LibArchiveReader(new MemoryStream(archive));
        ApplicationException? ex;
        if (firstEntry)
        {
            Assert.That(reader.FirstEntry()!.Name, Is.EqualTo("first.txt"));
            ex = Assert.Throws<ApplicationException>(() => reader.FirstEntry());
        }
        else
        {
            using var entries = reader.Entries().GetEnumerator();
            Assert.That(entries.MoveNext(), Is.True);
            Assert.That(entries.Current.Name, Is.EqualTo("first.txt"));
            ex = Assert.Throws<ApplicationException>(() => entries.MoveNext());
        }
        return ex!;
    }

    // POSIX ustar headers and pax extended headers
    private static class Tar
    {
        // Two zero blocks end an archive
        public static readonly byte[] End = new byte[1024];

        public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        // A regular file and its data
        public static byte[] Entry(string name, byte[] data) => Concat(Header(name, data.Length), Pad(data));

        // A regular file and its data, after a pax extended header holding the given records
        public static byte[] PaxEntry(byte[] records, string name, byte[] data) =>
            Concat(Header("PaxHeader/" + name, records.Length, 'x'), Pad(records), Entry(name, data));

        // A ustar header, with its checksum. A base-256 size is the binary form that GNU tar
        // writes for sizes that do not fit the field in octal.
        public static byte[] Header(string name, long size, char type = '0', bool base256Size = false)
        {
            var h = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(h, 0);
            Octal(h, 100, 8, Convert.ToInt64("644", 8));
            Octal(h, 108, 8, 0);
            Octal(h, 116, 8, 0);
            if (base256Size)
            {
                h[124] = 0x80;
                for (var i = 0; i < 8; i++)
                    h[135 - i] = (byte)(size >> (8 * i));
            }
            else
            {
                Octal(h, 124, 12, size);
            }
            Octal(h, 136, 12, 1767225600); // 2026-01-01 00:00:00 UTC
            h[156] = (byte)type;
            Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(h, 257);

            // The checksum is the sum of the header's bytes, its own field counted as spaces
            for (var i = 148; i < 156; i++)
                h[i] = (byte)' ';
            var sum = h.Sum(b => (int)b);
            Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(h, 148);
            return h;
        }

        // A NUL-terminated octal number filling a field
        private static void Octal(byte[] header, int offset, int width, long value) =>
            Encoding.ASCII.GetBytes(Convert.ToString(value, 8).PadLeft(width - 1, '0') + "\0").CopyTo(header, offset);

        // Data padded with zeros to a whole number of 512-byte blocks
        public static byte[] Pad(byte[] data) => data.Concat(new byte[(512 - data.Length % 512) % 512]).ToArray();
    }
}
