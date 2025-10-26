using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
#if NETSTANDARD2_0
using System.Text;
#endif

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
#if NETSTANDARD2_0
        // .NET Standard 2.0 doesn't have automatic RID resolution, so we need manual loading
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 &&
            (RuntimeInformation.ProcessArchitecture != Architecture.Arm64 ||
             !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
            throw new PlatformNotSupportedException();

        PreloadNativeLibrary();
#endif
        // For .NET 6+: Automatic resolution from runtimes/{rid}/native/ handles everything!
        // No custom DllImportResolver needed - the runtime finds libraries automatically.
    }

    // Compatibility method for UTF-8 string marshaling
    private static string? PtrToStringUTF8(IntPtr ptr)
    {
#if NETSTANDARD2_0
        // Use the custom implementation for .NET Standard 2.0
        return PtrToStringUTF8Internal(ptr);
#else
        // Use the built-in method for modern .NET
        return Marshal.PtrToStringUTF8(ptr);
#endif
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
        throw new ApplicationException(PtrToStringUTF8(archive_error_string(handle)) ?? "Unknown error");
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
        protected readonly IntPtr entryHandle;
        protected readonly IntPtr archiveHandle;

        public string Name { get; }
        public EntryType Type;
        public FileStream Stream => new(archiveHandle);

        public bool IsDirectory => Type == EntryType.Directory;
        public bool IsRegularFile => Type == EntryType.RegularFile;

        protected Entry(IntPtr entryHandle, IntPtr archiveHandle)
        {
            this.entryHandle = entryHandle;
            this.archiveHandle = archiveHandle;
            Name = PtrToStringUTF8(archive_entry_pathname(entryHandle)) ?? throw new ApplicationException("Unable to retrieve entry's pathname");
            Type = (EntryType)archive_entry_filetype(entryHandle);
        }

        internal static Entry? Create(IntPtr entryHandle, IntPtr archiveHandle)
        {
            try
            {
                return new Entry(entryHandle, archiveHandle);
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
        private readonly IntPtr archiveHandle;

        internal FileStream(IntPtr archiveHandle)
        {
            this.archiveHandle = archiveHandle;
        }
        
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
#if NETSTANDARD2_0
            // Use traditional array slicing for .NET Standard 2.0
            unsafe
            {
                fixed (byte* ptr = &buffer[offset])
                {
                    return archive_read_data(archiveHandle, ref *ptr, count);
                }
            }
#else
            // Use modern range syntax for .NET 6.0+
            return archive_read_data(archiveHandle, ref MemoryMarshal.GetReference(buffer.AsSpan()[offset..]), count);
#endif
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

    // P/Invoke declarations for libarchive
    // Use LibraryImport for .NET 7+ (AOT-friendly, source-generated)
    // Use DllImport for older frameworks (runtime marshalling)
#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial IntPtr archive_read_new();

    [LibraryImport("archive")]
    private static partial void archive_read_support_filter_all(IntPtr a);

    [LibraryImport("archive")]
    private static partial void archive_read_support_format_all(IntPtr a);

    [LibraryImport("archive")]
    private static partial int archive_read_open_filename(IntPtr a, IntPtr filename, int blocksize);

    [LibraryImport("archive")]
    private static partial int archive_read_open_filenames(IntPtr a, IntPtr filename, int blocksize);

    [LibraryImport("archive")]
    private static partial int archive_read_data(IntPtr a, ref byte buff, int size);

    [LibraryImport("archive")]
    private static partial int archive_read_next_header(IntPtr a, out IntPtr entry);

    [LibraryImport("archive")]
    private static partial IntPtr archive_entry_pathname(IntPtr entry);

    [LibraryImport("archive")]
    private static partial int archive_entry_filetype(IntPtr entry);

    [LibraryImport("archive")]
    private static partial int archive_read_free(IntPtr a);

    [LibraryImport("archive")]
    private static partial IntPtr archive_error_string(IntPtr a);
#else
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
#endif

#if NETSTANDARD2_0
    // .NET Standard 2.0 Native Library Loading
    private static void PreloadNativeLibrary()
    {
        var libraryPath = GetNativeLibraryPath();

        if (!File.Exists(libraryPath))
        {
            throw new DllNotFoundException($"Native library not found at: {libraryPath}");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            LoadWindowsLibrary(libraryPath);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            LoadUnixLibrary(libraryPath);
        }
    }

    private static string GetNativeLibraryPath()
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var assemblyDir = Path.GetDirectoryName(assemblyLocation)
                         ?? throw new InvalidOperationException("Could not determine assembly directory");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException($"Unsupported Windows architecture: {RuntimeInformation.ProcessArchitecture}")
            };
            return Path.Combine(assemblyDir, "runtimes", arch, "native", "archive.dll");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Path.Combine(assemblyDir, "runtimes", "linux-x64", "native", "libarchive.so");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return Path.Combine(assemblyDir, "runtimes", arch, "native", "libarchive.dylib");
        }
        else
        {
            throw new PlatformNotSupportedException($"Unsupported platform: {RuntimeInformation.OSDescription}");
        }
    }

    private static void LoadWindowsLibrary(string libraryPath)
    {
        if (!File.Exists(libraryPath))
        {
            throw new DllNotFoundException($"Native library not found at: {libraryPath}");
        }

        var handle = LoadLibrary(libraryPath);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new DllNotFoundException($"Failed to load native library '{libraryPath}'. Win32 error: {error}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static void LoadUnixLibrary(string libraryPath)
    {
        // On .NET Framework (Mono), we need to explicitly load the library using dlopen
        // RTLD_NOW = 2: Resolve all symbols immediately
        const int RTLD_NOW = 2;
        var handle = dlopen(libraryPath, RTLD_NOW);
        if (handle == IntPtr.Zero)
        {
            var error = dlerror();
            throw new DllNotFoundException($"Failed to load native library '{libraryPath}'. Error: {error}");
        }
    }

    // Linux: try multiple libdl versions for compatibility
    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen_linux_v2(string filename, int flags);

    [DllImport("libdl.so", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen_linux(string filename, int flags);

    [DllImport("libdl.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr dlopen_macos(string filename, int flags);

    private static IntPtr dlopen(string filename, int flags)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Try versioned first, fall back to unversioned
            try { return dlopen_linux_v2(filename, flags); }
            catch (DllNotFoundException) { return dlopen_linux(filename, flags); }
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return dlopen_macos(filename, flags);
        throw new PlatformNotSupportedException();
    }

    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr dlerror_linux_v2();

    [DllImport("libdl.so", EntryPoint = "dlerror")]
    private static extern IntPtr dlerror_linux();

    [DllImport("libdl.dylib", EntryPoint = "dlerror")]
    private static extern IntPtr dlerror_macos();

    private static string? dlerror()
    {
        IntPtr ptr;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try { ptr = dlerror_linux_v2(); }
            catch (DllNotFoundException) { ptr = dlerror_linux(); }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            ptr = dlerror_macos();
        else
            return "Unknown platform";

        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }

    // .NET Standard 2.0 UTF-8 Marshaling Support
    private static string? PtrToStringUTF8Internal(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            // Find the null terminator to determine string length
            var length = 0;
            unsafe
            {
                byte* p = (byte*)ptr;
                while (p[length] != 0)
                {
                    length++;

                    // Prevent infinite loop on corrupted data
                    if (length > 1000000) // 1MB limit for safety
                        throw new InvalidOperationException("UTF-8 string too long or not null-terminated");
                }
            }

            if (length == 0)
                return string.Empty;

            // Copy the UTF-8 bytes and convert to string
            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // Fallback to basic ASCII conversion if UTF-8 fails
            try
            {
                return Marshal.PtrToStringAnsi(ptr);
            }
            catch
            {
                throw new InvalidOperationException($"Failed to marshal UTF-8 string: {ex.Message}", ex);
            }
        }
    }
#endif
}