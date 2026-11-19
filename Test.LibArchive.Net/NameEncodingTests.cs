using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

/// <summary>
/// Entry names outside ASCII in every format the writer supports: the bytes each format stores
/// for them, and the names read back. Nothing here depends on the process locale or on how a file
/// system normalizes names: archives are written to memory and compared byte for byte.
/// </summary>
[TestFixture]
public class NameEncodingTests
{
    // Latin-1 (composed, NFC), CJK, and a character outside the Basic Multilingual Plane
    // (a surrogate pair in UTF-16)
    public static readonly string[] Names = { "Größe.txt", "日本語.txt", "\U0001F600.txt" };

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // XAR cannot be written on Linux (see XarTests)
    public static IEnumerable<ArchiveFormat> WritableFormats()
    {
        foreach (var format in new[]
                 {
                     ArchiveFormat.Zip, ArchiveFormat.SevenZip, ArchiveFormat.Tar, ArchiveFormat.Ustar,
                     ArchiveFormat.Pax, ArchiveFormat.Cpio, ArchiveFormat.Iso9660,
                 })
            yield return format;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            yield return ArchiveFormat.Xar;
    }

    private static byte[] ContentOf(string name) => Encoding.UTF8.GetBytes("content of " + name);

    private static byte[] Write(ArchiveFormat format, params string[] names)
    {
        var writer = LibArchiveWriter.CreateMemoryWriter(format);
        using (writer)
        {
            foreach (var name in names)
                writer.AddEntry(name, ContentOf(name));
        }
        return writer.ToArray();
    }

    private static List<(string Name, byte[] Content)> ReadFiles(byte[] archive)
    {
        using var reader = new LibArchiveReader(new MemoryStream(archive));
        return reader.Entries()
            .Where(e => e.IsRegularFile)
            .Select(e => (e.Name, e.ReadAllBytes()))
            .ToList();
    }

    [Test]
    public void StoredNameIsTheFormatsEncoding(
        [ValueSource(nameof(WritableFormats))] ArchiveFormat format,
        [ValueSource(nameof(Names))] string name)
    {
        // One entry per archive: 7-Zip leaves the header holding the names uncompressed only then
        var archive = Write(format, name);
        var utf8 = Encoding.UTF8.GetBytes(name);

        switch (format)
        {
            case ArchiveFormat.Zip:
                AssertZipName(archive, utf8);
                break;
            case ArchiveFormat.SevenZip:
                // The names are UTF-16LE, each ending with a NUL character
                Assert.That(IndexOf(archive, Encoding.Unicode.GetBytes(name + "\0")), Is.GreaterThan(0));
                break;
            case ArchiveFormat.Tar:
            case ArchiveFormat.Pax:
                AssertPaxName(archive, utf8);
                break;
            case ArchiveFormat.Ustar:
                Assert.That(Encoding.ASCII.GetString(archive, 257, 5), Is.EqualTo("ustar"));
                Assert.That(Field(archive, 0, 100), Is.EqualTo(utf8), "ustar name field");
                break;
            case ArchiveFormat.Cpio:
                AssertCpioName(archive, utf8);
                break;
            case ArchiveFormat.Iso9660:
                AssertIsoNames(archive, name);
                break;
            case ArchiveFormat.Xar:
                AssertXarName(archive, name, utf8);
                break;
            default:
                Assert.Fail($"No assertion for {format}");
                break;
        }

        var files = ReadFiles(archive);
        Assert.That(files.Select(f => f.Name), Is.EqualTo(new[] { name }));
        Assert.That(files[0].Name.IsNormalized(NormalizationForm.FormC), Is.True);
        Assert.That(files[0].Content, Is.EqualTo(ContentOf(name)));
    }

