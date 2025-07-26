using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#if NETSTANDARD2_0
using System.Text;
#endif

namespace LibArchive.Net;

public class SafeStringBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    public IntPtr Ptr => handle;
    
    public SafeStringBuffer(string s) : base(true)
    {
        if (s is null)
            throw new ArgumentNullException(nameof(s));
#if !NETSTANDARD2_0
        handle = Marshal.StringToCoTaskMemUTF8(s);
#else
        // For .NET Standard 2.0, manually allocate and copy UTF8 bytes
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(s);
        handle = Marshal.AllocCoTaskMem(utf8Bytes.Length + 1); // +1 for null terminator
        Marshal.Copy(utf8Bytes, 0, handle, utf8Bytes.Length);
        Marshal.WriteByte(handle, utf8Bytes.Length, 0); // Null terminator
#endif
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeCoTaskMem(handle);
        return true;
    }
}