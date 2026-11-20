using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net
{
[TestFixture]
public class WriterTests
{
    private string testDirectory = null!;
    private readonly HashAlgorithm hasher = SHA256.Create();

    [SetUp]
    public void Setup()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), $"libarchive-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, true);
    }

    [OneTimeTearDown]
    public void OneTimeTeardown()
    {
        hasher.Dispose();
    }

    #region Basic Write Tests

    [Test]
    public void TestCreateSimpleZip()
    {
        var archivePath = Path.Combine(testDirectory, "test.zip");
        var testContent = "Hello, LibArchive.Net!";

        // Create archive
        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("test.txt", Encoding.UTF8.GetBytes(testContent));
        }

        // Verify archive was created
        Assert.That(File.Exists(archivePath), Is.True);
        Assert.That(new FileInfo(archivePath).Length, Is.GreaterThan(0));

        // Read back and verify
        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        Assert.That(entry.Name, Is.EqualTo("test.txt"));

        using var stream = entry.Stream;
        using var streamReader = new StreamReader(stream);
        var content = streamReader.ReadToEnd();
        Assert.That(content, Is.EqualTo(testContent));
    }

    [Test]
    public void TestCreate7zArchive()
    {
        var archivePath = Path.Combine(testDirectory, "test.7z");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.SevenZip))
        {
            writer.AddEntry("file1.txt", Encoding.UTF8.GetBytes("Content 1"));
            writer.AddEntry("file2.txt", Encoding.UTF8.GetBytes("Content 2"));
        }

        Assert.That(File.Exists(archivePath), Is.True);

        using var reader = new LibArchiveReader(archivePath);
        var entries = reader.Entries().ToList();
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "file1.txt", "file2.txt" }));
    }

    [Test]
    public void TestCreateTarGz()
    {
        var archivePath = Path.Combine(testDirectory, "test.tar.gz");

        using (var writer = new LibArchiveWriter(
            archivePath,
            ArchiveFormat.Tar,
            compression: CompressionType.Gzip,
            compressionLevel: 9))
        {
            writer.AddEntry("data.bin", new byte[1024]); // 1KB of zeros
        }

        Assert.That(File.Exists(archivePath), Is.True);

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        Assert.That(entry.Name, Is.EqualTo("data.bin"));
    }

    #endregion

    #region File Addition Tests

    [Test]
    public void TestAddFile()
    {
        var sourceFile = Path.Combine(testDirectory, "source.txt");
        var testData = "Test file content";
        File.WriteAllText(sourceFile, testData);

        var archivePath = Path.Combine(testDirectory, "files.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddFile(sourceFile, "archived.txt");
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        Assert.That(entry.Name, Is.EqualTo("archived.txt"));

        using var stream = entry.Stream;
        using var streamReader = new StreamReader(stream);
        Assert.That(streamReader.ReadToEnd(), Is.EqualTo(testData));
    }

    [Test]
    public void TestAddMultipleFiles()
    {
        // Create test files
        var files = new List<FileInfo>();
        for (int i = 0; i < 10; i++)
        {
            var filePath = Path.Combine(testDirectory, $"file{i}.txt");
            File.WriteAllText(filePath, $"Content {i}");
            files.Add(new FileInfo(filePath));
        }

        var archivePath = Path.Combine(testDirectory, "multiple.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddFiles(files);
        }

        using var reader = new LibArchiveReader(archivePath);
        var entries = reader.Entries().ToList();
        Assert.That(entries, Has.Count.EqualTo(10));
    }

    [Test]
    public void TestAddDirectory()
    {
        var sourceDir = Path.Combine(testDirectory, "source");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "subdir"));

        File.WriteAllText(Path.Combine(sourceDir, "file1.txt"), "File 1");
        File.WriteAllText(Path.Combine(sourceDir, "file2.txt"), "File 2");
        File.WriteAllText(Path.Combine(sourceDir, "subdir", "file3.txt"), "File 3");

        var archivePath = Path.Combine(testDirectory, "directory.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddDirectory(sourceDir, recursive: true);
        }

        using var reader = new LibArchiveReader(archivePath);
        var entries = reader.Entries().ToList();
        Assert.That(entries, Has.Count.EqualTo(3));
        Assert.That(entries.Any(e => e.Name.Contains("subdir")), Is.True);
    }

    #endregion

    #region Encryption Tests

    private const string Password = "TestPassword123!";

    [TestCase(EncryptionType.Default)]
    [TestCase(EncryptionType.AES256)]
    [TestCase(EncryptionType.AES192)]
    [TestCase(EncryptionType.AES128)]
    [TestCase(EncryptionType.Traditional)]
    public void TestPasswordProtectedZip(EncryptionType encryption)
    {
        var archivePath = Path.Combine(testDirectory, "encrypted.zip");

        using (var writer = new LibArchiveWriter(
            archivePath,
            ArchiveFormat.Zip,
            password: Password,
            encryption: encryption))
        {
            writer.AddEntry(EncryptionAssert.EntryName, Encoding.UTF8.GetBytes(EncryptionAssert.SecretText));
        }

        EncryptionAssert.IsEncrypted(archivePath, Password);
    }

    [TestCase(ArchiveFormat.SevenZip)]
    [TestCase(ArchiveFormat.Tar)]
    [TestCase(ArchiveFormat.Ustar)]
    [TestCase(ArchiveFormat.Pax)]
    [TestCase(ArchiveFormat.Cpio)]
    [TestCase(ArchiveFormat.Iso9660)]
    [TestCase(ArchiveFormat.Xar)]
    public void TestPasswordWithNonZipFormatThrows(ArchiveFormat format)
    {
        // libarchive only encrypts ZIP archives. Every other writer accepts a passphrase and ignores it,
        // so the wrapper has to refuse instead of writing plaintext.
        var archivePath = Path.Combine(testDirectory, "not-encrypted.bin");

        var ex = Assert.Throws<NotSupportedException>(() =>
            new LibArchiveWriter(archivePath, format, password: Password).Dispose());

        Assert.That(ex!.Message, Does.Contain("ZIP"));
        Assert.That(File.Exists(archivePath), Is.False, "The output file must not be created");
    }

    [Test]
    public void TestPasswordWithNonZipFormatDoesNotTruncateExistingFile()
    {
        var archivePath = Path.Combine(testDirectory, "existing.7z");
        var original = Encoding.UTF8.GetBytes("existing content");
        File.WriteAllBytes(archivePath, original);

        Assert.Throws<NotSupportedException>(() =>
            new LibArchiveWriter(archivePath, ArchiveFormat.SevenZip, password: Password).Dispose());

        Assert.That(File.ReadAllBytes(archivePath), Is.EqualTo(original));
    }

    [TestCase(ArchiveFormat.Zip)]
    [TestCase(ArchiveFormat.SevenZip)]
    [TestCase(ArchiveFormat.Tar)]
    public void TestEncryptionNoneWithPasswordWritesUnencryptedArchive(ArchiveFormat format)
    {
        var archivePath = Path.Combine(testDirectory, "plain.bin");

        using (var writer = new LibArchiveWriter(
            archivePath,
            format,
            password: Password,
            encryption: EncryptionType.None))
        {
            writer.AddEntry(EncryptionAssert.EntryName, Encoding.UTF8.GetBytes(EncryptionAssert.SecretText));
        }

        EncryptionAssert.IsNotEncrypted(archivePath);
    }

    [Test]
    public void TestWithPasswordRejectsWhatLibarchiveCannotEncrypt()
    {
        using var sevenZip = new LibArchiveWriter(Path.Combine(testDirectory, "with-password.7z"), ArchiveFormat.SevenZip);
        Assert.Throws<NotSupportedException>(() => sevenZip.WithPassword(Password));
    }

    [Test]
    public void TestWithPasswordRejectsInvalidArguments()
    {
        using var zip = new LibArchiveWriter(Path.Combine(testDirectory, "with-password.zip"), ArchiveFormat.Zip);

        Assert.Throws<ArgumentException>(() => zip.WithPassword(string.Empty));
        Assert.Throws<ArgumentException>(() => zip.WithPassword(Password, (EncryptionType)int.MaxValue));
    }

    [Test]
    public void TestWithPasswordCannotChangeEncryptionOfOpenArchive()
    {
        // libarchive accepts a passphrase only before the archive is opened, and the constructor opens it
        using var plain = new LibArchiveWriter(Path.Combine(testDirectory, "plain.zip"), ArchiveFormat.Zip);
        Assert.Throws<InvalidOperationException>(() => plain.WithPassword(Password));
        Assert.Throws<InvalidOperationException>(() => plain.WithPassword(Password, EncryptionType.AES192));

        using var encrypted = new LibArchiveWriter(
            Path.Combine(testDirectory, "encrypted.zip"),
            ArchiveFormat.Zip,
            password: Password);
        Assert.Throws<InvalidOperationException>(() => encrypted.WithPassword(Password, EncryptionType.None));
    }

    [Test]
    public void TestWithPasswordNoneLeavesArchiveUnencrypted()
    {
        var archivePath = Path.Combine(testDirectory, "plain.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            Assert.That(writer.WithPassword(Password, EncryptionType.None), Is.SameAs(writer));
            writer.AddEntry(EncryptionAssert.EntryName, Encoding.UTF8.GetBytes(EncryptionAssert.SecretText));
        }

        EncryptionAssert.IsNotEncrypted(archivePath);
    }

    #endregion

    #region Progress Reporting Tests

    [Test]
    public void TestProgressReporting()
    {
        // Create test files
        var files = new List<FileInfo>();
        for (int i = 0; i < 5; i++)
        {
            var filePath = Path.Combine(testDirectory, $"file{i}.dat");
            File.WriteAllBytes(filePath, new byte[1024 * 100]); // 100 KB each
            files.Add(new FileInfo(filePath));
        }

        var archivePath = Path.Combine(testDirectory, "progress.zip");
        var progressReports = new List<FileProgress>();

        var progress = new SynchronousProgress<FileProgress>(p => progressReports.Add(p));

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddFiles(files, progress: progress);
        }

        // Verify progress was reported
        Assert.That(progressReports, Is.Not.Empty);
        Assert.That(progressReports.Last().IsComplete, Is.True);
        Assert.That(progressReports.Last().PercentComplete, Is.EqualTo(100).Within(0.1));
    }

    /// <summary>
    /// Invokes the handler inline on the reporting thread.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> is deliberately asynchronous: it marshals callbacks to the
    /// captured <see cref="System.Threading.SynchronizationContext"/>, or posts them to the
    /// ThreadPool when there is none, as is the case under NUnit. Reports therefore arrive
    /// after the assertions have already run, and the handler would be mutating a
    /// non-thread-safe <see cref="List{T}"/> from pool threads. Reporting inline keeps the
    /// assertions deterministic while still exercising the library's IProgress contract.
    /// </remarks>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> handler;

        public SynchronousProgress(Action<T> handler) => this.handler = handler;

        public void Report(T value) => handler(value);
    }

    #endregion

    #region Empty File and Directory Tests

    [Test]
    public void TestAddEmptyFile()
    {
        var archivePath = Path.Combine(testDirectory, "empty.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("empty.txt", Array.Empty<byte>());
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        Assert.That(entry.Name, Is.EqualTo("empty.txt"));

        using var stream = entry.Stream;
        Assert.That(stream.Read(new byte[1], 0, 1), Is.EqualTo(0));
    }

    [Test]
    public void TestAddDirectoryEntry()
    {
        var archivePath = Path.Combine(testDirectory, "dirs.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddDirectoryEntry("folder/");
            writer.AddDirectoryEntry("folder/subfolder");
        }

        using var reader = new LibArchiveReader(archivePath);
        var entries = reader.Entries().ToList();
        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.That(entries.All(e => e.IsDirectory), Is.True);
    }

    #endregion

    #region Round-Trip Tests

    [Test]
    public void TestRoundTripPreservesContent()
    {
        var archivePath = Path.Combine(testDirectory, "roundtrip.zip");
        var originalData = new byte[10000];
        new Random(42).NextBytes(originalData);

        // Write
        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("random.bin", originalData);
        }

        // Read
        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        using var stream = entry.Stream;
        var readData = new byte[originalData.Length];
#if NET7_0_OR_GREATER
        stream.ReadExactly(readData);
#else
        int totalRead = 0;
        while (totalRead < readData.Length)
        {
            int bytesRead = stream.Read(readData, totalRead, readData.Length - totalRead);
            if (bytesRead == 0) break;
            totalRead += bytesRead;
        }
#endif

        // Verify
        Assert.That(readData, Is.EqualTo(originalData));
    }

    [Test]
    public void TestRoundTripPreservesFileHash()
    {
        var sourceFile = Path.Combine(testDirectory, "large.dat");
        var largeData = new byte[1024 * 1024]; // 1 MB
        new Random(123).NextBytes(largeData);
        File.WriteAllBytes(sourceFile, largeData);

        var originalHash = hasher.ComputeHash(File.ReadAllBytes(sourceFile));

        var archivePath = Path.Combine(testDirectory, "large.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddFile(sourceFile);
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        var extractedHash = hasher.ComputeHash(entry.Stream);

        Assert.That(extractedHash, Is.EqualTo(originalHash));
    }

    // AddFile reads a file of more than 2 MB and less than 64 MB through a 1 MiB pooled buffer on
    // every target framework (smaller files go through other paths on .NET 9 and later): this one
    // takes three full reads and a short last one.
    [Test]
    public void TestRoundTripOfFileReadThroughPooledBuffer()
    {
        var sourceFile = Path.Combine(testDirectory, "pooled.dat");
        var data = new byte[3 * 1024 * 1024 + 123];
        new Random(456).NextBytes(data);
        File.WriteAllBytes(sourceFile, data);

        var archivePath = Path.Combine(testDirectory, "pooled.tar");
        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Tar))
        {
            writer.AddFile(sourceFile);
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        Assert.That(entry.Name, Is.EqualTo("pooled.dat"));
        Assert.That(entry.LengthBytes, Is.EqualTo(data.Length));
        Assert.That(hasher.ComputeHash(entry.Stream), Is.EqualTo(hasher.ComputeHash(data)));
    }

    #endregion

    #region Compression Tests

    [TestCase(CompressionType.Gzip)]
    [TestCase(CompressionType.Bzip2)]
    [TestCase(CompressionType.Xz)]
    public void TestDifferentCompressionTypes(CompressionType compression)
    {
        var archivePath = Path.Combine(testDirectory, $"compressed-{compression}.tar.{GetCompressionExtension(compression)}");

        using (var writer = new LibArchiveWriter(
            archivePath,
            ArchiveFormat.Tar,
            compression: compression))
        {
            writer.AddEntry("compressible.txt", Encoding.UTF8.GetBytes(new string('A', 10000)));
        }

        Assert.That(File.Exists(archivePath), Is.True);

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().First();
        Assert.That(entry.Name, Is.EqualTo("compressible.txt"));
    }

    private static string GetCompressionExtension(CompressionType type) => type switch
    {
        CompressionType.Gzip => "gz",
        CompressionType.Bzip2 => "bz2",
        CompressionType.Xz => "xz",
        _ => "bin"
    };

    #endregion
}

