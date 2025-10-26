using System;
using System.Text;
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

#if NETSTANDARD2_0
        // Manual UTF-8 marshaling for .NET Standard 2.0
        handle = Marshal.StringToCoTaskMemAnsi(s);
#else
        // Use built-in UTF-8 marshaling for modern .NET
        handle = Marshal.StringToCoTaskMemUTF8(s);
#endif
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeCoTaskMem(handle);
        return true;
    }
}