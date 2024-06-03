using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>
    /// Open the named archive for read access with the specified block size
    /// </summary>
    /// <param name="filename"></param>
    /// <param name="blockSize">Block size in bytes, default 1 MiB</param>
    /// <exception cref="ApplicationException"></exception>
    public LibArchiveReader(string filename,uint blockSize = 1<<20) : base(true)
    {
        using var uName = new SafeStringBuffer(filename);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);
        if (archive_read_open_filename(handle, uName.Ptr, (int)blockSize) != 0)
            Throw();
    }

    /// <summary>
    /// 
    /// </summary>
    public LibArchiveReader(string[] filenames,uint blockSize=1<<20) : base(true)
    {
        using var names = new DisposableStringArray(filenames);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);
        if (archive_read_open_filenames(handle, names.Ptr, (int)blockSize) != 0)
            Throw();
    }

    private void Throw()
    {
        throw new ApplicationException(Marshal.PtrToStringUTF8(archive_error_string(handle)));
    }

    protected override bool ReleaseHandle()
    {
        return archive_read_free(handle) == 0;
    }

    public IEnumerable<Entry> Entries()
    {
        int r;
        while ((r=archive_read_next_header(handle, out var entryHandle))==0)
        {
            var entry = Entry.Create(entryHandle, handle);
            if (entry is not null)
            {
                yield return entry;
            }
        }

        if (r != (int)ARCHIVE_RESULT.ARCHIVE_EOF)
            Throw();
    }

    public class Entry
    {
        protected readonly IntPtr entry;
        protected readonly IntPtr archive;

        public string Name { get; }
        public EntryType Type;
        public FileStream Stream => new(archive);

        public bool IsDirectory => Type == EntryType.Directory;
        public bool IsRegularFile => Type == EntryType.RegularFile;

        protected Entry(IntPtr entry, IntPtr archive)
        {
            this.entry = entry;
            this.archive = archive;
            Name = Marshal.PtrToStringUTF8(archive_entry_pathname(entry)) ?? throw new ApplicationException("Unable to retrieve entry's pathname");
            Type = (EntryType)archive_entry_filetype(entry);
        }

        internal static Entry? Create(IntPtr entry, IntPtr archive)
        {
            try
            {
                return new Entry(entry, archive);
            }
            catch (ApplicationException)
            {
                return null;
            }
        }
    }

    public enum EntryType
    {
        Directory = 0x4000,  // AE_IFDIR
        RegularFile = 0x8000 // AE_IFREG
    }

    public class FileStream : Stream
    {
        private readonly IntPtr archive;

        internal FileStream(IntPtr archive)
        {
            this.archive = archive;
        }
        
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return archive_read_data(archive, ref MemoryMarshal.GetReference(buffer.AsSpan()[offset..]), count);
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
    private static extern int archive_read_open_filenames(IntPtr a, IntPtr filename, int blocksize);

    [DllImport("archive")]
    private static extern int archive_read_data(IntPtr a, ref byte buff, int size);

    [DllImport("archive")]
    private static extern int archive_read_next_header(IntPtr a, out IntPtr entry);

    [DllImport("archive")]
    private static extern IntPtr archive_entry_pathname(IntPtr entry);

    [DllImport("archive")]
    private static extern int archive_entry_filetype(IntPtr entry);

    [DllImport("archive")]
    private static extern int archive_read_free(IntPtr a);

    [DllImport("archive")]
    private static extern IntPtr archive_error_string(IntPtr a);
}

public class DisposableStringArray : IDisposable
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