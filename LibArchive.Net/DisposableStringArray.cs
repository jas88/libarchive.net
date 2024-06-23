using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace LibArchive.Net;

public sealed class DisposableStringArray : IDisposable
{
    private readonly IntPtr[] backing;
    private readonly GCHandle handle;
    private readonly SafeStringBuffer[] strings;

    public DisposableStringArray(string[] a)
    {
        backing = new IntPtr[a.Length+1];
        strings = a.Select(s => new SafeStringBuffer(s)).ToArray();
        for (int i=0;i<strings.Length;i++)
            backing[i] = strings[i].Ptr;
        backing[strings.Length] = IntPtr.Zero;
        handle = GCHandle.Alloc(backing, GCHandleType.Pinned);
    }

    public IntPtr Ptr => handle.AddrOfPinnedObject();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        handle.Free();
        foreach (var s in strings)
            s.Dispose();
    }
}