/// <summary>
/// Reads archives back to prove whether their content is really encrypted.
/// </summary>
internal static class EncryptionAssert
{
    internal const string EntryName = "secret.txt";

    // Long and repetitive so the entry is deflated: data decrypted with the wrong key cannot be a valid stream
    internal static readonly string SecretText =
        string.Concat(Enumerable.Repeat("Secret data that must not be readable without the password. ", 64));

    internal static void IsEncrypted(string archivePath, string password)
    {
        var noPassword = Assert.Throws<ApplicationException>(() => ReadFirstEntry(archivePath));
        Assert.That(noPassword!.Message, Does.Contain("Passphrase required"));

        // Only the exception type is asserted here. ZIP checks a password against a one or two byte verifier,
        // so a wrong password occasionally gets past it and fails on the decrypted data with another message.
        Assert.Throws<ApplicationException>(() => ReadFirstEntry(archivePath, "Wrong" + password));

        Assert.That(ReadFirstEntry(archivePath, password), Is.EqualTo(SecretText));
    }

    internal static void IsNotEncrypted(string archivePath)
    {
        Assert.That(ReadFirstEntry(archivePath), Is.EqualTo(SecretText));
    }

    private static string ReadFirstEntry(string archivePath, string? password = null)
    {
        using var reader = new LibArchiveReader(archivePath, password: password);
        var entry = reader.Entries().First();
        using var stream = entry.Stream;
        using var streamReader = new StreamReader(stream);
        return streamReader.ReadToEnd();
    }
}
}
