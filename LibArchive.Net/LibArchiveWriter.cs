using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LibArchive.Net;

/// <summary>
/// Provides write access to create archive files using the libarchive library.
/// Supports various formats (zip, tar, 7zip) with compression, and optional encryption for ZIP archives.
/// </summary>
public partial class LibArchiveWriter : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Constants and Fields

    private const long SMALL_FILE_THRESHOLD = 2 * 1024 * 1024; // 2 MB
    private const long MEMORY_MAP_THRESHOLD = 64 * 1024 * 1024; // 64 MB
    private const int DEFAULT_BLOCK_SIZE = 1 << 20; // 1 MB

    private readonly ArchiveFormat format;
    private readonly uint blockSize;
    private readonly bool encrypted;

    private enum ARCHIVE_RESULT
    {
        ARCHIVE_OK = 0,
        ARCHIVE_EOF = 1,
        ARCHIVE_RETRY = -10,
        ARCHIVE_WARN = -20,
        ARCHIVE_FAILED = -25,
        ARCHIVE_FATAL = -30
    }

    // Entry file type constants (from archive_entry.h)
    private const int AE_IFREG = 0x8000;  // Regular file
    private const int AE_IFDIR = 0x4000;  // Directory
    private const int AE_IFLNK = 0xA000;  // Symbolic link

    #endregion

    #region Static Constructor

    static LibArchiveWriter()
    {
        // Ensure native library is preloaded by triggering LibArchiveReader's static constructor
        // This is needed for .NET Standard 2.0 (used by net462/Mono) which requires manual preloading
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(LibArchiveReader).TypeHandle);
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new archive writer for the specified file.
    /// </summary>
    /// <param name="filename">Path to the archive file to create.</param>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">Compression algorithm to apply (default: None).</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6).</param>
    /// <param name="blockSize">Block size in bytes for writing (default: 1 MiB).</param>
    /// <param name="password">Optional password for encryption. libarchive can only encrypt ZIP archives.</param>
    /// <param name="encryption">Encryption type when a password is provided (default: AES-256).</param>
    /// <exception cref="NotSupportedException">
    /// A password was supplied for a format other than ZIP (unless the encryption type is
    /// <see cref="EncryptionType.None"/>, which ignores the password), or with an encryption type that libarchive
    /// or this native library build cannot write.
    /// </exception>
    public LibArchiveWriter(
        string filename,
        ArchiveFormat format,
        CompressionType compression = CompressionType.None,
        int compressionLevel = 6,
        uint blockSize = DEFAULT_BLOCK_SIZE,
        string? password = null,
        EncryptionType encryption = EncryptionType.Default)
        : base(true)
    {
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("Filename cannot be null or empty", nameof(filename));

        // Reject encryption libarchive cannot provide before the output file is created or truncated
        var encryptionOption = GetEncryptionOption(format, password, encryption);

        this.format = format;
        this.blockSize = blockSize;
        this.encrypted = encryptionOption != null;

        // Create and configure archive
        handle = archive_write_new();
        if (handle == IntPtr.Zero)
            throw new ApplicationException("Failed to create archive writer");

        try
        {
            SetFormat(format);
            AddFilter(compression, compressionLevel);

            if (encryptionOption != null)
                ConfigureEncryption(encryptionOption, password!);

            // Open the file for writing
            using var filenameBuffer = new SafeStringBuffer(filename);
            if (archive_write_open_filename(handle, filenameBuffer.Ptr, (int)blockSize) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }
        catch
        {
            // Clean up on error
            archive_write_free(handle);
            handle = IntPtr.Zero;
            throw;
        }
    }

    #endregion

    #region Format and Compression Configuration

    private void SetFormat(ArchiveFormat format)
    {
        var result = format switch
        {
            ArchiveFormat.Zip => archive_write_set_format_zip(handle),
            ArchiveFormat.SevenZip => archive_write_set_format_7zip(handle),
            ArchiveFormat.Tar => archive_write_set_format_pax_restricted(handle), // Modern TAR
            ArchiveFormat.Ustar => archive_write_set_format_ustar(handle),
            ArchiveFormat.Pax => archive_write_set_format_pax(handle),
            ArchiveFormat.Cpio => archive_write_set_format_cpio_newc(handle),
            ArchiveFormat.Iso9660 => archive_write_set_format_iso9660(handle),
            ArchiveFormat.Xar => archive_write_set_format_xar(handle),
            _ => throw new NotSupportedException($"Archive format {format} is not supported")
        };

        if (result != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();
    }

    private void AddFilter(CompressionType compression, int level)
    {
        if (compression == CompressionType.None)
            return;

        var result = compression switch
        {
            CompressionType.Gzip => archive_write_add_filter_gzip(handle),
            CompressionType.Bzip2 => archive_write_add_filter_bzip2(handle),
            CompressionType.Xz => archive_write_add_filter_xz(handle),
            CompressionType.Lzma => archive_write_add_filter_lzma(handle),
            CompressionType.Lz4 => archive_write_add_filter_lz4(handle),
            CompressionType.Zstd => archive_write_add_filter_zstd(handle),
            CompressionType.Compress => archive_write_add_filter_compress(handle),
            CompressionType.Lzip => archive_write_add_filter_lzip(handle),
            _ => throw new NotSupportedException($"Compression type {compression} is not supported")
        };

        if (result != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();

        // Set compression level (0-9)
        if (level < 0 || level > 9)
            throw new ArgumentOutOfRangeException(nameof(level), "Compression level must be between 0 and 9");

        // Set compression level via filter options
        // The option name varies by filter: "compression-level" is the common name
        using var optionName = new SafeStringBuffer("compression-level");
        using var optionValue = new SafeStringBuffer(level.ToString());
        // Ignore result - not all filters support compression-level, and that's ok
        archive_write_set_filter_option(handle, IntPtr.Zero, optionName.Ptr, optionValue.Ptr);
    }

    #endregion

    #region Encryption Configuration

    /// <summary>
    /// Works out the libarchive option for an encryption request, rejecting whatever libarchive cannot encrypt.
    /// Touches neither native nor file system state, so it can run before the output is created.
    /// </summary>
    /// <returns>The option to apply, or <c>null</c> when the archive is to be written unencrypted.</returns>
    private static string? GetEncryptionOption(ArchiveFormat format, string? password, EncryptionType encryption)
    {
        // EncryptionType.None means no encryption, even when a password is supplied
        if (string.IsNullOrEmpty(password) || encryption == EncryptionType.None)
            return null;

        // Only libarchive's ZIP writer encrypts. Every other writer accepts a passphrase and then ignores it,
        // which would leave the caller with a plaintext archive they believe to be protected.
        if (format != ArchiveFormat.Zip)
            throw new NotSupportedException(
                $"Password encryption is not supported for the {format} format: " +
                "libarchive can only encrypt ZIP archives.");

        // The "zip:" module prefix makes libarchive report the ZIP writer's own error message.
        // Without it, every failure is replaced by "Undefined option: `encryption'".
        return encryption switch
        {
            EncryptionType.Default or EncryptionType.AES256 => "zip:encryption=aes256",
            EncryptionType.AES128 => "zip:encryption=aes128",
            EncryptionType.Traditional => "zip:encryption=traditional",
            EncryptionType.AES192 => throw new NotSupportedException(
                "AES-192 encryption cannot be written: libarchive's ZIP writer supports AES-128 and AES-256 only."),
            _ => throw new ArgumentException($"Unsupported encryption type for ZIP: {encryption}")
        };
    }

    private void ConfigureEncryption(string encryptionOption, string password)
    {
        using var optionBuffer = new SafeStringBuffer(encryptionOption);
        if (archive_write_set_options(handle, optionBuffer.Ptr) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
        {
            var errorMsg = GetErrorMessage();

            // This is what libarchive's ZIP writer reports when it has no cryptography backend to encrypt with
            if (errorMsg.IndexOf("encryption not supported", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new NotSupportedException(
                    "Encryption is not supported by this native library build: " +
                    "libarchive was built without a usable cryptography backend.",
                    new ApplicationException(errorMsg));

            throw new ApplicationException(errorMsg);
        }

        using var passBuffer = new SafeStringBuffer(password);
        if (archive_write_set_passphrase(handle, passBuffer.Ptr) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();
    }

    /// <summary>
    /// Requests a password and encryption type for an archive that is already open.
    /// </summary>
    /// <remarks>
    /// libarchive only accepts encryption settings before an archive is opened, and the archive is opened when
    /// the writer is created, so this method cannot enable, change or remove encryption. Pass the password to
    /// the constructor or to <see cref="CreateMemoryWriter"/> instead.
    /// </remarks>
    /// <param name="password">Password for encryption.</param>
    /// <param name="encryption">Encryption type (default: AES-256).</param>
    /// <returns>This LibArchiveWriter instance for fluent API.</returns>
    /// <exception cref="ArgumentException">The password is null or empty.</exception>
    /// <exception cref="NotSupportedException">
    /// Encryption other than <see cref="EncryptionType.None"/> was requested for a format other than ZIP,
    /// or libarchive cannot write the encryption type.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown for every request that is not rejected with one of the exceptions above, unless
    /// <paramref name="encryption"/> is <see cref="EncryptionType.None"/> and the writer is not encrypting.
    /// Encryption can only be configured before the archive is opened, so a request that matches the encryption
    /// the writer was created with throws as well.
    /// </exception>
    public LibArchiveWriter WithPassword(string password, EncryptionType encryption = EncryptionType.Default)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty", nameof(password));

        var encryptionRequested = GetEncryptionOption(format, password, encryption) != null;

        // EncryptionType.None on a writer that is not encrypting leaves nothing to change
        if (!encryptionRequested && !encrypted)
            return this;

        throw new InvalidOperationException(
            "Encryption settings cannot be changed after the archive has been opened. " +
            "Pass the password to the LibArchiveWriter constructor or to CreateMemoryWriter instead.");
    }

    #endregion

    #region Error Handling

    private void Throw()
    {
        throw new ApplicationException(GetErrorMessage());
    }

    private string GetErrorMessage()
    {
        return PtrToStringUTF8(archive_error_string(handle)) ?? "Unknown error";
    }

    private static string? PtrToStringUTF8(IntPtr ptr)
    {
#if NETSTANDARD2_0
        if (ptr == IntPtr.Zero)
            return null;

        // Find null terminator
        var length = 0;
        unsafe
        {
            byte* p = (byte*)ptr;
            while (p[length] != 0)
            {
                length++;
                if (length > 1000000) // Safety limit
                    throw new InvalidOperationException("UTF-8 string too long or not null-terminated");
            }
        }

        if (length == 0)
            return string.Empty;

        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        return System.Text.Encoding.UTF8.GetString(bytes);
#else
        return Marshal.PtrToStringUTF8(ptr);
#endif
    }

    #endregion

    #region Resource Management

    /// <summary>
    /// Releases the archive handle and closes the archive.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        if (handle == IntPtr.Zero)
            return true;

        try
        {
            // Close the archive (writes final data)
            var closeResult = archive_write_close(handle);

            // Free resources
            var freeResult = archive_write_free(handle);

            // Cleanup callbacks if stream-based writer
            CleanupCallbacks();

            // Return false if either operation failed - indicates potential archive corruption
            return closeResult == (int)ARCHIVE_RESULT.ARCHIVE_OK &&
                   freeResult == (int)ARCHIVE_RESULT.ARCHIVE_OK;
        }
        catch
        {
            return false;
        }
    }

    // Partial method to allow cleanup from stream module
    partial void CleanupCallbacks();

    #endregion

    #region P/Invoke Declarations - Core Functions

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial IntPtr archive_write_new();

    [LibraryImport("archive")]
    private static partial int archive_write_open_filename(IntPtr archive, IntPtr filename, int blockSize);

    [LibraryImport("archive")]
    private static partial int archive_write_close(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_free(IntPtr archive);

    [LibraryImport("archive")]
    private static partial IntPtr archive_error_string(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_header(IntPtr archive, IntPtr entry);

    [LibraryImport("archive")]
    private static unsafe partial nint archive_write_data(IntPtr archive, byte* buffer, nint length);

    [LibraryImport("archive")]
    private static partial int archive_write_finish_entry(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_bytes_per_block(IntPtr archive, int bytesPerBlock);

    [LibraryImport("archive")]
    private static partial int archive_write_set_bytes_in_last_block(IntPtr archive, int bytesInLastBlock);
#else
    [DllImport("archive")]
    private static extern IntPtr archive_write_new();

    [DllImport("archive")]
    private static extern int archive_write_open_filename(IntPtr archive, IntPtr filename, int blockSize);

    [DllImport("archive")]
    private static extern int archive_write_close(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_free(IntPtr archive);

    [DllImport("archive")]
    private static extern IntPtr archive_error_string(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_header(IntPtr archive, IntPtr entry);

    [DllImport("archive")]
    private static extern unsafe nint archive_write_data(IntPtr archive, byte* buffer, nint length);

    [DllImport("archive")]
    private static extern int archive_write_finish_entry(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_bytes_per_block(IntPtr archive, int bytesPerBlock);

    [DllImport("archive")]
    private static extern int archive_write_set_bytes_in_last_block(IntPtr archive, int bytesInLastBlock);
#endif

    #endregion

    #region P/Invoke Declarations - Format Functions

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial int archive_write_set_format_zip(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_7zip(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_pax_restricted(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_ustar(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_pax(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_cpio_newc(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_iso9660(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_format_xar(IntPtr archive);
#else
    [DllImport("archive")]
    private static extern int archive_write_set_format_zip(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_7zip(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_pax_restricted(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_ustar(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_pax(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_cpio_newc(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_iso9660(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_format_xar(IntPtr archive);
#endif

    #endregion

    #region P/Invoke Declarations - Filter Functions

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_gzip(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_bzip2(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_xz(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_lzma(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_lz4(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_zstd(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_compress(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_add_filter_lzip(IntPtr archive);

    [LibraryImport("archive")]
    private static partial int archive_write_set_filter_option(IntPtr archive, IntPtr module, IntPtr option, IntPtr value);
#else
    [DllImport("archive")]
    private static extern int archive_write_add_filter_gzip(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_bzip2(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_xz(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_lzma(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_lz4(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_zstd(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_compress(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_add_filter_lzip(IntPtr archive);

    [DllImport("archive")]
    private static extern int archive_write_set_filter_option(IntPtr archive, IntPtr module, IntPtr option, IntPtr value);
#endif

    #endregion

    #region P/Invoke Declarations - Encryption Functions

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial int archive_write_set_passphrase(IntPtr archive, IntPtr passphrase);

    [LibraryImport("archive")]
    private static partial int archive_write_set_options(IntPtr archive, IntPtr options);
#else
    [DllImport("archive")]
    private static extern int archive_write_set_passphrase(IntPtr archive, IntPtr passphrase);

    [DllImport("archive")]
    private static extern int archive_write_set_options(IntPtr archive, IntPtr options);
#endif

    #endregion

    #region P/Invoke Declarations - Entry Functions

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial IntPtr archive_entry_new();

    [LibraryImport("archive")]
    private static partial void archive_entry_free(IntPtr entry);

    [LibraryImport("archive")]
    private static partial void archive_entry_set_pathname(IntPtr entry, IntPtr pathname);

    [LibraryImport("archive")]
    private static partial void archive_entry_set_size(IntPtr entry, long size);

    [LibraryImport("archive")]
    private static partial void archive_entry_set_filetype(IntPtr entry, int filetype);

    [LibraryImport("archive")]
    private static partial void archive_entry_set_perm(IntPtr entry, int perm);

    private static void archive_entry_set_perm(IntPtr entry, UnixFileMode perm)
        => archive_entry_set_perm(entry, (int)perm);

    [LibraryImport("archive")]
    private static partial void archive_entry_set_mtime(IntPtr entry, long sec, long nsec);
#else
    [DllImport("archive")]
    private static extern IntPtr archive_entry_new();

    [DllImport("archive")]
    private static extern void archive_entry_free(IntPtr entry);

    [DllImport("archive")]
    private static extern void archive_entry_set_pathname(IntPtr entry, IntPtr pathname);

    [DllImport("archive")]
    private static extern void archive_entry_set_size(IntPtr entry, long size);

    [DllImport("archive")]
    private static extern void archive_entry_set_filetype(IntPtr entry, int filetype);

    [DllImport("archive")]
    private static extern void archive_entry_set_perm(IntPtr entry, int perm);

    private static void archive_entry_set_perm(IntPtr entry, UnixFileMode perm)
        => archive_entry_set_perm(entry, (int)perm);

    [DllImport("archive")]
    private static extern void archive_entry_set_mtime(IntPtr entry, long sec, long nsec);
#endif

    #endregion
}
