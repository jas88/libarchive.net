using System;
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
        // Manual UTF-8 marshaling for .NET Standard 2.0
        handle = Marshal.StringToCoTaskMemAnsi(s);
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
}