    // All three names in one archive, read back from memory and from a file: each name is the one
    // written, composed (NFC), with its own content.
    [Test]
    public void NamesReadBackComposed([ValueSource(nameof(WritableFormats))] ArchiveFormat format)
    {
        var archive = Write(format, Names);
        var expected = Names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var files = ReadFiles(archive);
        Assert.That(files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(expected));
        Assert.That(files.Select(f => f.Name.IsNormalized(NormalizationForm.FormC)), Has.All.True, "composed");
        foreach (var (name, content) in files)
            Assert.That(content, Is.EqualTo(ContentOf(name)), name);

        var path = Path.Combine(Path.GetTempPath(), $"names-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, archive);
            using var reader = new LibArchiveReader(path);
            var names = reader.Entries().Where(e => e.IsRegularFile).Select(e => e.Name);
            Assert.That(names.OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(expected), "read from a file");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ZIP: the name is UTF-8 and general purpose bit 11 (the UTF-8 flag) is set, in the local file
    // header and in the central directory header.
    private static void AssertZipName(byte[] zip, byte[] utf8)
    {
        const ushort Utf8Flag = 1 << 11;

        Assert.That(ReadUInt32(zip, 0), Is.EqualTo(0x04034b50), "local file header signature");
        Assert.That(ReadUInt16(zip, 6) & Utf8Flag, Is.EqualTo(Utf8Flag), "UTF-8 flag in the local file header");
        Assert.That(Slice(zip, 30, ReadUInt16(zip, 26)), Is.EqualTo(utf8), "name in the local file header");

        var end = LastIndexOf(zip, new byte[] { 0x50, 0x4b, 0x05, 0x06 });
        Assert.That(end, Is.GreaterThan(0), "end of central directory record");
        var central = (int)ReadUInt32(zip, end + 16);
        Assert.That(ReadUInt32(zip, central), Is.EqualTo(0x02014b50), "central directory header signature");
        Assert.That(ReadUInt16(zip, central + 8) & Utf8Flag, Is.EqualTo(Utf8Flag), "UTF-8 flag in the central directory");
        Assert.That(Slice(zip, central + 46, ReadUInt16(zip, central + 28)), Is.EqualTo(utf8), "name in the central directory");
    }

    // pax: the name is a UTF-8 "path" record in the extended header that precedes the entry's
    // ustar header, whose name field holds the same UTF-8 (it fits); no record switches the
    // header to binary.
    private static void AssertPaxName(byte[] tar, byte[] utf8)
    {
        Assert.That((char)tar[156], Is.EqualTo('x'), "pax extended header");
        var size = Convert.ToInt32(Encoding.ASCII.GetString(tar, 124, 11), 8);
        var records = Slice(tar, 512, size);

        var record = PaxRecord("path", utf8);
        Assert.That(IndexOf(records, record), Is.GreaterThanOrEqualTo(0),
            $"record {Encoding.UTF8.GetString(record).TrimEnd()} in {Encoding.UTF8.GetString(records)}");
        Assert.That(Encoding.ASCII.GetString(records), Does.Not.Contain("hdrcharset"));

        var header = 512 + (size + 511) / 512 * 512;
        Assert.That((char)tar[header + 156], Is.EqualTo('0'), "the entry's own header");
        Assert.That(Field(tar, header, 100), Is.EqualTo(utf8), "ustar name field");
    }

    // A pax record is "<length> <key>=<value>\n", its length counting the digits of the length
    private static byte[] PaxRecord(string key, byte[] value)
    {
        static int Digits(int n) => n.ToString(CultureInfo.InvariantCulture).Length;

        var rest = 1 + key.Length + 1 + value.Length + 1;
        var length = rest + 1;
        while (Digits(length) + rest != length)
            length = Digits(length) + rest;
        var prefix = length.ToString(CultureInfo.InvariantCulture) + " " + key + "=";
        return Encoding.ASCII.GetBytes(prefix).Concat(value).Concat(new[] { (byte)'\n' }).ToArray();
    }

    // cpio (SVR4 "newc"): the name follows the 110-byte ASCII header, as UTF-8 ending with a NUL,
    // and its size field counts that NUL.
    private static void AssertCpioName(byte[] cpio, byte[] utf8)
    {
        Assert.That(Encoding.ASCII.GetString(cpio, 0, 6), Is.EqualTo("070701"));
        Assert.That(Convert.ToInt32(Encoding.ASCII.GetString(cpio, 94, 8), 16), Is.EqualTo(utf8.Length + 1), "name size");
        Assert.That(Slice(cpio, 110, utf8.Length + 1), Is.EqualTo(utf8.Concat(new byte[] { 0 }).ToArray()));
    }

    // ISO 9660: the Joliet directory holds the name in UTF-16BE, and the primary directory its Rock
    // Ridge NM entry: "NM", its length, version 1 and no flags, then the name. libarchive stores
    // that name in the C library's locale form: UTF-8 on macOS and Linux, and on Windows, whose
    // "C" locale maps U+0000-U+00FF to single bytes, Latin-1 for a name made only of such
    // characters (and UTF-8 for any other). Reading prefers Rock Ridge (see the read-back
    // assertions), and gets the name back on each platform.
    private static void AssertIsoNames(byte[] iso, string name)
    {
        Assert.That(IndexOf(iso, Encoding.BigEndianUnicode.GetBytes(name)), Is.GreaterThan(0), "Joliet name");

        var rockRidge = IsWindows && name.All(c => c <= 'ÿ')
            ? name.Select(c => (byte)c).ToArray()
            : Encoding.UTF8.GetBytes(name);
        var nm = new byte[] { (byte)'N', (byte)'M', (byte)(5 + rockRidge.Length), 1, 0 }.Concat(rockRidge).ToArray();
        Assert.That(IndexOf(iso, nm), Is.GreaterThan(0), "Rock Ridge NM entry");
    }

    // XAR: the table of contents (zlib-compressed XML after the 28-byte header) holds the name as
    // UTF-8 text when every character is in Latin-1, and otherwise as base64 of its UTF-8, which
    // is how libarchive's XAR writer stores names.
    private static void AssertXarName(byte[] xar, string name, byte[] utf8)
    {
        Assert.That(Encoding.ASCII.GetString(xar, 0, 4), Is.EqualTo("xar!"));
        var headerSize = ReadUInt16BigEndian(xar, 4);
        var compressed = (int)ReadUInt64BigEndian(xar, 8);

        // The raw deflate data between the 2-byte zlib header and the 4-byte Adler-32 checksum
        using var deflate = new DeflateStream(new MemoryStream(xar, headerSize + 2, compressed - 6), CompressionMode.Decompress);
        using var toc = new StreamReader(deflate, Encoding.UTF8);
        var xml = toc.ReadToEnd();

        var element = name.All(c => c <= 'ÿ')
            ? $"<name>{name}</name>"
            : $"<name enctype=\"base64\">{Convert.ToBase64String(utf8)}</name>";
        Assert.That(xml, Does.Contain(element));
    }

    // The bytes of a NUL-padded field
    private static byte[] Field(byte[] data, int offset, int size)
    {
        var length = 0;
        while (length < size && data[offset + length] != 0)
            length++;
        return Slice(data, offset, length);
    }

    private static byte[] Slice(byte[] data, int offset, int count) => data.Skip(offset).Take(count).ToArray();

    private static ushort ReadUInt16(byte[] d, int i) => (ushort)(d[i] | d[i + 1] << 8);

    private static uint ReadUInt32(byte[] d, int i) => (uint)(d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24);

    private static int ReadUInt16BigEndian(byte[] d, int i) => d[i] << 8 | d[i + 1];

    private static ulong ReadUInt64BigEndian(byte[] d, int i)
    {
        ulong value = 0;
        for (var k = 0; k < 8; k++)
            value = value << 8 | d[i + k];
        return value;
    }

    internal static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (Matches(haystack, i, needle))
                return i;
        return -1;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
            if (Matches(haystack, i, needle))
                return i;
        return -1;
    }

    private static bool Matches(byte[] haystack, int offset, byte[] needle)
    {
        for (var j = 0; j < needle.Length; j++)
            if (haystack[offset + j] != needle[j])
                return false;
        return true;
    }
}
