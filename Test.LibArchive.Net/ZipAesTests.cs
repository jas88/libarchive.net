using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// WinZip AES encryption, as WinZip's "AES Encryption Information" (AE-1 and AE-2) describes it. An
// encrypted entry has compression method 99, and in its local and its central directory header an AES
// extra field (header ID 0x9901) of 7 bytes: the vendor version (1 for AE-1, 2 for AE-2, which stores
// no CRC), the vendor ID "AE", the strength (1, 2 or 3 for AES-128, AES-192 or AES-256) and the entry's
// actual compression method. Its data is a salt of 8, 12 or 16 bytes, a 2-byte password verification
// value, the encrypted data and a 10-byte authentication code. PBKDF2 with HMAC-SHA1 and 1000
// iterations derives from the password and the salt an AES key and an HMAC-SHA1 key of 16, 24 or 32
// bytes each, then the verification value. The data is encrypted with AES in CTR mode, with a
// little-endian counter that starts at 1, and the authentication code is the first 10 bytes of the
// HMAC-SHA1 of the encrypted data. WinZipAes below implements this from the specification, so these
// tests check what libarchive writes without libarchive's own reader.
//
// aes192test-p7zip.zip holds aes192.txt, four copies of a line of ASCII text (so that 7-Zip deflates
// it rather than storing it), encrypted with AES-192 by p7zip 17.05 (Homebrew) on macOS, without NTFS
// timestamps:
//
//     for i in 1 2 3 4; do echo 'AES-192, written by 7-Zip'; done > aes192.txt
//     touch -t 202601010000.00 aes192.txt
//     7z a -bd -bso0 -tzip -mem=AES192 -mtc=off -paes192-fixture aes192test-p7zip.zip aes192.txt
//
// 7-Zip wrote the entry as AE-2, strength 2, with Deflate (8) as its actual compression method.
[TestFixture]
public class ZipAesTests
{
    private const string Password = "TestPassword123!";

    // Shorter than 20 bytes: libarchive writes it as AE-2, as WinZip 11 does
    private const string ShortText = "tiny";

    private const string Fixture = "aes192test-p7zip.zip";
    private const string FixturePassword = "aes192-fixture";
    private static readonly string FixtureText = string.Concat(Enumerable.Repeat("AES-192, written by 7-Zip\n", 4));

