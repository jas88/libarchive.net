using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static LibArchive.Net.LibArchiveReader;

namespace Test.LibArchive.Net;

// Reads text that libarchive's mtree reader takes for an mtree specification. Even without a
// "#mtree" signature, that reader claims text whose first lines each hold a single word, as a
// list of file names, and fails only when it reads the first entry, which has no type.
// Compressed, such text is read like any other compressed file that is not an archive, as one
// raw entry (see CompressedFileTests.cs); uncompressed, it fails as it always has, at the first
// header. A real mtree specification is still read as one, compressed or not.
//
// The fixtures are written by GNU gzip 1.15 and the bsdtar of libarchive 3.8.11:
//
//     seq 1 1000 > mtreetest-seq.txt
//     gzip -9 -n -c mtreetest-seq.txt > mtreetest-seq.txt.gz
//     mkdir -p tree/dir && printf 'Hello mtree\n' > tree/dir/file.txt && ln -s file.txt tree/dir/link
//     (cd tree && bsdtar --format=mtree --options '!all,type,size,link' -cf ../mtreetest.mtree dir)
//     gzip -9 -n -c mtreetest.mtree > mtreetest.mtree.gz
//     printf '#mtree\n./dir type=dir\n./dir/untyped\n' | gzip -9 -n > mtreetest-untyped.mtree.gz
//
// The specification records only types, sizes and link targets, so it does not depend on the
// owners and times of the files. libarchive 3.8.11 opens the files that a specification names
// only when its "checkfs" option is set, which LibArchive.Net never does, so these tests do not
// depend on the working directory: the file entry has the size the specification gives it and
// no data. The uncompressed fixtures are read the same way with CRLF line endings, which a
// Windows checkout can give them, so no test compares their bytes.
[TestFixture]
public class MtreeTextTests
{
    private const string Text = "mtreetest-seq.txt";
    private const string CompressedText = "mtreetest-seq.txt.gz";
    private const string UntypedSpec = "mtreetest-untyped.mtree.gz";

    // The name libarchive's raw format gives an entry whose compressed file records no name
    private const string RawEntryName = "data";

    private const string MissingType = "Missing type keyword in mtree specification";

    // AE_IFLNK, which EntryType does not name
    private const int SymbolicLink = 0xA000;

    // What "seq 1 1000" prints: the numbers 1 to 1000, one per line
    private static readonly byte[] Expected =
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, 1000).Select(n => n + "\n")));

    private static readonly string[] Specs = { "mtreetest.mtree", "mtreetest.mtree.gz" };

    // The entries of mtreetest.mtree: name, type, size and the length of the data read
    private static readonly (string, int, long?, int)[] SpecEntries =
    {
        ("./dir", (int)EntryType.Directory, 0, 0),
        ("./dir/file.txt", (int)EntryType.RegularFile, 12, 0),
        ("./dir/link", SymbolicLink, 0, 0),
    };

    public enum Source { Path, Volumes, Stream, NonSeekableStream }

    [Test]
    public void TestCompressedTextFirstEntry([Values] Source source)
    {
        using var reader = Open(source, CompressedText);

        var entry = reader.FirstEntry();
        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Name, Is.EqualTo(RawEntryName));
        Assert.That(entry.Type, Is.EqualTo(EntryType.RegularFile));
        Assert.That(entry.LengthBytes, Is.Null, "The decompressed size is not known in advance");
        Assert.That(entry.ReadAllBytes(), Is.EqualTo(Expected));
        Assert.That(reader.FirstEntry(), Is.Null, "A compressed file holds exactly one entry");
    }

    // From a non-seekable stream, the bytes that the mtree reader consumed are replayed to the
    // raw format; a tiny block size makes that replay span many chunks.
    [Test]
    public void TestCompressedTextEntries([Values] Source source, [Values(1u << 20, 16u)] uint blockSize)
    {
        using var reader = Open(source, CompressedText, blockSize);

        AssertRawEntry(reader);
    }

    [Test]
    public void TestResetRereadsCompressedText([Values(Source.Path, Source.Volumes, Source.Stream)] Source source)
    {
        using var reader = Open(source, CompressedText);

        AssertRawEntry(reader);
        reader.Reset();
        AssertRawEntry(reader);
    }

    // Uncompressed, the same text still fails as it always has: it opens, as an mtree
    // specification, and the first header throws.
    [Test]
    public void TestUncompressedTextStillFailsAtFirstHeader([Values] Source source)
    {
        using (var reader = Open(source, Text))
        {
            var fromFirstEntry = Assert.Throws<ApplicationException>(() => reader.FirstEntry());
            Assert.That(fromFirstEntry!.Message, Is.EqualTo(MissingType));
        }

        using (var reader = Open(source, Text))
        {
            using var entries = reader.Entries().GetEnumerator();
            var fromEntries = Assert.Throws<ApplicationException>(() => entries.MoveNext());
            Assert.That(fromEntries!.Message, Is.EqualTo(MissingType));
        }
    }

    [Test]
    public void TestSpecIsReadAsMtree([ValueSource(nameof(Specs))] string fixture, [Values] Source source)
    {
        using var reader = Open(source, fixture);

        var entries = reader.Entries()
            .Select(e => (e.Name, (int)e.Type, e.LengthBytes, e.ReadAllBytes().Length))
            .ToList();

        Assert.That(entries, Is.EqualTo(SpecEntries));
    }

    // Only the first header is read again as raw data: once an entry has been returned, a header
    // that fails is an error in the specification, and is reported.
    [Test]
    public void TestLaterHeaderErrorIsReported([Values] Source source)
    {
        using var reader = Open(source, UntypedSpec);
        using var entries = reader.Entries().GetEnumerator();

        Assert.That(entries.MoveNext(), Is.True);
        Assert.That(entries.Current.Name, Is.EqualTo("./dir"));
        var ex = Assert.Throws<ApplicationException>(() => entries.MoveNext());
        Assert.That(ex!.Message, Is.EqualTo(MissingType));
    }

    // The reader does not own a stream it is given; a MemoryStream needs no disposal.
    private static LibArchiveReader Open(Source source, string fixture, uint blockSize = 1 << 20) => source switch
    {
        Source.Path => new LibArchiveReader(fixture, blockSize),
        Source.Volumes => new LibArchiveReader(new[] { fixture }, blockSize),
        Source.Stream => new LibArchiveReader(new MemoryStream(File.ReadAllBytes(fixture)), blockSize: blockSize),
        Source.NonSeekableStream => new LibArchiveReader(
            new ReaderApiTests.NonSeekableStream(new MemoryStream(File.ReadAllBytes(fixture))), blockSize: blockSize),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static void AssertRawEntry(LibArchiveReader reader)
    {
        var entries = reader.Entries().Select(e => (e.Name, e.Type, e.LengthBytes, Content: e.ReadAllBytes())).ToList();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Name, Is.EqualTo(RawEntryName));
        Assert.That(entries[0].Type, Is.EqualTo(EntryType.RegularFile));
        Assert.That(entries[0].LengthBytes, Is.Null, "The decompressed size is not known in advance");
        Assert.That(entries[0].Content, Is.EqualTo(Expected));
    }
}
