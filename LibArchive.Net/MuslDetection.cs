using System;
using System.IO;
using System.Security;

namespace LibArchive.Net;

/// <summary>
/// Tells a Linux process that runs on musl from one that runs on glibc, for the .NET Standard 2.0
/// build, which finds its native library itself: the package has one for each C library, in
/// runtimes/linux-musl-&lt;arch&gt;/native and runtimes/linux-&lt;arch&gt;/native. (.NET 6 and later
/// choose between the two from the RID graph.)
/// </summary>
/// <remarks>
/// A musl process has musl's dynamic linker, which is also its C library, mapped: every dynamically
/// linked musl executable, the dotnet and mono hosts among them, names it as its interpreter, so the
/// kernel maps it before the process runs at all. A glibc process never maps it. Whether the file
/// exists says nothing, since Debian's musl package installs one on glibc systems.
/// </remarks>
internal static class MuslDetection
{
    // The file name of musl's dynamic linker where /lib/ld-musl-<arch>.so.1 is the file itself, as on
    // Alpine: ld-musl-x86_64.so.1, ld-musl-aarch64.so.1 or ld-musl-armhf.so.1. Alpine's other name
    // for it, libc.musl-<arch>.so.1, never appears in the maps: it is a symbolic link, and the maps
    // name the file mapped, with symbolic links resolved. Nor is it ever opened, since musl's dynamic
    // linker takes a library named libc.* to be itself.
    private const string DynamicLinkerPrefix = "ld-musl-";

    // The file name of musl's dynamic linker where /lib/ld-musl-<arch>.so.1 is a symbolic link to
    // <libdir>/libc.so, as an upstream make install of musl leaves it, and as on Void Linux. A glibc
    // process never maps a file of that exact name: glibc's C library is libc.so.6, or libc-2.<n>.so
    // before glibc 2.34, and glibc's own <libdir>/libc.so is a linker script, which is never mapped.
    private const string LibcFileName = "libc.so";

    // What the kernel appends to the path of a file deleted since it was mapped, as musl's is when it
    // is upgraded under a running process
    private const string DeletedSuffix = " (deleted)";

    private static readonly char[] FieldSeparator = { ' ' };

    /// <summary>
    /// Whether this process runs on musl, from the mappings that /proc/self/maps lists: see
    /// <see cref="IsMuslProcess(string)"/>.
    /// </summary>
    /// <returns><see langword="true"/> for musl; <see langword="false"/> for glibc, and when
    /// /proc/self/maps cannot be read, so that such a process loads the glibc library, as every Linux
    /// process did before musl had a library of its own.</returns>
    internal static bool IsMuslProcess()
    {
        string maps;
        try
        {
            maps = File.ReadAllText("/proc/self/maps");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
        {
            // /proc is not mounted, or this process may not read it
            return false;
        }
        return IsMuslProcess(maps);
    }

    /// <summary>
    /// Whether the process with the memory map <paramref name="maps"/> runs on musl: whether it maps
    /// musl's dynamic linker, a file whose name starts with <c>ld-musl-</c> or is <c>libc.so</c>.
    /// </summary>
    /// <param name="maps">The text of /proc/&lt;pid&gt;/maps: a line for each mapping, of its address
    /// range, permissions, offset, device and inode, then the path of the file it maps, if any.</param>
    internal static bool IsMuslProcess(string maps)
    {
        foreach (var line in maps.Split('\n'))
        {
            // The path, which may itself hold spaces, is all that follows the inode and the padding
            // after it. An anonymous mapping has none, and a pseudo-path such as [heap], [vdso] or
            // [anon:name] does not start with '/'.
            var fields = line.Split(FieldSeparator, 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
                continue;
            var path = fields[5].Trim();
            if (path.Length == 0 || path[0] != '/')
                continue;
            if (path.EndsWith(DeletedSuffix, StringComparison.Ordinal))
                path = path.Substring(0, path.Length - DeletedSuffix.Length);

            // The file name alone: a directory, such as /home/musl, does not make a process musl's
            var name = path.Substring(path.LastIndexOf('/') + 1);
            if (name.StartsWith(DynamicLinkerPrefix, StringComparison.Ordinal) || name == LibcFileName)
                return true;
        }
        return false;
    }
}