    [TestCase(EncryptionType.AES128, 1, 8, 16)]
    [TestCase(EncryptionType.AES192, 2, 12, 24)]
    [TestCase(EncryptionType.AES256, 3, 16, 32)]
    [TestCase(EncryptionType.Default, 3, 16, 32)]
    public void WritesWinZipAesEntries(EncryptionType encryption, int strength, int saltLength, int keyLength)
    {
        byte[] archive;
        using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip, password: Password, encryption: encryption))
        {
            writer.AddEntry(EncryptionAssert.EntryName, Encoding.UTF8.GetBytes(EncryptionAssert.SecretText));
            writer.AddEntry("short.txt", Encoding.UTF8.GetBytes(ShortText));
            writer.Dispose();
            archive = writer.ToArray();
        }

        var entries = ZipEntryHeaders.Read(archive);
        Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { EncryptionAssert.EntryName, "short.txt" }));
        AssertWinZipAes(entries[0], vendorVersion: 1, strength, saltLength, keyLength, EncryptionAssert.SecretText);
        AssertWinZipAes(entries[1], vendorVersion: 2, strength, saltLength, keyLength, ShortText);
    }

    [Test]
    public void ReadsAes192ZipWrittenBy7Zip()
    {
        // The fixture is what it claims to be, by the specification
        var entry = ZipEntryHeaders.Read(File.ReadAllBytes(Fixture)).Single();
        Assert.That(entry.Name, Is.EqualTo("aes192.txt"));
        AssertWinZipAes(entry, vendorVersion: 2, strength: 2, saltLength: 12, keyLength: 24, FixtureText, FixturePassword);

        using var reader = new LibArchiveReader(Fixture, password: FixturePassword);
        var files = reader.Entries().Select(e => (e.Name, Text: e.ReadAllText())).ToArray();

        Assert.That(files, Is.EqualTo(new[] { ("aes192.txt", FixtureText) }));
    }

    [TestCase(null, "Passphrase required")]
    [TestCase("wrong-password", "Incorrect passphrase")]
    public void Aes192ZipWrittenBy7ZipNeedsItsPassword(string? password, string message)
    {
        using var reader = new LibArchiveReader(Fixture, password: password);

        var ex = Assert.Throws<ApplicationException>(() => reader.Entries().First().ReadAllText());

        Assert.That(ex!.Message, Does.Contain(message));
    }

    private static void AssertWinZipAes(ZipEntryHeaders entry, int vendorVersion, int strength, int saltLength,
        int keyLength, string text, string password = Password)
    {
        Assert.That((entry.LocalMethod, entry.CentralMethod), Is.EqualTo((99, 99)), "compression method");
        Assert.That((entry.LocalFlags & 1, entry.CentralFlags & 1), Is.EqualTo((1, 1)), "encrypted flag");
        foreach (var (aesField, header) in new[] { (entry.LocalAesField, "local"), (entry.CentralAesField, "central") })
        {
            Assert.That(aesField, Is.Not.Null, $"AES extra field in the {header} header");
            var field = aesField!;
            Assert.That(field, Has.Length.EqualTo(7), $"AES extra field size in the {header} header");
            Assert.That(field[0] | (field[1] << 8), Is.EqualTo(vendorVersion), $"vendor version in the {header} header");
            Assert.That(Encoding.ASCII.GetString(field, 2, 2), Is.EqualTo("AE"), $"vendor ID in the {header} header");
            Assert.That(field[4], Is.EqualTo(strength), $"strength in the {header} header");
            Assert.That(field[5] | (field[6] << 8), Is.EqualTo(8), $"actual compression method (Deflate) in the {header} header");
        }
        if (vendorVersion == 2)
            Assert.That(entry.CentralCrc, Is.Zero, "AE-2 stores no CRC");

        // Salt, password verification value, encrypted data, authentication code
        var data = entry.Data;
        var keys = WinZipAes.DeriveKeys(password, data.Take(saltLength).ToArray(), keyLength);
        Assert.That(data.Skip(saltLength).Take(2), Is.EqualTo(keys.Skip(2 * keyLength)),
            $"the password verification value follows a salt of {saltLength} bytes");
        var encrypted = data.Skip(saltLength + 2).Take(data.Length - saltLength - 2 - 10).ToArray();
        Assert.That(data.Skip(data.Length - 10), Is.EqualTo(WinZipAes.AuthenticationCode(keys, keyLength, encrypted)),
            "the authentication code ends the data");
        Assert.That(WinZipAes.DecryptAndInflate(keys, keyLength, encrypted), Is.EqualTo(text),
            $"the data decrypted with a key of {keyLength} bytes");
    }
}

