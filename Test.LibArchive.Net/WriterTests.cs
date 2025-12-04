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

    [Test]
    public void TestPasswordProtectedZipAES256()
    {
        var archivePath = Path.Combine(testDirectory, "encrypted.zip");
        var testData = "Secret data";
        var password = "TestPassword123!";

        // Create encrypted archive
        try
        {
            using (var writer = new LibArchiveWriter(
                archivePath,
                ArchiveFormat.Zip,
                password: password,
                encryption: EncryptionType.AES256))
            {
                writer.AddEntry("secret.txt", Encoding.UTF8.GetBytes(testData));
            }
        }
        catch (ApplicationException ex) when (ex.Message.Contains("Undefined option"))
        {
            Assert.Ignore("Encryption not supported by this libarchive build");
            return;
        }

        // Verify cannot read without password
        Assert.Throws<ApplicationException>(() =>
        {
            using var reader = new LibArchiveReader(archivePath);
            var entry = reader.Entries().First();
            entry.Stream.ReadByte();
        });

        // Verify can read with correct password
        using (var reader = new LibArchiveReader(archivePath, password: password))
        {
            var entry = reader.Entries().First();
            using var stream = entry.Stream;
            using var streamReader = new StreamReader(stream);
            Assert.That(streamReader.ReadToEnd(), Is.EqualTo(testData));
        }
    }

    [Test]
    public void TestPasswordProtected7z()
    {
        var archivePath = Path.Combine(testDirectory, "encrypted.7z");
        var password = "7zPassword";

        try
        {
            using (var writer = new LibArchiveWriter(
                archivePath,
                ArchiveFormat.SevenZip,
                password: password))
            {
                writer.AddEntry("data.bin", new byte[100]);
            }
        }
        catch (ApplicationException ex) when (ex.Message.Contains("Undefined option") ||
                                               ex.Message.Contains("encryption"))
        {
            Assert.Ignore("7-Zip encryption not supported by this libarchive build");
            return;
        }

        // Verify encryption is applied (if supported)
        using var reader = new LibArchiveReader(archivePath, password: password);
        var hasEncrypted = reader.HasEncryptedEntries();
        // hasEncrypted can be -1 if encryption detection isn't supported
        if (hasEncrypted < 0)
            Assert.Ignore("Encryption detection not supported by this libarchive build");
        Assert.That(hasEncrypted, Is.GreaterThan(0));
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

        var progress = new Progress<FileProgress>(p => progressReports.Add(p));

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddFiles(files, progress: progress);
        }

        // Verify progress was reported
        Assert.That(progressReports, Is.Not.Empty);
        Assert.That(progressReports.Last().IsComplete, Is.True);
        Assert.That(progressReports.Last().PercentComplete, Is.EqualTo(100).Within(0.1));
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
        stream.Read(readData, 0, readData.Length);

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

}
