﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
namespace LibArchive.Net;

public class LibArchiveReader : SafeHandleZeroOrMinusOneIsInvalid
{
    private enum ARCHIVE_RESULT
    {
        ARCHIVE_OK = 0,
        ARCHIVE_EOF = 1,
        ARCHIVE_RETRY=-10,
        ARCHIVE_WARN=-20,
        ARCHIVE_FAILED=-25,
        ARCHIVE_FATAL=-30
    }

    static LibArchiveReader()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibArchiveReader).Assembly,
            (name, asm, path) =>
            {
                // Currently supported: Linux+Win+OSX on x64, OSX only on arm64
                if (RuntimeInformation.ProcessArchitecture != Architecture.X64 &&
                    (RuntimeInformation.ProcessArchitecture != Architecture.Arm64 ||
                     !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
                    throw new PlatformNotSupportedException();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return NativeLibrary.Load($"{AppDomain.CurrentDomain.BaseDirectory}runtimes/linux-x64/libarchive.so");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return NativeLibrary.Load($"{AppDomain.CurrentDomain.BaseDirectory}runtimes/osx-any64/libarchive.dylib");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return NativeLibrary.Load($"{AppDomain.CurrentDomain.BaseDirectory}runtimes/win-x64/archive.dll");
                throw new PlatformNotSupportedException();
            });
    }
    public LibArchiveReader(string filename) : base(true)
    {
        using var uName = new SafeStringBuffer(filename);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);
        if (archive_read_open_filename(handle, uName.Ptr, 16384) != 0)
            throw new ApplicationException("TODO: Archive open failed");
    }

    private void Throw()
    {
        throw new ApplicationException($"{Marshal.PtrToStringUTF8(archive_error_string(handle))}");
    }

    protected override bool ReleaseHandle()
    {
        return archive_read_free(handle) == 0;
    }

    public IEnumerable<Entry> Entries()
    {
        int r;
        while ((r=archive_read_next_header(handle, out var entry))==0)
        {
            var name = Marshal.PtrToStringUTF8(archive_entry_pathname(entry));
            if (name is not null)
                yield return new Entry(name, handle);
        }

        if (r != (int)ARCHIVE_RESULT.ARCHIVE_EOF)
            Throw();
    }

    public class Entry
    {
        public string Name { get; }
        private readonly IntPtr handle;
        public FileStream Stream => new FileStream(handle);

        internal Entry(string name, IntPtr handle)
        {
            this.Name = name;
            this.handle = handle;
        }
    }

    public class FileStream : Stream
    {
        private readonly IntPtr _archive;
        internal FileStream(IntPtr archive)
        {
            this._archive = archive;
        }
        
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return archive_read_data(_archive, ref MemoryMarshal.GetReference(buffer.AsSpan()[offset..]), count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    [DllImport("archive")]
    private static extern IntPtr archive_read_new();

    [DllImport("archive")]
    private static extern void archive_read_support_filter_all(IntPtr a);
    
    [DllImport("archive")]
    private static extern void archive_read_support_format_all(IntPtr a);

    [DllImport("archive")]
    private static extern int archive_read_open_filename(IntPtr a, IntPtr filename, int blocksize);

    [DllImport("archive")]
    private static extern int archive_read_data(IntPtr a, ref byte buff, int size);

    [DllImport("archive")]
    private static extern int archive_read_next_header(IntPtr a, out IntPtr entry);

    [DllImport("archive")]
    private static extern IntPtr archive_entry_pathname(IntPtr entry);

    [DllImport("archive")]
    private static extern int archive_read_free(IntPtr a);

    [DllImport("archive")]
    private static extern IntPtr archive_error_string(IntPtr a);
}
