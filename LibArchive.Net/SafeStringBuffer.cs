using System;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

/// <summary>
/// Represents a safe handle to a UTF-8 encoded string in unmanaged memory.
/// </summary>
public class SafeStringBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Gets a pointer to the UTF-8 encoded string in unmanaged memory.
    /// </summary>
    public IntPtr Ptr => handle;

    /// <summary>
    /// Initializes a new instance of <see cref="SafeStringBuffer"/> from a managed string.
    /// </summary>
    /// <param name="s">The string to marshal to UTF-8 in unmanaged memory.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="s"/> is null.</exception>
    public SafeStringBuffer(string s) : base(true)
    {
        if (s is null)
            throw new ArgumentNullException(nameof(s));

#if NETSTANDARD2_0
        // Manual UTF-8 marshaling for .NET Standard 2.0, which has no StringToCoTaskMemUTF8.
        // (StringToCoTaskMemAnsi is UTF-8 on Unix but the ANSI code page on Windows, which
        // replaces every character outside that code page with '?'.)
        var bytes = Encoding.UTF8.GetBytes(s);
        handle = Marshal.AllocCoTaskMem(bytes.Length + 1);
        Marshal.Copy(bytes, 0, handle, bytes.Length);
        Marshal.WriteByte(handle, bytes.Length, 0);
#else
        // Use built-in UTF-8 marshaling for modern .NET
        handle = Marshal.StringToCoTaskMemUTF8(s);
#endif
    }

    /// <summary>
    /// Releases the unmanaged string buffer.
    /// </summary>
    /// <returns>true if the handle is released successfully; otherwise, false.</returns>
    protected override bool ReleaseHandle()
    {
        Marshal.FreeCoTaskMem(handle);
        return true;
    }

    /// <summary>
    /// Reads a NUL-terminated UTF-8 string, such as one that libarchive returns, from unmanaged
    /// memory, or returns null for a null pointer. A byte sequence that is not valid UTF-8 becomes
    /// U+FFFD.
    /// </summary>
    /// <remarks>
    /// libarchive's narrow strings are UTF-8 on every platform (native/libarchive-narrow-utf8.patch),
    /// so they are not read with <c>Marshal.PtrToStringAnsi</c>, which takes the ANSI code page on
    /// Windows, or <c>Marshal.PtrToStringAuto</c>, which reads UTF-16 there. .NET Standard 2.0 has no
    /// <c>Marshal.PtrToStringUTF8</c>, so there the terminator is found one byte at a time and the
    /// bytes are decoded in place: a vectorised search, such as IndexOf over a span longer than the
    /// string, could read past the terminator into memory that is not mapped.
    /// </remarks>
    internal static string? PtrToStringUTF8(IntPtr ptr)
    {
#if NETSTANDARD2_0
        if (ptr == IntPtr.Zero)
            return null;
        unsafe
        {
            var start = (byte*)ptr;
            var end = start;
            while (*end != 0)
                end++;
            return Encoding.UTF8.GetString(start, checked((int)(end - start)));
        }
#else
        return Marshal.PtrToStringUTF8(ptr);
#endif
    }

    /// <summary>
    /// Reads libarchive's message for a call that did not succeed, or, when libarchive set none,
    /// says which result the call returned, as in "libarchive returned ARCHIVE_FATAL without a
    /// message".
    /// </summary>
    /// <param name="errorString">What archive_error_string returned after the call.</param>
    /// <param name="result">What the call returned, such as ARCHIVE_WARN or ARCHIVE_FATAL.</param>
    /// <remarks>
    /// libarchive returns some warnings and errors without a message: the tar reader returns
    /// ARCHIVE_WARN alone when it ignores a pax path record longer than 1 MiB
    /// (archive_read_support_format_tar.c:2972-2980), and the writer returns ARCHIVE_FATAL alone
    /// when the callback that writes to a stream fails (archive_write.c:430-439).
    /// </remarks>
    internal static string ErrorMessage(IntPtr errorString, int result) =>
        PtrToStringUTF8(errorString) ?? $"libarchive returned {ResultName(result)} without a message";

    // The name that archive.h gives a result, or its number if it has none
    private static string ResultName(int result) => result switch
    {
        0 => "ARCHIVE_OK",
        1 => "ARCHIVE_EOF",
        -10 => "ARCHIVE_RETRY",
        -20 => "ARCHIVE_WARN",
        -25 => "ARCHIVE_FAILED",
        -30 => "ARCHIVE_FATAL",
        _ => "result " + result.ToString(CultureInfo.InvariantCulture),
    };
}
