using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

public class SafeBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    public IntPtr Ptr => handle;

    public SafeBuffer(int size) : base(true)
    {
        handle = Marshal.AllocHGlobal(size);
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }
}