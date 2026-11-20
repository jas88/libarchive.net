using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static Test.LibArchive.Net.ArchiveLayout;

namespace Test.LibArchive.Net;

// ZIP compresses each entry itself: the compression type selects the method that each entry's local file
// header and central directory header record (APPNOTE 4.4.5: 0 store, 8 deflate, 12 bzip2, 14 lzma,
// 93 zstd, 95 xz), and no filter wraps the archive, which still starts with a local file header. With AES
// encryption both headers record method 99, and the AES extra field the method the entry is compressed with.
[TestFixture]
public class ZipCompressionTests
{
    private const string Password = "TestPassword123!";

    private static readonly byte[] LocalFileHeader = { 0x50, 0x4b, 0x03, 0x04 };

    public static readonly CompressionType[] Types =
    {
        CompressionType.None, CompressionType.Deflate, CompressionType.Bzip2, CompressionType.Lzma,
        CompressionType.Xz, CompressionType.Zstd,
    };

    private static ushort MethodOf(CompressionType compression) => compression switch
    {
        CompressionType.None or CompressionType.Deflate => 8,
        CompressionType.Bzip2 => 12,
        CompressionType.Lzma => 14,
        CompressionType.Zstd => 93,
        CompressionType.Xz => 95,
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
    };

    private static readonly (string Name, byte[] Data)[] Entries =
    {
        ("first.txt", CompressibleText(16 * 1024, seed: 1)),
        ("second.txt", CompressibleText(8 * 1024, seed: 2)),
    };

    private static readonly byte[] Text = CompressibleText(128 * 1024);

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(EncryptionAssert.SecretText);

