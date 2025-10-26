#if NET462
using System;
using System.Collections.Generic;
using System.Linq;
#endif
using System.Security.Cryptography;
using LibArchive.Net;
using NUnit.Framework;
using static LibArchive.Net.LibArchiveReader;

namespace Test.LibArchive.Net
{

public class SevenZipTests
{
    #region Setup and Tear Down

    private readonly HashAlgorithm hasher;
    private readonly string emptyHash;

    public SevenZipTests()
    {
        hasher = SHA256.Create();
        emptyHash = HashToString(hasher.ComputeHash(Array.Empty<byte>()));
    }

    [OneTimeTearDownAttribute]
    public void OneTimeTearDown()
    {
        hasher.Dispose();
    }

    #endregion

    [Test]
    public void Test7z()
    {
        using var lar = new LibArchiveReader("7ztest.7z");

        var extracted = lar.Entries().ToDictionary(_ => _.Name, ToExtractedEntry);

        Assert.That(extracted, Is.EquivalentTo(new Dictionary<string, ExtractedEntry>
        {
            { "subdir/", new ExtractedEntry(EntryType.Directory, emptyHash) },
            { "empty", new ExtractedEntry(EntryType.RegularFile, emptyHash) },
            { "subdir/empty", new ExtractedEntry(EntryType.RegularFile, emptyHash) },
            { "1gzero", new ExtractedEntry(EntryType.RegularFile, "49-BC-20-DF-15-E4-12-A6-44-72-42-1E-13-FE-86-FF-1C-51-65-E1-8B-2A-FC-CF-16-0D-4D-C1-9F-E6-8A-14") },
            { "1krandom", new ExtractedEntry(EntryType.RegularFile, "DA-26-F3-BE-7A-9A-2D-F1-0A-49-35-87-8B-18-C8-FF-FE-2B-96-13-EA-CD-E2-C8-67-DF-8A-A2-5D-41-0D-0A") },
        }));
    }

    [Test]
    public void TestMultiRar()
    {
        var files = Enumerable.Range(1, 4).Select(n => $"rartest.part0000{n}.rar").ToArray();
        using var lar = new LibArchiveReader(files);

        var extracted = lar.Entries().ToDictionary(_ => _.Name, ToExtractedEntry);

        Assert.That(extracted, Is.EquivalentTo(new Dictionary<string, ExtractedEntry>
        {
            { "subdir", new ExtractedEntry(EntryType.Directory, emptyHash) },
            { "empty", new ExtractedEntry(EntryType.RegularFile, emptyHash) },
            { "subdir/empty", new ExtractedEntry(EntryType.RegularFile, emptyHash) },
            { "1gzero", new ExtractedEntry(EntryType.RegularFile, "8B-A9-05-B1-20-A7-C8-D7-89-0F-AB-53-3B-75-65-C9-2C-3D-30-B7-E2-98-41-DF-52-C0-CF-F3-9D-3C-F1-A2") },
            { "1krandom", new ExtractedEntry(EntryType.RegularFile, "DA-26-F3-BE-7A-9A-2D-F1-0A-49-35-87-8B-18-C8-FF-FE-2B-96-13-EA-CD-E2-C8-67-DF-8A-A2-5D-41-0D-0A") },
        }));
    }

    #region Support code

#if NET462
    // Use class instead of record for .NET Framework 4.6.2 compatibility
    private class ExtractedEntry
    {
        public EntryType Type { get; }
        public string ContentHash { get; }

        public ExtractedEntry(EntryType type, string contentHash)
        {
            Type = type;
            ContentHash = contentHash;
        }

        public override bool Equals(object? obj) =>
            obj is ExtractedEntry other && Type == other.Type && ContentHash == other.ContentHash;

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Type.GetHashCode();
                hash = hash * 31 + (ContentHash?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }
#else
    private record ExtractedEntry(EntryType Type, string ContentHash);
#endif

    private ExtractedEntry ToExtractedEntry(Entry entry) =>
        new ExtractedEntry(entry.Type, ContentHash(entry));

    private string ContentHash(Entry entry)
    {
        using var stream = entry.Stream;
        return HashToString(hasher.ComputeHash(stream));
    }

    private static string HashToString(byte[] hash) =>
        BitConverter.ToString(hash);

    #endregion
}

}