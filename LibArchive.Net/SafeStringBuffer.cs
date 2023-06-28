using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

public class SafeStringBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    public IntPtr Ptr => handle;
    
    public SafeStringBuffer(string s) : base(true)
    {
        if (s is null)
            throw new ArgumentNullException(nameof(s));
        handle = Marshal.StringToCoTaskMemUTF8(s);
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeCoTaskMem(handle);
        return true;
    }
}