    private string testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), $"zip-compression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void Teardown() => Directory.Delete(testDirectory, true);

    private string Save(byte[] archive)
    {
        var path = Path.Combine(testDirectory, $"{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, archive);
        return path;
    }

    private static void AssertMethods(byte[] zip, ushort method)
    {
        Assert.That(StartsWith(zip, LocalFileHeader), Is.True, "the archive starts with a local file header: no filter wraps it");
        var entries = ZipEntries(zip);
        Assert.That(entries, Is.Not.Empty);
        foreach (var entry in entries)
        {
            Assert.That(entry.Method, Is.EqualTo(method), $"method of {entry.Name} in the central directory");
            Assert.That(entry.LocalMethod, Is.EqualTo(method), $"method of {entry.Name} in its local file header");
        }
    }

    [Test]
    public void TestCompressionSelectsTheMethod([Values] WriterKind kind, [ValueSource(nameof(Types))] CompressionType compression)
    {
        var zip = Write(kind, ArchiveFormat.Zip, compression, 6, w => AddEntries(w, Entries));

        AssertMethods(zip, MethodOf(compression));
        Assert.That(ZipEntries(zip).Select(e => e.Name), Is.EqualTo(Entries.Select(e => e.Name)));
        AssertFiles(zip, Entries);
    }

    // AES: both headers record method 99, and the AES extra field in each the key strength and the method the
    // entry is compressed with. Traditional encryption: both headers record that method. Either way both set
    // the encrypted flag (general purpose bit 0).
    private static void AssertEncryptedMethod(byte[] zip, ushort method, EncryptionType encryption)
    {
        Assert.That(StartsWith(zip, LocalFileHeader), Is.True, "the archive starts with a local file header: no filter wraps it");
        var entry = ZipEntries(zip).Single();
        Assert.That(entry.Flags & 1, Is.EqualTo(1), "encrypted flag in the central directory");
        Assert.That(entry.LocalFlags & 1, Is.EqualTo(1), "encrypted flag in the local file header");

        if (encryption == EncryptionType.Traditional)
        {
            Assert.That(entry.Method, Is.EqualTo(method), "method in the central directory");
            Assert.That(entry.LocalMethod, Is.EqualTo(method), "method in the local file header");
            Assert.That(entry.Aes, Is.Null, "AES extra field in the central directory");
            Assert.That(entry.LocalAes, Is.Null, "AES extra field in the local file header");
            return;
        }

        byte strength = encryption switch
        {
            EncryptionType.AES128 => 1,
            EncryptionType.AES192 => 2,
            _ => 3,
        };
        var aes = (strength, method);
        Assert.That(entry.Method, Is.EqualTo(ZipAesMethod), "method in the central directory");
        Assert.That(entry.LocalMethod, Is.EqualTo(ZipAesMethod), "method in the local file header");
        Assert.That(entry.Aes, Is.EqualTo(aes), "AES extra field in the central directory");
        Assert.That(entry.LocalAes, Is.EqualTo(aes), "AES extra field in the local file header");
    }

    private static byte[] WriteSecret(WriterKind kind, CompressionType compression, EncryptionType encryption) =>
        Write(kind, ArchiveFormat.Zip, compression, 6, w => w.AddEntry(EncryptionAssert.EntryName, Secret, MTime),
            Password, encryption);

    [Test]
    public void TestEncryptionKeepsTheMethod([ValueSource(nameof(Types))] CompressionType compression,
        [Values(EncryptionType.AES256, EncryptionType.AES192, EncryptionType.AES128, EncryptionType.Traditional)]
        EncryptionType encryption)
    {
        var zip = WriteSecret(WriterKind.File, compression, encryption);

        AssertEncryptedMethod(zip, MethodOf(compression), encryption);
        EncryptionAssert.IsEncrypted(Save(zip), Password);
    }

    [Test]
    public void TestEncryptionKeepsTheMethodWithEveryWriter([Values] WriterKind kind,
        [Values(EncryptionType.AES256, EncryptionType.Traditional)] EncryptionType encryption)
    {
        var zip = WriteSecret(kind, CompressionType.Zstd, encryption);

        AssertEncryptedMethod(zip, MethodOf(CompressionType.Zstd), encryption);
        EncryptionAssert.IsEncrypted(Save(zip), Password);
    }

    // WithPassword cannot change the encryption of an open archive, whatever its method, and leaves the method alone
    [Test]
    public void TestWithPasswordOnWriterWithAMethod()
    {
        var plain = Path.Combine(testDirectory, "plain.zip");
        using (var writer = new LibArchiveWriter(plain, ArchiveFormat.Zip, CompressionType.Bzip2))
        {
            Assert.That(writer.WithPassword(Password, EncryptionType.None), Is.SameAs(writer));
            Assert.Throws<InvalidOperationException>(() => writer.WithPassword(Password));
            writer.AddEntry(EncryptionAssert.EntryName, Secret, MTime);
        }
        AssertMethods(File.ReadAllBytes(plain), MethodOf(CompressionType.Bzip2));
        EncryptionAssert.IsNotEncrypted(plain);

        var encrypted = Path.Combine(testDirectory, "encrypted.zip");
        using (var writer = new LibArchiveWriter(encrypted, ArchiveFormat.Zip, CompressionType.Bzip2, password: Password))
        {
            Assert.Throws<InvalidOperationException>(() => writer.WithPassword(Password, EncryptionType.None));
            writer.AddEntry(EncryptionAssert.EntryName, Secret, MTime);
        }
        AssertEncryptedMethod(File.ReadAllBytes(encrypted), MethodOf(CompressionType.Bzip2), EncryptionType.AES256);
        EncryptionAssert.IsEncrypted(encrypted, Password);
    }

    // The level reaches each method: level 9 compresses this text better than level 1. (For Zstandard, ZIP's
    // level 1 is Zstandard's fastest level, which hardly compresses it.)
    [Test]
    public void TestLevelsOneAndNineDiffer([ValueSource(nameof(Types))] CompressionType compression)
    {
        var fastest = Write(ArchiveFormat.Zip, compression, 1, ("text.txt", Text));
        var smallest = Write(ArchiveFormat.Zip, compression, 9, ("text.txt", Text));

        foreach (var zip in new[] { fastest, smallest })
        {
            AssertMethods(zip, MethodOf(compression));
            AssertFiles(zip, ("text.txt", Text));
        }
        Assert.That(smallest.Length, Is.LessThan(fastest.Length));
    }

    // Level 0 stores the entries, whatever the method: the ZIP writer switches to store at level 0
    // (archive_write_set_format_zip.c:410-412), and the level is set after the method
    [Test]
    public void TestLevelZeroStores([ValueSource(nameof(Types))] CompressionType compression)
    {
        var zip = Write(ArchiveFormat.Zip, compression, 0, Entries);

        AssertMethods(zip, 0);
        foreach (var entry in ZipEntries(zip))
            Assert.That(entry.CompressedSize, Is.EqualTo(entry.Size), $"compressed size of {entry.Name}");
        foreach (var (name, data) in Entries)
            Assert.That(Contains(zip, data), Is.True, $"{name} is in the archive as it is");
        AssertFiles(zip, Entries);
    }

    // The default call writes what DEFLATE, ZIP's default method, writes at level 6, libarchive's default level
    [Test]
    public void TestDefaultIsDeflateAtLevelSix([Values] WriterKind kind)
    {
        void Add(LibArchiveWriter writer) => AddEntries(writer, Entries);
        var defaults = Write(kind, ArchiveFormat.Zip, compression: null, 0, Add);

        AssertMethods(defaults, MethodOf(CompressionType.Deflate));
        Assert.That(Write(kind, ArchiveFormat.Zip, CompressionType.Deflate, 6, Add), Is.EqualTo(defaults), "Deflate, level 6");
        Assert.That(Write(kind, ArchiveFormat.Zip, CompressionType.None, 6, Add), Is.EqualTo(defaults), "None, level 6");
    }
}
