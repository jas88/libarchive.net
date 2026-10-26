using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

[TestFixture]
public class StreamReaderTests
{
    private static readonly Dictionary<string, byte[]> Payload = new()
    {
        { "first.txt", "First file contents"u8.ToArray() },
        { "second.txt", "Second file contents"u8.ToArray() }
    };

    private readonly string testDirectory =
        Path.Combine(Path.GetTempPath(), $"libarchive-stream-reader-tests-{Guid.NewGuid():N}");

    [SetUp]
    public void Setup()
    {
        Directory.CreateDirectory(testDirectory);
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }

    [TestCase(ArchiveFormat.SevenZip)]
    [TestCase(ArchiveFormat.Zip)]
    public void TestReadFromFileStream(ArchiveFormat format)
    {
        var archivePath = ArchiveFile(format);
        using var stream = File.OpenRead(archivePath);

        using var reader = new LibArchiveReader(stream);
        AssertContents(reader);
    }

    [TestCase(ArchiveFormat.SevenZip)]
    [TestCase(ArchiveFormat.Zip)]
    public void TestReadAndResetFromInitialPosition(ArchiveFormat format)
    {
        var prefix = RandomBuffer();
        var contents = prefix.Concat(ArchiveBytes(format)).ToArray();

        using var stream = new MemoryStream(contents);
        stream.Seek(prefix.Length, SeekOrigin.Begin);

        // Reads from current stream position (after prefix)
        using var reader = new LibArchiveReader(stream);
        AssertContents(reader);

        // Resets to same position (after prefix)
        reader.Reset();
        AssertContents(reader);
    }

    private byte[] RandomBuffer()
    {
        return Encoding.UTF8.GetBytes(testDirectory);
    }

    private static byte[] ArchiveBytes(ArchiveFormat format)
    {
        using var stream = new MemoryStream();
        using (var writer = new LibArchiveWriter(stream, format))
        {
            foreach (var file in Payload)
            {
                writer.AddEntry(file.Key, file.Value);
            }
        }
        return stream.ToArray();
    }

    private string ArchiveFile(ArchiveFormat format)
    {
        var archivePath = Path.Combine(testDirectory, "archive");
        File.WriteAllBytes(archivePath, ArchiveBytes(format));
        return archivePath;
    }

    private static void AssertContents(LibArchiveReader reader)
    {
        var entries = reader.Entries().ToDictionary(entry => entry.Name, entry => entry.ReadAllBytes());
        Assert.That(entries, Is.EqualTo(Payload));
    }
}
