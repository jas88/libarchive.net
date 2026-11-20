using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// What the reader makes of each result of libarchive's archive_read_next_header. The archives are
// built here, byte by byte, so that a header can hold what no tar writer would write.
[TestFixture]
public class HeaderResultTests
{
    private static readonly byte[] FirstData = Encoding.ASCII.GetBytes("first\n");

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
        Assert.That(ex!.Message, Is.EqualTo("libarchive returned ARCHIVE_FATAL without a message"));
    }

    // POSIX ustar headers and pax extended headers
    internal static class Tar
    {
        // Two zero blocks end an archive
        public static readonly byte[] End = new byte[1024];

        public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        // A regular file and its data
        public static byte[] Entry(string name, byte[] data) => Concat(Header(name, data.Length), Pad(data));

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
