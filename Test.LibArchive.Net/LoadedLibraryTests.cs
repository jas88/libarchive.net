using System.Runtime.InteropServices;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// Which native library this process loaded, where CI says which it must be: it sets
// LIBARCHIVE_NET_EXPECT_RID to the runtime whose library .NET must load, linux-x64 in the
// package job on Ubuntu and linux-musl-x64 in the test-alpine job. No other test tells: musl's
// dynamic linker loads the glibc linux-x64 library as well, since it takes libc.so.6 and
// libpthread.so.0 to be itself and provides every name that the library imports, so the tests
// in Alpine x64 pass with either library.
[TestFixture]
public class LoadedLibraryTests
{
    private const string ExpectedRuntimeVariable = "LIBARCHIVE_NET_EXPECT_RID";

    [Test]
    public void LoadsTheLibraryOfTheExpectedRuntime()
    {
        var rid = Environment.GetEnvironmentVariable(ExpectedRuntimeVariable);
        if (string.IsNullOrEmpty(rid))
            Assert.Ignore($"{ExpectedRuntimeVariable} names no runtime whose library this process must load");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Assert.Fail($"{ExpectedRuntimeVariable} is set, to {rid}, but only Linux has /proc/self/maps, from which this test reads the library loaded");

        // Loads the native library, unless a test before this one has
        using (new LibArchiveReader("7ztest.7z"))
        {
        }

        // Every file named libarchive.so that the process maps. The path, which may itself hold
        // spaces, is all that follows the inode and the padding after it.
        var loaded = File.ReadAllLines("/proc/self/maps")
            .Select(line => line.Split(new[] { ' ' }, 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 6)
            .Select(fields => fields[5].Trim())
            .Where(path => path.StartsWith("/", StringComparison.Ordinal) && Path.GetFileName(path) == "libarchive.so")
            .Distinct()
            .ToList();

        Assert.That(loaded, Has.Count.EqualTo(1), $"The files named libarchive.so that the process maps: {string.Join(", ", loaded)}");
        Assert.That(loaded[0], Does.EndWith($"/runtimes/{rid}/native/libarchive.so"));
    }
}
