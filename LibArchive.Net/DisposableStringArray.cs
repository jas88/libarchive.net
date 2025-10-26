using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace LibArchive.Net;

/// <summary>
/// Represents a disposable array of UTF-8 encoded strings for P/Invoke interop.
/// </summary>
public sealed class DisposableStringArray : IDisposable
{
    private readonly IntPtr[] backing;
    private readonly GCHandle handle;
    private readonly SafeStringBuffer[] strings;

    /// <summary>
    /// Initializes a new instance of <see cref="DisposableStringArray"/> from a string array.
    /// </summary>
    /// <param name="a">The array of strings to marshal.</param>
    public DisposableStringArray(string[] a)
    {
        backing = new IntPtr[a.Length+1];
        strings = a.Select(s => new SafeStringBuffer(s)).ToArray();
        for (int i=0;i<strings.Length;i++)
            backing[i] = strings[i].Ptr;
        backing[strings.Length] = IntPtr.Zero;
        handle = GCHandle.Alloc(backing, GCHandleType.Pinned);
    }

    /// <summary>
    /// Gets a pointer to the pinned array of string pointers.
    /// </summary>
    public IntPtr Ptr => handle.AddrOfPinnedObject();

    /// <summary>
    /// Releases all resources used by this instance.
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        handle.Free();
        foreach (var s in strings)
            s.Dispose();
    }
}