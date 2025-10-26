using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

/// <summary>
/// Represents a safe handle to an unmanaged memory buffer.
/// </summary>
public class SafeBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Gets a pointer to the unmanaged memory buffer.
    /// </summary>
    public IntPtr Ptr => handle;

    /// <summary>
    /// Initializes a new instance of <see cref="SafeBuffer"/> with the specified size.
    /// </summary>
    /// <param name="size">The size of the buffer in bytes.</param>
    public SafeBuffer(int size) : base(true)
    {
        handle = Marshal.AllocHGlobal(size);
    }

    /// <summary>
    /// Releases the unmanaged memory buffer.
    /// </summary>
    /// <returns>true if the handle is released successfully; otherwise, false.</returns>
    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }
}