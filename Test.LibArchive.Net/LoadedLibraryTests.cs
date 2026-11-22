using System.Globalization;
using System.Runtime.InteropServices;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// Which native library LibArchive.Net's P/Invokes call, where CI says which it must be: it sets
// LIBARCHIVE_NET_EXPECT_RID to the runtime whose library .NET must load, linux-x64 in the
// package job on Ubuntu and linux-musl-x64 in the test-alpine job, for net11.0 and for net8.0,
// where the tests run LibArchive.Net's .NET Standard 2.0 build, which loads the library itself.
// No other test tells: musl's dynamic linker loads the glibc linux-x64 library as well, since it
// takes libc.so.6 and libpthread.so.0 to be itself and provides every name that the library
// imports, so the tests in Alpine x64 pass with either library.
[TestFixture]
public class LoadedLibraryTests
{
    private const string ExpectedRuntimeVariable = "LIBARCHIVE_NET_EXPECT_RID";

    // ARCHIVE_READ_MAGIC (archive_private.h), the first field of the archive object that
    // archive_read_new makes
    private const uint ArchiveReadMagic = 0xdeb0c5;

    [Test]
    public void LoadsTheLibraryOfTheExpectedRuntime()
    {
        var rid = Environment.GetEnvironmentVariable(ExpectedRuntimeVariable);
        if (string.IsNullOrEmpty(rid))
            Assert.Ignore($"{ExpectedRuntimeVariable} names no runtime whose library this process must load");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Assert.Fail($"{ExpectedRuntimeVariable} is set, to {rid}, but only Linux has /proc/self/maps, from which this test reads the library loaded");

        // The reader's handle is the archive object that LibArchive.Net's P/Invoke of
        // archive_read_new made. libarchive's struct archive starts with two 32-bit fields, its
        // magic number and its state, and then the address of its vtable, archive_read_vtable, a
        // constant of the library whose archive_read_new ran: so the file mapped there is the
        // library that the P/Invokes call, whatever other library the process has loaded.
        ulong vtable;
        using (var reader = new LibArchiveReader("7ztest.7z"))
        {
            var archive = reader.DangerousGetHandle();
            Assert.That(unchecked((uint)Marshal.ReadInt32(archive)), Is.EqualTo(ArchiveReadMagic),
                "The reader's handle does not hold an archive object of archive_read_new");
            vtable = IntPtr.Size == 8 ? unchecked((ulong)Marshal.ReadInt64(archive, 8)) : unchecked((uint)Marshal.ReadInt32(archive, 8));
        }

        var mappings = File.ReadAllLines("/proc/self/maps").Select(ParseMapping).OfType<Mapping>().ToList();
        var called = mappings.Where(mapping => mapping.Start <= vtable && vtable < mapping.End).Select(mapping => mapping.Path).ToList();
        Assert.That(called, Has.Count.EqualTo(1), $"The files mapped at the vtable's address, 0x{vtable:x}: {string.Join(", ", called)}");
        Assert.That(called[0], Does.EndWith($"/runtimes/{rid}/native/libarchive.so"));

        // Nor may the process map another file of that name: the P/Invokes would then call one
        // library, while the exported functions that it calls itself might be the other's.
        var loaded = mappings.Select(mapping => mapping.Path).Where(path => Path.GetFileName(path) == "libarchive.so").Distinct().ToList();
        Assert.That(loaded, Is.EqualTo(called), $"The files named libarchive.so that the process maps: {string.Join(", ", loaded)}");
    }

    private sealed record Mapping(ulong Start, ulong End, string Path);

    // A line of /proc/self/maps, for a file: its address range, then its permissions, offset,
    // device and inode, then the path of the file, which may itself hold spaces, after the padding.
    // An anonymous mapping has no path, and a pseudo-path such as [heap] does not start with '/'.
    private static Mapping? ParseMapping(string line)
    {
        var fields = line.Split(new[] { ' ' }, 6, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 6)
            return null;
        var path = fields[5].Trim();
        var range = fields[0].Split('-');
        if (!path.StartsWith("/", StringComparison.Ordinal) || range.Length != 2)
            return null;
        return new Mapping(ulong.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ulong.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture), path);
    }
}
