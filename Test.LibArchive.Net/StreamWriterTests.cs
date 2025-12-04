using System;
using System.IO;
using System.Linq;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net
{
[TestFixture]
public class StreamWriterTests
{
    private string testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), $"libarchive-stream-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, true);
    }

    #region Stream Writing Tests

    [Test]
    public void TestWriteToStream()
    {
        var testContent = "Hello from stream!";
        byte[] archiveBytes;

        // Write to MemoryStream
        using (var memoryStream = new MemoryStream())
        {
            using (var writer = new LibArchiveWriter(memoryStream, ArchiveFormat.Zip))
            {
                writer.AddEntry("test.txt", Encoding.UTF8.GetBytes(testContent));
            }

            archiveBytes = memoryStream.ToArray();
        }

        Assert.That(archiveBytes, Is.Not.Empty);

        // Verify by reading back from file
        var tempFile = Path.Combine(testDirectory, "stream-test.zip");
        File.WriteAllBytes(tempFile, archiveBytes);

        using var reader = new LibArchiveReader(tempFile);
        var entry = reader.Entries().Single();
        using var stream = entry.Stream;
        using var streamReader = new StreamReader(stream);
        Assert.That(streamReader.ReadToEnd(), Is.EqualTo(testContent));
    }

    [Test]
    public void TestWriteToFileStream()
    {
        var archivePath = Path.Combine(testDirectory, "filestream.zip");
        var testData = new byte[10000];
        new Random(42).NextBytes(testData);

        using (var fileStream = File.Create(archivePath))
        {
            using var writer = new LibArchiveWriter(fileStream, ArchiveFormat.Zip);
            writer.AddEntry("random.bin", testData);
        }

        Assert.That(File.Exists(archivePath), Is.True);

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.Entries().Single();
        using var stream = entry.Stream;
        var readData = new byte[testData.Length];
        stream.Read(readData, 0, readData.Length);
        Assert.That(readData, Is.EqualTo(testData));
    }

    #endregion

    #region Memory Writer Tests

    [Test]
    public void TestCreateMemoryWriter()
    {
        var testContent = "Memory writer test";
        byte[] archiveBytes;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            writer.AddEntry("test.txt", Encoding.UTF8.GetBytes(testContent));
        }
        // Note: We can't call ToArray() here because we need to dispose the writer first

        // Instead, test with direct stream access
        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            writer.AddEntry("test.txt", Encoding.UTF8.GetBytes(testContent));
            writer.Dispose();

            archiveBytes = writer.ToArray();
        }

        Assert.That(archiveBytes, Is.Not.Empty);

        // Verify content
        var tempFile = Path.Combine(testDirectory, "memory-test.zip");
        File.WriteAllBytes(tempFile, archiveBytes);

        using var reader = new LibArchiveReader(tempFile);
        var entry = reader.Entries().Single();
        using var stream = entry.Stream;
        using var streamReader = new StreamReader(stream);
        Assert.That(streamReader.ReadToEnd(), Is.EqualTo(testContent));
    }

    [Test]
    public void TestMemoryWriterWithCompression()
    {
        byte[] archiveBytes;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(
            ArchiveFormat.Tar,
            compression: CompressionType.Gzip,
            compressionLevel: 9))
        {
            // Add compressible data
            var data = Encoding.UTF8.GetBytes(new string('A', 10000));
            writer.AddEntry("compressible.txt", data);
            writer.Dispose();

            archiveBytes = writer.ToArray();
        }

        Assert.That(archiveBytes, Is.Not.Empty);
        // Compressed size should be much smaller than 10KB
        Assert.That(archiveBytes.Length, Is.LessThan(10000));
    }

    [Test]
    public void TestMemoryWriterWithEncryption()
    {
        var password = "TestPassword123";
        var testContent = "Secret data";
        byte[] archiveBytes;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(
            ArchiveFormat.Zip,
            password: password,
            encryption: EncryptionType.AES256))
        {
            writer.AddEntry("secret.txt", Encoding.UTF8.GetBytes(testContent));
            writer.Dispose();

            archiveBytes = writer.ToArray();
        }

        Assert.That(archiveBytes, Is.Not.Empty);

        // Verify encryption by reading back
        var tempFile = Path.Combine(testDirectory, "encrypted-memory.zip");
        File.WriteAllBytes(tempFile, archiveBytes);

        // Should fail without password
        Assert.Throws<ApplicationException>(() =>
        {
            using var reader = new LibArchiveReader(tempFile);
            var entry = reader.Entries().First();
            entry.Stream.ReadByte();
        });

        // Should succeed with password
        using (var reader = new LibArchiveReader(tempFile, password: password))
        {
            var entry = reader.Entries().Single();
            using var stream = entry.Stream;
            using var streamReader = new StreamReader(stream);
            Assert.That(streamReader.ReadToEnd(), Is.EqualTo(testContent));
        }
    }

    [Test]
    public void TestMemoryWriterMultipleFiles()
    {
        byte[] archiveBytes;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            for (int i = 0; i < 10; i++)
            {
                writer.AddEntry($"file{i}.txt", Encoding.UTF8.GetBytes($"Content {i}"));
            }
            writer.Dispose();

            archiveBytes = writer.ToArray();
        }

        var tempFile = Path.Combine(testDirectory, "multiple-memory.zip");
        File.WriteAllBytes(tempFile, archiveBytes);

        using var reader = new LibArchiveReader(tempFile);
        var entries = reader.Entries().ToList();
        Assert.That(entries, Has.Count.EqualTo(10));
    }

    [Test]
    public void TestToMemoryStream()
    {
        MemoryStream resultStream;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            writer.AddEntry("test.txt", Encoding.UTF8.GetBytes("Test"));
            writer.Dispose();

            resultStream = writer.ToMemoryStream();
        }

        Assert.That(resultStream, Is.Not.Null);
        Assert.That(resultStream.Length, Is.GreaterThan(0));
        Assert.That(resultStream.CanRead, Is.True);
    }

    [Test]
    public void TestToArrayThrowsIfNotDisposed()
    {
        using var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip);
        writer.AddEntry("test.txt", Encoding.UTF8.GetBytes("Test"));

        // Should throw because writer is not disposed
        Assert.Throws<InvalidOperationException>(() => writer.ToArray());
    }

    [Test]
    public void TestToArrayThrowsIfNotMemoryWriter()
    {
        var tempFile = Path.Combine(testDirectory, "file.zip");

        using var writer = new LibArchiveWriter(tempFile, ArchiveFormat.Zip);
        writer.AddEntry("test.txt", Encoding.UTF8.GetBytes("Test"));
        writer.Dispose();

        // Should throw because this is a file-based writer
        Assert.Throws<InvalidOperationException>(() => writer.ToArray());
    }

    #endregion

    #region Stream Writing with Real Files

    [Test]
    public void TestStreamWriterWithRealFile()
    {
        // Create a test file
        var sourceFile = Path.Combine(testDirectory, "source.txt");
        File.WriteAllText(sourceFile, "Test file content");

        byte[] archiveBytes;

        using (var memoryStream = new MemoryStream())
        {
            using (var writer = new LibArchiveWriter(memoryStream, ArchiveFormat.Zip))
            {
                writer.AddFile(sourceFile, "archived.txt");
            }

            archiveBytes = memoryStream.ToArray();
        }

        // Verify
        var tempArchive = Path.Combine(testDirectory, "stream-archive.zip");
        File.WriteAllBytes(tempArchive, archiveBytes);

        using var reader = new LibArchiveReader(tempArchive);
        var entry = reader.Entries().Single();
        Assert.That(entry.Name, Is.EqualTo("archived.txt"));
    }

    #endregion

    #region Large Data Tests

    [Test]
    public void TestStreamWriterWithLargeFile()
    {
        // Create 5 MB of compressible data (repetitive pattern)
        var largeData = new byte[5 * 1024 * 1024];
        var pattern = Encoding.UTF8.GetBytes("This is a repeating pattern for compression testing. ");
        for (int i = 0; i < largeData.Length; i++)
            largeData[i] = pattern[i % pattern.Length];

        byte[] archiveBytes;

        using (var memoryStream = new MemoryStream())
        {
            using (var writer = new LibArchiveWriter(
                memoryStream,
                ArchiveFormat.Zip,
                compression: CompressionType.Deflate))
            {
                writer.AddEntry("large.bin", largeData);
            }

            archiveBytes = memoryStream.ToArray();
        }

        Assert.That(archiveBytes, Is.Not.Empty);
        // Compressed size should be less than original (compressible data)
        Assert.That(archiveBytes.Length, Is.LessThan(largeData.Length));
    }

    #endregion
}

}
