using System;
using System.IO;

namespace LibArchive.Net;

/// <summary>
/// Keeps the copy of the native library that the .NET Standard 2.0 build puts in the application's
/// base directory, where Mono looks for it, the same as the library in runtimes/&lt;rid&gt;/native that
/// it loads (see LibArchiveReader.LoadUnixLibrary).
/// </summary>
internal static class NativeLibraryCopy
{
    private const int BufferSize = 81920;

    /// <summary>What <see cref="Refresh"/> did.</summary>
    internal enum Outcome
    {
        /// <summary>The copy was the same as the library already, and was left alone.</summary>
        UpToDate,

        /// <summary>The copy was made, or replaced with a new one, from the library.</summary>
        Copied,

        /// <summary>The copy could not be made; whatever was there is as it was.</summary>
        Failed,
    }

    /// <summary>
    /// Makes <paramref name="destination"/> a copy of <paramref name="source"/>, unless it already
    /// is one: a copy left by an earlier version of the package, or made in a process of the other C
    /// library, is replaced.
    /// </summary>
    /// <remarks>
    /// The library is copied to a new file in the destination's directory, which is then renamed
    /// over the destination, or moved there when there is none. So the destination is never written
    /// into: a process that has the old copy loaded keeps that file as it was, and no process finds a
    /// partial copy, even when two start at once. The destination's directory may be read-only, or
    /// the destination in use, so this never throws, and leaves no new file behind when it fails.
    /// </remarks>
    /// <param name="source">The library that the process loads.</param>
    /// <param name="destination">The copy, which need not exist.</param>
    /// <param name="error">What made the copy fail, for <see cref="Outcome.Failed"/>; otherwise
    /// <see langword="null"/>.</param>
    internal static Outcome Refresh(string source, string destination, out Exception? error)
    {
        error = null;
        string? temporary = null;
        try
        {
            if (File.Exists(destination) && SameContents(source, destination))
                return Outcome.UpToDate;

            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            temporary = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory,
                $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            File.Copy(source, temporary);

            if (File.Exists(destination))
            {
                // A rename, which replaces the file that the name links to rather than writing into it
                File.Replace(temporary, destination, null);
            }
            else
            {
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another process has put a copy there since: replace that one, as any other
                    File.Replace(temporary, destination, null);
                }
            }
            temporary = null;
            return Outcome.Copied;
        }
        catch (Exception ex)
        {
            error = ex;
            return Outcome.Failed;
        }
        finally
        {
            if (temporary != null)
                TryDelete(temporary);
        }
    }

    // Whether the two files hold the same bytes; false as well when either cannot be read, so that
    // a copy which cannot be read is replaced
    private static bool SameContents(string first, string second)
    {
        try
        {
            using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (a.Length != b.Length)
                return false;

            var bufferA = new byte[BufferSize];
            var bufferB = new byte[BufferSize];
            while (true)
            {
                var read = ReadFully(a, bufferA);
                if (ReadFully(b, bufferB) != read)
                    return false;
                if (read == 0)
                    return true;
                if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Reads until the buffer is full or the stream ends, and returns the number of bytes read
    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Left behind: it bears a name of its own, so nothing will take it for the library
        }
    }
}
