using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

/// <summary>The ways of creating a writer.</summary>
public enum WriterKind
{
    File,
    Stream,
    MemoryWriter,
    MemoryWriterWithBlockSize,
}

/// <summary>
/// Writes archives through each kind of writer, and reads what ZIP, 7-Zip and XAR archives record about how
/// their entries are compressed.
/// </summary>
internal static class ArchiveLayout
{
    internal static readonly DateTime MTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Writes an archive with a writer of the given kind. With no <paramref name="compression"/>, the writer is
    /// created without compression arguments, as a default call is.
    /// </summary>
    internal static byte[] Write(WriterKind kind, ArchiveFormat format, CompressionType? compression, int compressionLevel,
        Action<LibArchiveWriter> addEntries, string? password = null, EncryptionType encryption = EncryptionType.Default)
    {
        switch (kind)
        {
            case WriterKind.File:
                var path = Path.Combine(Path.GetTempPath(), $"compression-{Guid.NewGuid():N}.bin");
                try
                {
                    using (var writer = compression is { } c
                               ? new LibArchiveWriter(path, format, c, compressionLevel, password: password, encryption: encryption)
                               : new LibArchiveWriter(path, format, password: password, encryption: encryption))
                        addEntries(writer);
                    return File.ReadAllBytes(path);
                }
                finally
                {
                    File.Delete(path);
                }
            case WriterKind.Stream:
                using (var output = new MemoryStream())
                {
                    using (var writer = compression is { } c
                               ? new LibArchiveWriter(output, format, c, compressionLevel, password, encryption)
                               : new LibArchiveWriter(output, format, password: password, encryption: encryption))
                        addEntries(writer);
                    return output.ToArray();
                }
            case WriterKind.MemoryWriter:
                return WriteToMemory(compression is { } c1
                    ? LibArchiveWriter.CreateMemoryWriter(format, c1, compressionLevel, password, encryption)
                    : LibArchiveWriter.CreateMemoryWriter(format, password: password, encryption: encryption), addEntries);
            case WriterKind.MemoryWriterWithBlockSize:
                return WriteToMemory(compression is { } c2
                    ? LibArchiveWriter.CreateMemoryWriter(4096, format, c2, compressionLevel, password, encryption)
                    : LibArchiveWriter.CreateMemoryWriter(4096, format, password: password, encryption: encryption), addEntries);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    internal static byte[] Write(ArchiveFormat format, CompressionType compression, int compressionLevel, params (string Name, byte[] Data)[] entries) =>
        Write(WriterKind.MemoryWriter, format, compression, compressionLevel, writer => AddEntries(writer, entries));

    private static byte[] WriteToMemory(LibArchiveWriter writer, Action<LibArchiveWriter> addEntries)
    {
        using (writer)
            addEntries(writer);
        return writer.ToArray();
    }

    internal static void AddEntries(LibArchiveWriter writer, IEnumerable<(string Name, byte[] Data)> entries)
    {
        foreach (var (name, data) in entries)
            writer.AddEntry(name, data, MTime);
    }

    /// <summary>The regular files of an archive, by name, with their contents.</summary>
    internal static List<(string Name, byte[] Data)> ReadFiles(byte[] archive)
    {
        using var reader = new LibArchiveReader(new MemoryStream(archive));
        return reader.Entries().Where(e => e.IsRegularFile).Select(e => (e.Name, e.ReadAllBytes())).ToList();
    }

    /// <summary>Asserts that an archive's regular files are the given ones, in any order.</summary>
    internal static void AssertFiles(byte[] archive, params (string Name, byte[] Data)[] expected)
    {
        var files = ReadFiles(archive).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var sorted = expected.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Assert.That(files.Select(f => f.Name), Is.EqualTo(sorted.Select(e => e.Name)), "the files read back");
        for (var i = 0; i < sorted.Count; i++)
            Assert.That(files[i].Data, Is.EqualTo(sorted[i].Data), $"the content of {sorted[i].Name}");
    }

    /// <summary>
    /// Text of words drawn from a small vocabulary: it compresses well, but not so easily that every level
    /// gives the same size.
    /// </summary>
    internal static byte[] CompressibleText(int length, int seed = 1)
    {
        var words = new[]
        {
            "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
            "lambda", "mu", "nu", "xi", "omicron", "pi", "rho", "sigma", "tau", "upsilon",
        };
        var random = new Random(seed);
        var text = new StringBuilder(length + 16);
        while (text.Length < length)
            text.Append(words[random.Next(words.Length)]).Append(' ');
        return Encoding.ASCII.GetBytes(text.ToString(0, length));
    }

    /// <summary>
    /// The base-2 logarithm of the dictionary size that each LZMA preset, 0 to 9, sets (liblzma's
    /// lzma_lzma_preset): LZMA records it in its properties, so they show the preset, that is, the level.
    /// </summary>
    internal static readonly int[] LzmaPresetDictionaryLog = { 18, 20, 21, 22, 22, 23, 23, 24, 25, 26 };

    /// <summary>LZMA2's one-byte dictionary size n for a power of two: 2^(12 + n/2) for an even n.</summary>
    internal static byte Lzma2DictionarySize(int log) => (byte)((log - 12) * 2);

    /// <summary>
    /// On Linux, where XAR cannot be written (see <see cref="XarTests"/>), asserts that creating the writer
    /// throws, and returns true.
    /// </summary>
    internal static bool XarIsUnavailable(Func<byte[]> write)
    {
        if (!IsLinux)
            return false;
        var ex = Assert.Throws<ApplicationException>(() => write());
        Assert.That(ex!.Message, Is.EqualTo("Xar not supported on this platform"));
        return true;
    }

    #region ZIP

    internal const ushort ZipAesMethod = 99;

    /// <summary>
    /// What the central directory header and the local file header of a ZIP entry record about its compression.
    /// </summary>
    internal sealed class ZipEntry
    {
        public ZipEntry(string name, ushort method, ushort localMethod, ushort flags, ushort localFlags,
            (byte Strength, ushort Method)? aes, (byte Strength, ushort Method)? localAes, long compressedSize, long size)
        {
            Name = name;
            Method = method;
            LocalMethod = localMethod;
            Flags = flags;
            LocalFlags = localFlags;
            Aes = aes;
            LocalAes = localAes;
            CompressedSize = compressedSize;
            Size = size;
        }

        public string Name { get; }
        public ushort Method { get; }
        public ushort LocalMethod { get; }
        public ushort Flags { get; }
        public ushort LocalFlags { get; }

        // The WinZip AES extra field: the key strength and the actual compression method
        public (byte Strength, ushort Method)? Aes { get; }
        public (byte Strength, ushort Method)? LocalAes { get; }
        public long CompressedSize { get; }
        public long Size { get; }
    }

    /// <summary>
    /// Every entry in a ZIP archive's central directory, with what its local file header records. The
    /// archives here are small enough to need no Zip64 fields.
    /// </summary>
    internal static List<ZipEntry> ZipEntries(byte[] zip)
    {
        var end = LastIndexOf(zip, new byte[] { 0x50, 0x4b, 0x05, 0x06 });
        Assert.That(end, Is.GreaterThan(0), "end of central directory record");
        var count = ReadUInt16(zip, end + 10);
        var offset = (int)ReadUInt32(zip, end + 16);

        var entries = new List<ZipEntry>();
        for (var i = 0; i < count; i++)
        {
            Assert.That(ReadUInt32(zip, offset), Is.EqualTo(0x02014b50), "central directory header signature");
            var nameLength = ReadUInt16(zip, offset + 28);
            var extraLength = ReadUInt16(zip, offset + 30);
            var commentLength = ReadUInt16(zip, offset + 32);
            var local = (int)ReadUInt32(zip, offset + 42);

            Assert.That(ReadUInt32(zip, local), Is.EqualTo(0x04034b50), "local file header signature");
            var localNameLength = ReadUInt16(zip, local + 26);
            var localExtraLength = ReadUInt16(zip, local + 28);

            entries.Add(new ZipEntry(
                Encoding.UTF8.GetString(zip, offset + 46, nameLength),
                method: ReadUInt16(zip, offset + 10),
                localMethod: ReadUInt16(zip, local + 8),
                flags: ReadUInt16(zip, offset + 8),
                localFlags: ReadUInt16(zip, local + 6),
                aes: AesExtraField(zip, offset + 46 + nameLength, extraLength),
                localAes: AesExtraField(zip, local + 30 + localNameLength, localExtraLength),
                compressedSize: ReadUInt32(zip, offset + 20),
                size: ReadUInt32(zip, offset + 24)));
            offset += 46 + nameLength + extraLength + commentLength;
        }
        return entries;
    }

    // The WinZip AES extra field (ID 0x9901, 7 bytes): vendor version, vendor ID "AE", key strength
    // (1, 2 or 3 for AES-128, AES-192 or AES-256) and the actual compression method
    private static (byte Strength, ushort Method)? AesExtraField(byte[] zip, int start, int length)
    {
        for (var i = start; i + 4 <= start + length; i += 4 + ReadUInt16(zip, i + 2))
        {
            if (ReadUInt16(zip, i) != 0x9901)
                continue;
            Assert.That(ReadUInt16(zip, i + 2), Is.EqualTo(7), "AES extra field size");
            Assert.That(Encoding.ASCII.GetString(zip, i + 6, 2), Is.EqualTo("AE"), "AES vendor ID");
            return (zip[i + 8], ReadUInt16(zip, i + 9));
        }
        return null;
    }

    #endregion

    #region 7-Zip

    internal static readonly byte[] SevenZipSignature = { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c };

    /// <summary>The coder of a folder of a 7-Zip archive, and the size of the folder's packed stream.</summary>
    internal sealed class SevenZipCoder
    {
        public SevenZipCoder(byte[] id, byte[] properties, long packSize)
        {
            Id = id;
            Properties = properties;
            PackSize = packSize;
        }

        public byte[] Id { get; }
        public byte[] Properties { get; }
        public long PackSize { get; }
    }

    /// <summary>
    /// Reads the coder that unpacks each folder of a 7-Zip archive's data from its header, which must not be
    /// encoded, with the size of the folder's packed stream.
    /// </summary>
    /// <remarks>
    /// After the 32-byte signature header, the header is a property ID (kHeader, 1) followed by the main
    /// streams info (kMainStreamsInfo, 4): the pack info (kPackInfo, 6) gives the position, number and sizes of
    /// the packed streams, and the unpack info (kUnPackInfo, 7) lists the folders (kFolder, 11), each the
    /// coders that unpack its data, by method ID. Numbers are 7-Zip's variable-length UINT64s. Each folder
    /// libarchive writes has one coder, which unpacks one packed stream.
    /// </remarks>
    internal static List<SevenZipCoder> SevenZipCoders(byte[] archive)
    {
        Assert.That(archive.Take(6), Is.EqualTo(SevenZipSignature), "7-Zip signature");
        var header = new SevenZipHeader(archive, 32 + (int)ReadUInt64(archive, 12));

        header.Expect(0x01, "kHeader (an encoded header is kEncodedHeader, 0x17)");
        header.Expect(0x04, "kMainStreamsInfo");
        header.Expect(0x06, "kPackInfo");
        Assert.That(header.ReadNumber(), Is.Zero, "pack position");
        var packStreams = (int)header.ReadNumber();
        var packSizes = new List<long>();
        for (var property = header.ReadNumber(); property != 0; property = header.ReadNumber())
        {
            Assert.That(property, Is.EqualTo(0x09), "kSize");
            for (var i = 0; i < packStreams; i++)
                packSizes.Add((long)header.ReadNumber());
        }

        header.Expect(0x07, "kUnPackInfo");
        header.Expect(0x0b, "kFolder");
        var folders = (int)header.ReadNumber();
        Assert.That(folders, Is.EqualTo(packStreams), "one packed stream for each folder");
        Assert.That(packSizes, Has.Count.EqualTo(packStreams), "the size of each packed stream");
        header.Expect(0x00, "folders that are not external");

        var coders = new List<SevenZipCoder>();
        for (var i = 0; i < folders; i++)
        {
            Assert.That(header.ReadNumber(), Is.EqualTo(1), "number of coders");
            var flags = header.ReadByte();
            Assert.That(flags & 0x10, Is.Zero, "a simple coder, with one stream in and one out");
            var id = header.ReadBytes(flags & 0x0f);
            var properties = (flags & 0x20) != 0 ? header.ReadBytes((int)header.ReadNumber()) : Array.Empty<byte>();
            coders.Add(new SevenZipCoder(id, properties, packSizes[i]));
        }
        return coders;
    }

    /// <summary>The coder of a 7-Zip archive with one folder.</summary>
    internal static SevenZipCoder SevenZipFirstCoder(byte[] archive)
    {
        var coders = SevenZipCoders(archive);
        Assert.That(coders, Has.Count.EqualTo(1), "number of folders");
        return coders[0];
    }

    /// <summary>
    /// The packed stream of each folder of a 7-Zip archive, in order: they follow the signature header.
    /// </summary>
    internal static List<byte[]> SevenZipPackedStreams(byte[] archive)
    {
        var offset = 32;
        var streams = new List<byte[]>();
        foreach (var coder in SevenZipCoders(archive))
        {
            streams.Add(archive.Skip(offset).Take((int)coder.PackSize).ToArray());
            offset += (int)coder.PackSize;
        }
        return streams;
    }

    private sealed class SevenZipHeader
    {
        private readonly byte[] data;
        private int position;

        public SevenZipHeader(byte[] data, int position)
        {
            this.data = data;
            this.position = position;
        }

        public byte ReadByte() => data[position++];

        public byte[] ReadBytes(int count)
        {
            var bytes = data.Skip(position).Take(count).ToArray();
            position += count;
            return bytes;
        }

        public void Expect(byte id, string what) => Assert.That(ReadByte(), Is.EqualTo(id), what);

        // The first byte's leading 1 bits count the bytes that follow, which hold the low-order bytes of the
        // number, little-endian; the first byte's remaining bits are its high-order bits
        public ulong ReadNumber()
        {
            var first = ReadByte();
            ulong value = 0;
            var mask = 0x80;
            for (var i = 0; i < 8; i++, mask >>= 1)
            {
                if ((first & mask) == 0)
                    return value | ((ulong)(first & (mask - 1)) << (8 * i));
                value |= (ulong)ReadByte() << (8 * i);
            }
            return value;
        }
    }

    #endregion

    #region XAR

    /// <summary>A file in a XAR archive: its name, the encoding its table of contents records, and its data.</summary>
    internal sealed class XarFile
    {
        public XarFile(string name, string encoding, byte[] data)
        {
            Name = name;
            Encoding = encoding;
            Data = data;
        }

        public string Name { get; }
        public string Encoding { get; }
        public byte[] Data { get; }
    }

    /// <summary>
    /// Every file with data in a XAR archive. The table of contents gives each file's encoding and the offset
    /// and length of its data in the heap, which follows the compressed table of contents.
    /// </summary>
    internal static List<XarFile> XarFiles(byte[] xar)
    {
        var toc = XDocument.Parse(NameEncodingTests.XarTableOfContents(xar));
        var heap = XarHeapOffset(xar);
        return toc.Descendants("file")
            .Where(f => f.Element("data") != null)
            .Select(f =>
            {
                var data = f.Element("data")!;
                var offset = (int)(long)data.Element("offset")!;
                var length = (int)(long)data.Element("length")!;
                return new XarFile(f.Element("name")!.Value, (string)data.Element("encoding")!.Attribute("style")!,
                    xar.Skip(heap + offset).Take(length).ToArray());
            })
            .ToList();
    }

    /// <summary>
    /// The table of contents without the time it records the archive was written at
    /// (archive_write_set_format_xar.c:1660), the one part of an archive that depends on when it is written.
    /// </summary>
    internal static string XarTableOfContentsWithoutCreationTime(byte[] xar) =>
        Regex.Replace(NameEncodingTests.XarTableOfContents(xar), "<creation-time>[^<]*</creation-time>", "<creation-time/>");

    /// <summary>
    /// The heap after the checksum of the table of contents, which starts it: the data of the files.
    /// </summary>
    internal static byte[] XarFileData(byte[] xar)
    {
        var checksum = XDocument.Parse(NameEncodingTests.XarTableOfContents(xar)).Root!.Element("toc")!.Element("checksum")!;
        Assert.That((long)checksum.Element("offset")!, Is.Zero, "checksum offset");
        return xar.Skip(XarHeapOffset(xar) + (int)(long)checksum.Element("size")!).ToArray();
    }

    // The heap follows the header and the compressed table of contents, whose size the header gives
    private static int XarHeapOffset(byte[] xar) => ReadUInt16BigEndian(xar, 4) + (int)ReadUInt64BigEndian(xar, 8);

    #endregion

    #region Bytes

    internal static ushort ReadUInt16(byte[] d, int i) => (ushort)(d[i] | d[i + 1] << 8);

    internal static uint ReadUInt32(byte[] d, int i) => (uint)(d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24);

    internal static ulong ReadUInt64(byte[] d, int i) => ReadUInt32(d, i) | (ulong)ReadUInt32(d, i + 4) << 32;

    private static int ReadUInt16BigEndian(byte[] d, int i) => d[i] << 8 | d[i + 1];

    private static ulong ReadUInt64BigEndian(byte[] d, int i)
    {
        ulong value = 0;
        for (var k = 0; k < 8; k++)
            value = value << 8 | d[i + k];
        return value;
    }

    internal static bool StartsWith(byte[] data, params byte[] prefix) =>
        data.Length >= prefix.Length && data.Take(prefix.Length).SequenceEqual(prefix);

    internal static bool Contains(byte[] haystack, byte[] needle) => NameEncodingTests.IndexOf(haystack, needle) >= 0;

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                return i;
        }
        return -1;
    }

    #endregion
}
