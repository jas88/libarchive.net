using System;
using System.IO;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net
{

/// <summary>
/// Tests for the LibArchiveReader API improvements including:
/// - Stream-based reading
/// - Reset() functionality
/// - FirstEntry() convenience method
/// - ReadAllBytes()/ReadAllText() Entry methods
/// </summary>
[TestFixture]
public class ReaderApiTests
{
    private string testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), $"libarchive-reader-api-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, true);
    }

    #region Stream-based Reading Tests

    [Test]
    public void TestReadFromStream()
    {
        // Create a test archive
        var archivePath = Path.Combine(testDirectory, "test.zip");
        var testContent = "Hello from stream test!";

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("test.txt", Encoding.UTF8.GetBytes(testContent));
        }

        // Read using a stream
        using var fileStream = File.OpenRead(archivePath);
        using var reader = new LibArchiveReader(fileStream);

        var entry = reader.FirstEntry();
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Name, Is.EqualTo("test.txt"));
        Assert.That(entry.ReadAllText(), Is.EqualTo(testContent));
    }

    [Test]
    public void TestReadFromMemoryStream()
    {
        // Create archive in memory
        var testContent = "Memory stream content";
        byte[] archiveBytes;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            writer.AddEntry("memory.txt", Encoding.UTF8.GetBytes(testContent));
            writer.Dispose();
            archiveBytes = writer.ToArray();
        }

        // Read from MemoryStream
        using var memStream = new MemoryStream(archiveBytes);
        using var reader = new LibArchiveReader(memStream);

        var entry = reader.FirstEntry();
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.ReadAllText(), Is.EqualTo(testContent));
    }

    #endregion

    #region Reset() Tests

    [Test]
    public void TestResetFileBasedReader()
    {
        // Create test archive with multiple entries
        var archivePath = Path.Combine(testDirectory, "multi.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("file1.txt", Encoding.UTF8.GetBytes("Content 1"));
            writer.AddEntry("file2.txt", Encoding.UTF8.GetBytes("Content 2"));
        }

        using var reader = new LibArchiveReader(archivePath);

        // First pass - read all entries
        var firstPassEntries = new System.Collections.Generic.List<string>();
        foreach (var entry in reader.Entries())
        {
            firstPassEntries.Add(entry.Name);
        }
        Assert.That(firstPassEntries.Count, Is.EqualTo(2));

        // Reset and read again
        reader.Reset();

        var secondPassEntries = new System.Collections.Generic.List<string>();
        foreach (var entry in reader.Entries())
        {
            secondPassEntries.Add(entry.Name);
        }
        Assert.That(secondPassEntries.Count, Is.EqualTo(2));
        Assert.That(secondPassEntries, Is.EqualTo(firstPassEntries));
    }

    [Test]
    public void TestResetSeekableStream()
    {
        // Create test archive
        var testContent = "Seekable stream content";
        byte[] archiveBytes;

        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            writer.AddEntry("seekable.txt", Encoding.UTF8.GetBytes(testContent));
            writer.Dispose();
            archiveBytes = writer.ToArray();
        }

        // Use MemoryStream which is seekable
        using var memStream = new MemoryStream(archiveBytes);
        using var reader = new LibArchiveReader(memStream);

        // First read
        var entry1 = reader.FirstEntry();
        Assert.That(entry1, Is.Not.Null);
        var content1 = entry1!.ReadAllText();

        // Reset and read again
        reader.Reset();

        var entry2 = reader.FirstEntry();
        Assert.That(entry2, Is.Not.Null);
        var content2 = entry2!.ReadAllText();

        Assert.That(content2, Is.EqualTo(content1));
    }

    [Test]
    public void TestResetNonSeekableStreamThrows()
    {
        // Create test archive
        byte[] archiveBytes;
        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
        {
            writer.AddEntry("test.txt", Encoding.UTF8.GetBytes("test"));
            writer.Dispose();
            archiveBytes = writer.ToArray();
        }

        // Wrap in a non-seekable stream
        using var memStream = new MemoryStream(archiveBytes);
        using var nonSeekableStream = new NonSeekableStream(memStream);
        using var reader = new LibArchiveReader(nonSeekableStream);

        // Read first entry
        var entry = reader.FirstEntry();
        Assert.That(entry, Is.Not.Null);

        // Reset should throw for non-seekable stream
        Assert.Throws<NotSupportedException>(() => reader.Reset());
    }

    /// <summary>
    /// A wrapper stream that disables seeking.
    /// </summary>
    private class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    #endregion

    #region FirstEntry() Tests

    [Test]
    public void TestFirstEntry()
    {
        var archivePath = Path.Combine(testDirectory, "first.zip");
        var testContent = "First entry content";

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("first.txt", Encoding.UTF8.GetBytes(testContent));
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.FirstEntry();

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Name, Is.EqualTo("first.txt"));
        Assert.That(entry.ReadAllText(), Is.EqualTo(testContent));
    }

    [Test]
    public void TestFirstEntryEmptyArchive()
    {
        var archivePath = Path.Combine(testDirectory, "empty.zip");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            // Don't add any entries
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.FirstEntry();

        Assert.That(entry, Is.Null);
    }

    #endregion

    #region ReadAllBytes()/ReadAllText() Tests

    [Test]
    public void TestReadAllBytes()
    {
        var archivePath = Path.Combine(testDirectory, "bytes.zip");
        var testData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD };

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("binary.bin", testData);
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.FirstEntry();

        Assert.That(entry, Is.Not.Null);
        var readData = entry!.ReadAllBytes();
        Assert.That(readData, Is.EqualTo(testData));
    }

    [Test]
    public void TestReadAllTextUtf8()
    {
        var archivePath = Path.Combine(testDirectory, "utf8.zip");
        var testContent = "Hello, 世界! 🌍";

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("unicode.txt", Encoding.UTF8.GetBytes(testContent));
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.FirstEntry();

        Assert.That(entry, Is.Not.Null);
        var readContent = entry!.ReadAllText();
        Assert.That(readContent, Is.EqualTo(testContent));
    }

    [Test]
    public void TestReadAllTextCustomEncoding()
    {
        var archivePath = Path.Combine(testDirectory, "latin1.zip");
        var testContent = "Café résumé";
        var encoding = Encoding.GetEncoding("ISO-8859-1");

        using (var writer = new LibArchiveWriter(archivePath, ArchiveFormat.Zip))
        {
            writer.AddEntry("latin1.txt", encoding.GetBytes(testContent));
        }

        using var reader = new LibArchiveReader(archivePath);
        var entry = reader.FirstEntry();

        Assert.That(entry, Is.Not.Null);
        var readContent = entry!.ReadAllText(encoding);
        Assert.That(readContent, Is.EqualTo(testContent));
    }

    #endregion
}
}
