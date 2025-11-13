using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
#if NETSTANDARD2_0
using System.Text;
#endif

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
namespace LibArchive.Net;

/// <summary>
/// Provides read-only access to archive files using the libarchive library.
/// Supports various formats including zip, rar, 7zip, tar, gzip, bzip2, lzo, and lzma.
/// </summary>
public partial class LibArchiveReader : SafeHandleZeroOrMinusOneIsInvalid
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

    // Fields to store constructor parameters for Reset() support
    private readonly string? _filename;
    private readonly string[]? _filenames;
    private uint _blockSize;
    private string? _password;

    private enum SourceType { File, MultiVolume, Stream }
    private readonly SourceType _sourceType;

    static LibArchiveReader()
    {
#if NETSTANDARD2_0
        // .NET Standard 2.0 doesn't have automatic RID resolution, so we need manual loading
        // Supported platforms:
        // - Windows: x64, x86, ARM64
        // - Linux: x64, ARM, ARM64
        // - macOS: x64, ARM64
        var arch = RuntimeInformation.ProcessArchitecture;
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        var supported = (isWindows && (arch == Architecture.X64 || arch == Architecture.X86 || arch == Architecture.Arm64)) ||
                       (isLinux && (arch == Architecture.X64 || arch == Architecture.Arm || arch == Architecture.Arm64)) ||
                       (isMacOS && (arch == Architecture.X64 || arch == Architecture.Arm64));

        if (!supported)
            throw new PlatformNotSupportedException($"Unsupported platform/architecture: {RuntimeInformation.OSDescription} / {arch}");

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
    /// Opens the specified archive file for read access with the specified block size.
    /// </summary>
    /// <param name="filename">The path to the archive file.</param>
    /// <param name="blockSize">Block size in bytes for reading, default is 1 MiB (1048576 bytes).</param>
    /// <param name="password">Optional password for encrypted archives. Supports ZIP archives with traditional PKWARE or AES encryption. RAR and 7z encrypted archives are not supported by libarchive.</param>
    /// <exception cref="ApplicationException">Thrown when the archive cannot be opened.</exception>
    /// <exception cref="NotSupportedException">Thrown when attempting to read an encrypted RAR or 7z archive (not supported by libarchive).</exception>
    public LibArchiveReader(string filename, uint blockSize = 1<<20, string? password = null) : base(true)
    {
        _filename = filename;
        _blockSize = blockSize;
        _password = password;
        _sourceType = SourceType.File;

        using var uName = new SafeStringBuffer(filename);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);

        // Add password if provided
        if (password != null)
        {
            using var uPassword = new SafeStringBuffer(password);
            if (archive_read_add_passphrase(handle, uPassword.Ptr) != 0)
                Throw();
        }

        if (archive_read_open_filename(handle, uName.Ptr, (int)blockSize) != 0)
            Throw();
    }

    /// <summary>
    /// Opens a multi-volume archive for read access with the specified block size.
    /// All volume files must be in the same directory.
    /// </summary>
    /// <param name="filenames">The paths to all volume files of the archive.</param>
    /// <param name="blockSize">Block size in bytes for reading, default is 1 MiB (1048576 bytes).</param>
    /// <param name="password">Optional password for encrypted archives. Supports ZIP archives with traditional PKWARE or AES encryption. RAR and 7z encrypted archives are not supported by libarchive.</param>
    /// <exception cref="ApplicationException">Thrown when the archive cannot be opened.</exception>
    /// <exception cref="NotSupportedException">Thrown when attempting to read an encrypted RAR or 7z archive (not supported by libarchive).</exception>
    public LibArchiveReader(string[] filenames, uint blockSize=1<<20, string? password = null) : base(true)
    {
        _filenames = filenames;
        _blockSize = blockSize;
        _password = password;
        _sourceType = SourceType.MultiVolume;

        using var names = new DisposableStringArray(filenames);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);

        // Add password if provided
        if (password != null)
        {
            using var uPassword = new SafeStringBuffer(password);
            if (archive_read_add_passphrase(handle, uPassword.Ptr) != 0)
                Throw();
        }

        if (archive_read_open_filenames(handle, names.Ptr, (int)blockSize) != 0)
            Throw();
    }

    // Partial method for cleanup - implemented in LibArchiveReader.Stream.cs
    partial void CleanupCallbacks();

    private void Throw()
    {
        throw new ApplicationException(PtrToStringUTF8(archive_error_string(handle)) ?? "Unknown error");
    }

    /// <summary>
    /// Checks if the archive contains encrypted entries.
    /// </summary>
    /// <returns>
    /// 1 if at least one entry is encrypted,
    /// 0 if no entries are encrypted,
    /// negative value on error.
    /// </returns>
    public int HasEncryptedEntries()
    {
        return archive_read_has_encrypted_entries(handle);
    }

    /// <summary>
    /// Releases the archive handle.
    /// </summary>
    /// <returns>true if the handle is released successfully; otherwise, false.</returns>
    protected override bool ReleaseHandle()
    {
        CleanupCallbacks();
        return archive_read_free(handle) == 0;
    }

    /// <summary>
    /// Enumerates all entries in the archive.
    /// </summary>
    /// <returns>An enumerable collection of archive entries.</returns>
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

    /// <summary>
    /// Resets the archive reader to the beginning, allowing entries to be enumerated again.
    /// </summary>
    /// <remarks>
    /// For file-based archives, this closes and reopens the file.
    /// For stream-based archives, the stream must be seekable (CanSeek = true).
    /// </remarks>
    /// <exception cref="NotSupportedException">Thrown when the archive source does not support reset (e.g., non-seekable stream).</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the reader has been disposed.</exception>
    public void Reset()
    {
        if (IsClosed || IsInvalid)
            throw new ObjectDisposedException(nameof(LibArchiveReader));

        // Close current handle
        archive_read_free(handle);
        handle = IntPtr.Zero;
        CleanupCallbacks();

        // Reopen based on source type
        switch (_sourceType)
        {
            case SourceType.File:
                ReopenFile();
                break;
            case SourceType.MultiVolume:
                ReopenMultiVolume();
                break;
            case SourceType.Stream:
                ReopenStream();
                break;
        }
    }

    private void ReopenFile()
    {
        using var uName = new SafeStringBuffer(_filename!);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);

        if (_password != null)
        {
            using var uPassword = new SafeStringBuffer(_password);
            if (archive_read_add_passphrase(handle, uPassword.Ptr) != 0)
                Throw();
        }

        if (archive_read_open_filename(handle, uName.Ptr, (int)_blockSize) != 0)
            Throw();
    }

    private void ReopenMultiVolume()
    {
        using var names = new DisposableStringArray(_filenames!);
        handle = archive_read_new();
        archive_read_support_filter_all(handle);
        archive_read_support_format_all(handle);

        if (_password != null)
        {
            using var uPassword = new SafeStringBuffer(_password);
            if (archive_read_add_passphrase(handle, uPassword.Ptr) != 0)
                Throw();
        }

        if (archive_read_open_filenames(handle, names.Ptr, (int)_blockSize) != 0)
            Throw();
    }

    private void ReopenStream()
    {
        if (_inputStream == null)
            throw new InvalidOperationException("No input stream available");

        if (!_inputStream.CanSeek)
            throw new NotSupportedException("Cannot reset: the underlying stream is not seekable. Use a seekable stream or reopen from file.");

        _inputStream.Seek(0, SeekOrigin.Begin);
        InitializeFromStream();
    }

    /// <summary>
    /// Gets the first entry in the archive without consuming the iterator.
    /// </summary>
    /// <returns>The first entry, or null if the archive is empty.</returns>
    /// <remarks>
    /// This is a convenience method for archives containing a single file.
    /// The entry's stream must be consumed before calling other methods on the reader.
    /// Unlike using Entries().First(), this method does not advance past the entry.
    /// </remarks>
    public Entry? FirstEntry()
    {
        int r = archive_read_next_header(handle, out var entryHandle);
        if (r == (int)ARCHIVE_RESULT.ARCHIVE_EOF)
            return null;
        if (r != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();
        return Entry.Create(entryHandle, handle);
    }


    /// <summary>
    /// Represents an entry (file or directory) within an archive.
    /// </summary>
    public class Entry
    {
        /// <summary>
        /// The handle to the native archive entry.
        /// </summary>
        protected readonly IntPtr entryHandle;
        /// <summary>
        /// The handle to the native archive.
        /// </summary>
        protected readonly IntPtr archiveHandle;

        /// <summary>
        /// Gets the name of the entry.
        /// </summary>
        public string Name { get; }
        /// <summary>
        /// Gets or sets the type of the entry.
        /// </summary>
        public EntryType Type;
        /// <summary>
        /// Gets the extracted length, in bytes, of the entry.
        /// </summary>
        public long LengthBytes { get; }
        /// <summary>
        /// Gets a stream to read the content of the entry.
        /// </summary>
        public FileStream Stream => new(archiveHandle, LengthBytes);

        /// <summary>
        /// Gets a value indicating whether this entry is a directory.
        /// </summary>
        public bool IsDirectory => Type == EntryType.Directory;
        /// <summary>
        /// Gets a value indicating whether this entry is a regular file.
        /// </summary>
        public bool IsRegularFile => Type == EntryType.RegularFile;

        /// <summary>
        /// Initializes a new instance of <see cref="Entry"/> with native handles.
        /// </summary>
        /// <param name="entryHandle">The handle to the native archive entry.</param>
        /// <param name="archiveHandle">The handle to the native archive.</param>
        protected Entry(IntPtr entryHandle, IntPtr archiveHandle)
        {
            this.entryHandle = entryHandle;
            this.archiveHandle = archiveHandle;
            Name = PtrToStringUTF8(archive_entry_pathname(entryHandle)) ?? throw new ApplicationException("Unable to retrieve entry's pathname");
            Type = (EntryType)archive_entry_filetype(entryHandle);
            LengthBytes = archive_entry_size(entryHandle);
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

        /// <summary>
        /// Reads the entire content of the entry into a byte array.
        /// </summary>
        /// <returns>A byte array containing the entry's content.</returns>
        /// <remarks>
        /// This method reads the entire entry content into memory.
        /// For large files, consider using the Stream property directly.
        /// This method can only be called once per entry; subsequent calls will return incomplete data.
        /// </remarks>
        public byte[] ReadAllBytes()
        {
            using var stream = Stream;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Reads the entire content of the entry as a string.
        /// </summary>
        /// <param name="encoding">The encoding to use. Defaults to UTF-8 if not specified.</param>
        /// <returns>A string containing the entry's content.</returns>
        /// <remarks>
        /// This method reads the entire entry content into memory.
        /// For large files, consider using the Stream property directly.
        /// This method can only be called once per entry; subsequent calls will return incomplete data.
        /// </remarks>
        public string ReadAllText(System.Text.Encoding? encoding = null)
        {
            encoding ??= System.Text.Encoding.UTF8;
            return encoding.GetString(ReadAllBytes());
        }

    }

    /// <summary>
    /// Specifies the type of an archive entry.
    /// </summary>
    public enum EntryType
    {
        /// <summary>
        /// Represents a directory entry.
        /// </summary>
        Directory = 0x4000,  // AE_IFDIR
        /// <summary>
        /// Represents a regular file entry.
        /// </summary>
        RegularFile = 0x8000 // AE_IFREG
    }

    /// <summary>
    /// Provides a read-only stream for reading archive entry data.
    /// </summary>
    public class FileStream : Stream
    {
        private readonly IntPtr archiveHandle;
        private bool _eof;

        internal FileStream(IntPtr archiveHandle, long lengthBytes)
        {
            this.archiveHandle = archiveHandle;
            Length = lengthBytes;
        }

        /// <summary>
        /// Flushes any buffered data. This is a no-op for read-only streams.
        /// </summary>
        public override void Flush()
        {
        }

        /// <summary>
        /// Reads a sequence of bytes from the current stream and advances the position within the stream by the number of bytes read.
        /// </summary>
        /// <param name="buffer">An array of bytes to read data into.</param>
        /// <param name="offset">The zero-based byte offset in buffer at which to begin storing data.</param>
        /// <param name="count">The maximum number of bytes to read.</param>
        /// <returns>The total number of bytes read into the buffer.</returns>
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Once EOF is reached, return 0 without calling native code again
            // (libarchive throws if archive_read_data is called after EOF)
            if (_eof)
                return 0;

            nint result = archive_read_data(archiveHandle, ref buffer[offset], count);
            if (result < 0)
            {
                // Negative return indicates error (e.g., wrong password, encryption not supported)
                throw new ApplicationException(
                    PtrToStringUTF8(archive_error_string(archiveHandle))
                    ?? $"Error reading archive data (code: {result})");
            }

            if (result == 0)
                _eof = true;

            return (int)result;
        }

        /// <summary>
        /// Sets the position within the current stream. Not supported for archive streams.
        /// </summary>
        /// <param name="offset">A byte offset relative to the origin parameter.</param>
        /// <param name="origin">A value of type SeekOrigin indicating the reference point.</param>
        /// <returns>The new position within the current stream.</returns>
        /// <exception cref="NotSupportedException">Seeking is not supported.</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Sets the length of the current stream. Not supported for archive streams.
        /// </summary>
        /// <param name="value">The desired length of the current stream in bytes.</param>
        /// <exception cref="NotSupportedException">Setting length is not supported.</exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Writes a sequence of bytes to the current stream. Not supported for read-only archive streams.
        /// </summary>
        /// <param name="buffer">An array of bytes to write.</param>
        /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes.</param>
        /// <param name="count">The number of bytes to write.</param>
        /// <exception cref="NotSupportedException">Writing is not supported.</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Gets a value indicating whether the current stream supports reading. Always returns true.
        /// </summary>
        public override bool CanRead => true;
        /// <summary>
        /// Gets a value indicating whether the current stream supports seeking. Always returns false.
        /// </summary>
        public override bool CanSeek => false;
        /// <summary>
        /// Gets a value indicating whether the current stream supports writing. Always returns false.
        /// </summary>
        public override bool CanWrite => false;
        public override long Length { get; }
        /// <summary>
        /// Gets or sets the position within the current stream. Not supported for archive streams.
        /// </summary>
        /// <exception cref="NotSupportedException">Position is not supported.</exception>
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
    private static partial nint archive_read_data(IntPtr a, ref byte buff, nint size);

    [LibraryImport("archive")]
    private static partial int archive_read_next_header(IntPtr a, out IntPtr entry);

    [LibraryImport("archive")]
    private static partial IntPtr archive_entry_pathname(IntPtr entry);

    [LibraryImport("archive")]
    private static partial int archive_entry_filetype(IntPtr entry);

    [LibraryImport("archive")]
    private static partial long archive_entry_size(IntPtr entry);

    [LibraryImport("archive")]
    private static partial int archive_read_free(IntPtr a);

    [LibraryImport("archive")]
    private static partial IntPtr archive_error_string(IntPtr a);

    [LibraryImport("archive")]
    private static partial int archive_read_add_passphrase(IntPtr a, IntPtr passphrase);

    [LibraryImport("archive")]
    private static partial int archive_read_has_encrypted_entries(IntPtr a);
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
    private static extern nint archive_read_data(IntPtr a, ref byte buff, nint size);

    [DllImport("archive")]
    private static extern int archive_read_next_header(IntPtr a, out IntPtr entry);

    [DllImport("archive")]
    private static extern IntPtr archive_entry_pathname(IntPtr entry);

    [DllImport("archive")]
    private static extern int archive_entry_filetype(IntPtr entry);

    [DllImport("archive")]
    private static extern long archive_entry_size(IntPtr entry);

    [DllImport("archive")]
    private static extern int archive_read_free(IntPtr a);

    [DllImport("archive")]
    private static extern IntPtr archive_error_string(IntPtr a);

    [DllImport("archive")]
    private static extern int archive_read_add_passphrase(IntPtr a, IntPtr passphrase);

    [DllImport("archive")]
    private static extern int archive_read_has_encrypted_entries(IntPtr a);
#endif

#if NETSTANDARD2_0
    // .NET Standard 2.0 Native Library Loading
    private static void PreloadNativeLibrary()
    {
        // On Windows, [assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        // handles native library loading automatically, so we only need manual loading on Unix.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // No manual loading needed - .NET Framework will find the DLL via AssemblyDirectory
            return;
        }

        // Unix platforms (Linux/macOS) need explicit dlopen() for Mono/.NET Framework
        var libraryPath = GetNativeLibraryPath();

        if (!File.Exists(libraryPath))
        {
            throw new DllNotFoundException($"Native library not found at: {libraryPath}");
        }

        LoadUnixLibrary(libraryPath);
    }

    private static string GetNativeLibraryPath()
    {
        // Enable diagnostics via environment variable: LIBARCHIVE_NET_DEBUG=1
        var enableDiagnostics = Environment.GetEnvironmentVariable("LIBARCHIVE_NET_DEBUG") == "1";

        // Search locations in priority order:
        // 1. AppDomain.CurrentDomain.BaseDirectory - where .targets copies files for consuming apps/tests
        // 2. Assembly.GetExecutingAssembly().Location - NuGet package cache location
        // 3. Assembly.GetEntryAssembly()?.Location - entry point location (fallback)
        var searchLocations = new[]
        {
            ("AppDomain.BaseDirectory", AppDomain.CurrentDomain.BaseDirectory),
            ("LibArchive.Net Assembly", Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)),
            ("Entry Assembly", Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location))
        }.Where(loc => !string.IsNullOrEmpty(loc.Item2)).ToArray();

        // Determine RID and filename based on platform
        var (rid, filename) = GetRuntimeIdentifierAndFilename();

        if (enableDiagnostics)
        {
            Console.Error.WriteLine($"[LibArchive.Net] Native library search:");
            Console.Error.WriteLine($"[LibArchive.Net]   Platform: {RuntimeInformation.OSDescription}");
            Console.Error.WriteLine($"[LibArchive.Net]   Architecture: {RuntimeInformation.ProcessArchitecture}");
            Console.Error.WriteLine($"[LibArchive.Net]   RID: {rid}");
            Console.Error.WriteLine($"[LibArchive.Net]   Filename: {filename}");
            Console.Error.WriteLine($"[LibArchive.Net]   Search locations:");
        }

        // Search each location for the native library
        foreach (var (locName, baseDir) in searchLocations)
        {
            if (string.IsNullOrEmpty(baseDir))
                continue;

            var libraryPath = Path.Combine(baseDir, "runtimes", rid, "native", filename);

            if (enableDiagnostics)
            {
                Console.Error.WriteLine($"[LibArchive.Net]     [{locName}] {libraryPath}");
                Console.Error.WriteLine($"[LibArchive.Net]       Exists: {File.Exists(libraryPath)}");
            }

            if (File.Exists(libraryPath))
            {
                if (enableDiagnostics)
                {
                    Console.Error.WriteLine($"[LibArchive.Net]   ✓ Found at: {libraryPath}");
                }
                return libraryPath;
            }
        }

        // Not found - build detailed error message
        var searchedPaths = searchLocations
            .Where(loc => !string.IsNullOrEmpty(loc.Item2))
            .Select(loc => $"  - [{loc.Item1}] {Path.Combine(loc.Item2, "runtimes", rid, "native", filename)}")
            .ToArray();

        var errorMessage = $"Native library '{filename}' not found. Searched locations:\n{string.Join("\n", searchedPaths)}\n\n" +
                          $"Platform: {RuntimeInformation.OSDescription}\n" +
                          $"Architecture: {RuntimeInformation.ProcessArchitecture}\n" +
                          $"RID: {rid}\n\n" +
                          $"Tip: Set LIBARCHIVE_NET_DEBUG=1 environment variable for detailed diagnostics.";

        throw new DllNotFoundException(errorMessage);
    }

    private static (string rid, string filename) GetRuntimeIdentifierAndFilename()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException($"Unsupported Windows architecture: {RuntimeInformation.ProcessArchitecture}")
            };
            return (arch, "archive.dll");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm => "linux-arm",
                Architecture.Arm64 => "linux-arm64",
                _ => throw new PlatformNotSupportedException($"Unsupported Linux architecture: {RuntimeInformation.ProcessArchitecture}")
            };
            return (arch, "libarchive.so");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return (arch, "libarchive.dylib");
        }
        else
        {
            throw new PlatformNotSupportedException($"Unsupported platform: {RuntimeInformation.OSDescription}");
        }
    }

    private static void LoadUnixLibrary(string libraryPath)
    {
        // CRITICAL for Mono: Mono's P/Invoke searches for "archive" → "libarchive.so"/"libarchive.dylib"
        // in AppDomain.BaseDirectory, but our library is in runtimes/{rid}/native/
        // We must copy it to the base directory so Mono can find it
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var destPath = Path.Combine(baseDir, Path.GetFileName(libraryPath));

        // Copy library to base directory if not already there
        if (!File.Exists(destPath))
        {
            try
            {
                File.Copy(libraryPath, destPath, overwrite: false);

                if (Environment.GetEnvironmentVariable("LIBARCHIVE_NET_DEBUG") == "1")
                {
                    Console.Error.WriteLine($"[LibArchive.Net] Copied {libraryPath} to {destPath}");
                }
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("LIBARCHIVE_NET_DEBUG") == "1")
                {
                    Console.Error.WriteLine($"[LibArchive.Net] Warning: Failed to copy library to base directory: {ex.Message}");
                }
            }
        }

        // Load the library with RTLD_GLOBAL so symbols are available
        // RTLD_NOW (0x2): Resolve all symbols immediately
        // RTLD_GLOBAL (0x100): Make symbols available in global namespace for P/Invoke
        const int RTLD_NOW = 0x2;
        const int RTLD_GLOBAL = 0x100;
        const int flags = RTLD_NOW | RTLD_GLOBAL;

        var handle = dlopen(libraryPath, flags);
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