/// <summary>
/// What the WinZip AES tests read of an entry of a ZIP archive: its local header, its central directory
/// header and its data. Enough of the format for the archives that libarchive and 7-Zip write here,
/// which have no archive comment and no Zip64 records.
/// </summary>
internal sealed record ZipEntryHeaders(
    string Name,
    int LocalFlags,
    int LocalMethod,
    byte[]? LocalAesField,
    int CentralFlags,
    int CentralMethod,
    byte[]? CentralAesField,
    uint CentralCrc,
    byte[] Data)
{
    private const int AesExtraFieldId = 0x9901;

    internal static List<ZipEntryHeaders> Read(byte[] zip)
    {
        // The end of central directory record, which is the last 22 bytes with no archive comment
        var end = zip.Length - 22;
        Assert.That(UInt32(zip, end), Is.EqualTo(0x06054B50u), "end of central directory signature");
        var count = UInt16(zip, end + 10);
        var central = (int)UInt32(zip, end + 16);

        var entries = new List<ZipEntryHeaders>();
        for (var i = 0; i < count; i++)
        {
            Assert.That(UInt32(zip, central), Is.EqualTo(0x02014B50u), "central directory header signature");
            var nameLength = UInt16(zip, central + 28);
            var extraLength = UInt16(zip, central + 30);
            var local = (int)UInt32(zip, central + 42);
            Assert.That(UInt32(zip, local), Is.EqualTo(0x04034B50u), "local file header signature");
            var localNameLength = UInt16(zip, local + 26);
            var localExtraLength = UInt16(zip, local + 28);

            entries.Add(new ZipEntryHeaders(
                Encoding.UTF8.GetString(zip, central + 46, nameLength),
                UInt16(zip, local + 6),
                UInt16(zip, local + 8),
                ExtraField(zip, local + 30 + localNameLength, localExtraLength),
                UInt16(zip, central + 8),
                UInt16(zip, central + 10),
                ExtraField(zip, central + 46 + nameLength, extraLength),
                UInt32(zip, central + 16),
                Slice(zip, local + 30 + localNameLength + localExtraLength, (int)UInt32(zip, central + 20))));
            central += 46 + nameLength + extraLength + UInt16(zip, central + 32);
        }
        return entries;
    }

    private static byte[]? ExtraField(byte[] zip, int start, int length)
    {
        for (var p = start; p + 4 <= start + length; p += 4 + UInt16(zip, p + 2))
        {
            if (UInt16(zip, p) == AesExtraFieldId)
                return Slice(zip, p + 4, UInt16(zip, p + 2));
        }
        return null;
    }

    internal static byte[] Slice(byte[] bytes, int start, int length)
    {
        var slice = new byte[length];
        Array.Copy(bytes, start, slice, 0, length);
        return slice;
    }

    private static int UInt16(byte[] bytes, int p) => bytes[p] | (bytes[p + 1] << 8);

    private static uint UInt32(byte[] bytes, int p) => (uint)UInt16(bytes, p) | ((uint)UInt16(bytes, p + 2) << 16);
}

/// <summary>
/// WinZip AES key derivation, authentication code and decryption, written from the specification (see
/// <see cref="ZipAesTests"/>) with .NET's cryptography rather than libarchive's.
/// </summary>
internal static class WinZipAes
{
    /// <summary>The AES key, the HMAC-SHA1 key and the password verification value, in that order.</summary>
    internal static byte[] DeriveKeys(string password, byte[] salt, int keyLength)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
#if NET6_0_OR_GREATER
        return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
#else
        using var pbkdf2 = new Rfc2898DeriveBytes(passwordBytes, salt, 1000);
        return pbkdf2.GetBytes(2 * keyLength + 2);
#endif
    }

    /// <summary>The first 10 bytes of the HMAC-SHA1 of the encrypted data.</summary>
    internal static byte[] AuthenticationCode(byte[] keys, int keyLength, byte[] encrypted)
    {
        using var hmac = new HMACSHA1(ZipEntryHeaders.Slice(keys, keyLength, keyLength));
        return hmac.ComputeHash(encrypted).Take(10).ToArray();
    }

    /// <summary>
    /// Decrypts with AES in CTR mode (the key's length selects AES-128, AES-192 or AES-256), then inflates
    /// the result as raw Deflate and decodes it as UTF-8.
    /// </summary>
    internal static string DecryptAndInflate(byte[] keys, int keyLength, byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = ZipEntryHeaders.Slice(keys, 0, keyLength);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var counter = new byte[16];
        var keystream = new byte[16];
        var plain = new byte[encrypted.Length];
        for (var i = 0; i < encrypted.Length; i++)
        {
            if (i % 16 == 0)
            {
                // The next value of the little-endian counter, encrypted
                for (var j = 0; j < counter.Length; j++)
                {
                    if (++counter[j] != 0)
                        break;
                }
                encryptor.TransformBlock(counter, 0, counter.Length, keystream, 0);
            }
            plain[i] = (byte)(encrypted[i] ^ keystream[i % 16]);
        }

        using var inflater = new DeflateStream(new MemoryStream(plain), CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
