using System.Security.Cryptography;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;
using static LibArchive.Net.LibArchiveReader;

namespace Test.LibArchive.Net;

// Reads a tar archive compressed with lzop. The native libraries decompress lzop data
// in-process with lzokay's LZO1X decompressor (see native/build-lzo-shim.sh). The wrapper
// has no lzop writer, so the archive has to be a fixture.
//
// The fixture is written by the reference lzop tool, so that these tests check lzokay
// against real lzop output and not against itself. It was created with lzop 1.04
// (LZO 2.10) and the bsdtar of macOS:
//
//     mkdir lzoptest && cd lzoptest
//     seq 1 5000 > numbers.txt
//     head -c 600000 /dev/zero > zeros.bin
//     : > empty
//     touch -t 202601010000.00 numbers.txt zeros.bin empty
//     COPYFILE_DISABLE=1 tar --format=ustar --no-xattrs --no-mac-metadata --no-acls \
//         --uid 0 --gid 0 --numeric-owner -cf ../lzoptest.tar numbers.txt zeros.bin empty
//     cd .. && touch -t 202601010000.00 lzoptest.tar && lzop -9 lzoptest.tar
//
// The extra tar options keep extended attributes, AppleDouble files and the local user
// and group out of the archive. zeros.bin spans three of lzop's 256 KiB blocks.
[TestFixture]
public class LzopTests
{
    private const string Fixture = "lzoptest.tar.lzo";

    private const int ZerosLength = 600000;

    // What "seq 1 5000" prints: the numbers 1 to 5000, one per line
    private static readonly byte[] Numbers =
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, 5000).Select(n => n + "\n")));

    [SetUp]
    public void RequireFixture()
    {
        Assert.That(Fixture, Does.Exist, $"{Fixture} was not copied to the test output directory");
    }

    [Test]
    public void TestLzopTarFromFile()
    {
        using var reader = new LibArchiveReader(Fixture);

        AssertContents(reader);
    }

    [Test]
    public void TestLzopTarFromStream()
    {
        using var stream = File.OpenRead(Fixture);
        using var reader = new LibArchiveReader(stream);

        AssertContents(reader);
    }

    [Test]
    public void TestLzopTarText()
    {
        using var reader = new LibArchiveReader(Fixture);

        var numbers = reader.Entries().First(entry => entry.Name == "numbers.txt");

        Assert.That(numbers.ReadAllText(Encoding.ASCII), Is.EqualTo(Encoding.ASCII.GetString(Numbers)));
    }

    private static void AssertContents(LibArchiveReader reader)
    {
        var extracted = reader.Entries().ToDictionary(
            entry => entry.Name,
            entry => (entry.Type, entry.LengthBytes, Hash: Hash(entry.ReadAllBytes())));

        Assert.That(extracted, Is.EquivalentTo(new Dictionary<string, (EntryType, long?, string)>
        {
            { "numbers.txt", (EntryType.RegularFile, Numbers.Length, Hash(Numbers)) },
            { "zeros.bin", (EntryType.RegularFile, ZerosLength, Hash(new byte[ZerosLength])) },
            { "empty", (EntryType.RegularFile, 0L, Hash(Array.Empty<byte>())) },
        }));
    }

    private static string Hash(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(content));
    }
}
