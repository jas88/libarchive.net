using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

public sealed class SafeStringBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    public IntPtr Ptr => handle;

    public SafeStringBuffer(string s) : base(true)
    {
        //  can't use ThrowIfNull on netstandard2.0
        // ReSharper disable once UseThrowIfNullMethod
        if (s == null)
            throw new ArgumentNullException(nameof(s));

        handle = Marshal.StringToCoTaskMemUTF8(s);
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeCoTaskMem(handle);
        return true;
    }
}