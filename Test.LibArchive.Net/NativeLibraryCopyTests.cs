using System.Runtime.InteropServices;
using LibArchive.Net;
using NUnit.Framework;

namespace Test.LibArchive.Net;

// NativeLibraryCopy keeps the copy of the native library that the .NET Standard 2.0 build puts in the
// application's base directory, for Mono, the same as the library in runtimes/<rid>/native that it
// loads. It is internal, and the tests otherwise use the public API alone, so this project compiles
// its source in (see Test.LibArchive.Net.csproj). Each test has a directory of its own, the base
// directory, with the library in native/ below it.
[TestFixture]
public class NativeLibraryCopyTests
{
    private const string Name = "libarchive.so";

    private string directory = null!;
    private string source = null!;
    private string destination = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "NativeLibraryCopyTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        source = Path.Combine(directory, "native", Name);
        destination = Path.Combine(directory, Name);
        File.WriteAllBytes(source, Bytes(200_000, 1));
    }

    [TearDown]
    public void TearDown()
    {
#if NET
        // Writable again, in case a test failed before it could undo its change
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
        Directory.Delete(directory, recursive: true);
    }

    [Test]
    public void CopiesTheLibraryWhenThereIsNoCopy()
    {
        Assert.That(NativeLibraryCopy.Refresh(source, destination, out var error), Is.EqualTo(NativeLibraryCopy.Outcome.Copied));
        Assert.That(error, Is.Null);
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(File.ReadAllBytes(source)));
        Assert.That(FilesInDirectory(), Is.EqualTo(new[] { Name }), "No temporary file is left");
    }

    // A copy left by an earlier version of the package, or made in a process of the other C library,
    // whether or not its length is the library's
    [TestCase(200_000)]
    [TestCase(150_000)]
    public void ReplacesACopyThatDiffers(int length)
    {
        File.WriteAllBytes(destination, Bytes(length, 2));

        Assert.That(NativeLibraryCopy.Refresh(source, destination, out var error), Is.EqualTo(NativeLibraryCopy.Outcome.Copied));
        Assert.That(error, Is.Null);
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(File.ReadAllBytes(source)));
        Assert.That(FilesInDirectory(), Is.EqualTo(new[] { Name }), "No temporary file is left");
    }

    // Left alone: a copy made again would have the library's time, not the one set here
    [Test]
    public void LeavesAnIdenticalCopyAlone()
    {
        File.Copy(source, destination);
        var time = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(destination, time);

        Assert.That(NativeLibraryCopy.Refresh(source, destination, out var error), Is.EqualTo(NativeLibraryCopy.Outcome.UpToDate));
        Assert.That(error, Is.Null);
        Assert.That(File.GetLastWriteTimeUtc(destination), Is.EqualTo(time));
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(File.ReadAllBytes(source)));
    }

    // A process that has the old copy loaded keeps the file that it mapped as it was: the new copy
    // takes its name, and the old file, open here as that process has it mapped, is not written into
    [Test]
    public void ReplacesACopyInUseWithoutWritingIntoIt()
    {
        IgnoreOnWindows();
        var old = Bytes(200_000, 2);
        File.WriteAllBytes(destination, old);

        using var inUse = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.That(NativeLibraryCopy.Refresh(source, destination, out var error), Is.EqualTo(NativeLibraryCopy.Outcome.Copied));
        Assert.That(error, Is.Null);
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(File.ReadAllBytes(source)));

        var seen = new byte[old.Length + 1];
        var read = 0;
        int n;
        while ((n = inUse.Read(seen, read, seen.Length - read)) > 0)
            read += n;
        Assert.That(seen.Take(read), Is.EqualTo(old), "The file that was open still holds the old copy");
    }

    [Test]
    public void FailsWithoutThrowingWhenTheLibraryIsMissing([Values] bool copyExists)
    {
        var old = Bytes(1_000, 2);
        if (copyExists)
            File.WriteAllBytes(destination, old);
        File.Delete(source);

        Assert.That(NativeLibraryCopy.Refresh(source, destination, out var error), Is.EqualTo(NativeLibraryCopy.Outcome.Failed));
        Assert.That(error, Is.InstanceOf<IOException>());
        Assert.That(FilesInDirectory(), Is.EqualTo(copyExists ? new[] { Name } : Array.Empty<string>()), "No temporary file is left");
        if (copyExists)
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(old));
    }

    // Where the application may not write, the copy stays as it was, whether there is one or not
    [Test]
    public void FailsWithoutThrowingWhenTheDirectoryIsReadOnly([Values] bool copyExists)
    {
#if NET
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore(WindowsIgnored);
            return;
        }
        if (Environment.IsPrivilegedProcess)
            Assert.Ignore("A privileged process may write into a read-only directory");

        var old = Bytes(1_000, 2);
        if (copyExists)
            File.WriteAllBytes(destination, old);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.That(NativeLibraryCopy.Refresh(source, destination, out var error), Is.EqualTo(NativeLibraryCopy.Outcome.Failed));
            Assert.That(error, Is.InstanceOf<UnauthorizedAccessException>().Or.InstanceOf<IOException>());
            Assert.That(FilesInDirectory(), Is.EqualTo(copyExists ? new[] { Name } : Array.Empty<string>()), "No temporary file is left");
            if (copyExists)
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(old));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#else
        Assert.Ignore("Making the directory read-only needs File.SetUnixFileMode, of .NET 7 and later");
#endif
    }

    // Processes that start at once, here threads, each make a copy of their own and rename it into
    // place: every one succeeds, and what is left is one whole copy
    [Test]
    public void ConcurrentRefreshesLeaveOneWholeCopy([Values] bool copyExists)
    {
        IgnoreOnWindows();
        if (copyExists)
            File.WriteAllBytes(destination, Bytes(150_000, 2));

        const int count = 8;
        var outcomes = new NativeLibraryCopy.Outcome[count];
        var errors = new Exception?[count];
        using var start = new Barrier(count);
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            outcomes[i] = NativeLibraryCopy.Refresh(source, destination, out errors[i]);
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Assert.That(errors, Is.All.Null);
        Assert.That(outcomes, Is.All.Not.EqualTo(NativeLibraryCopy.Outcome.Failed));
        Assert.That(outcomes, Has.Some.EqualTo(NativeLibraryCopy.Outcome.Copied));
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(File.ReadAllBytes(source)));
        Assert.That(FilesInDirectory(), Is.EqualTo(new[] { Name }), "No temporary file is left");
    }

    // NativeLibraryCopy runs on Linux and macOS alone, whose renames these tests rely on
    private const string WindowsIgnored = "The copy is made on Linux and macOS only";

    private static void IgnoreOnWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.Ignore(WindowsIgnored);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // The names of the files in the base directory, which holds the copy and any temporary file
    private string[] FilesInDirectory()
        => Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
