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
/// Supports various formats (zip, tar, 7zip) with compression and optional encryption.
/// </summary>
public partial class LibArchiveWriter : SafeHandleZeroOrMinusOneIsInvalid
{
    #region Constants and Fields

    private const long SMALL_FILE_THRESHOLD = 2 * 1024 * 1024; // 2 MB
    private const long MEMORY_MAP_THRESHOLD = 64 * 1024 * 1024; // 64 MB
    private const int DEFAULT_BLOCK_SIZE = 1 << 20; // 1 MB

    private readonly ArchiveFormat format;
    private readonly uint blockSize;
    private string? password;
    private EncryptionType encryptionType = EncryptionType.Default;

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

    #region Constructors

    /// <summary>
    /// Creates a new archive writer for the specified file.
    /// </summary>
    /// <param name="filename">Path to the archive file to create.</param>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">Compression algorithm to apply (default: None).</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6).</param>
    /// <param name="blockSize">Block size in bytes for writing (default: 1 MiB).</param>
    /// <param name="password">Optional password for encryption (ZIP and 7-Zip only).</param>
    /// <param name="encryption">Encryption type when password is provided (default: format-specific).</param>
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

        this.format = format;
        this.blockSize = blockSize;
        this.password = password;
        this.encryptionType = encryption;

        // Create and configure archive
        handle = archive_write_new();
        if (handle == IntPtr.Zero)
            throw new ApplicationException("Failed to create archive writer");

        try
        {
            SetFormat(format);
            AddFilter(compression, compressionLevel);

            if (!string.IsNullOrEmpty(password))
                ConfigureEncryption();

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

        // Note: Compression level setting is filter-specific in libarchive
        // For now, we rely on the default levels
        // TODO: Use archive_write_set_filter_option to set compression level
    }

    #endregion

    #region Encryption Configuration

    private void ConfigureEncryption()
    {
        if (string.IsNullOrEmpty(password))
            return;

        using var passBuffer = new SafeStringBuffer(password);

        // Set passphrase
        if (archive_write_set_passphrase(handle, passBuffer.Ptr) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();

        // Configure encryption based on format
        switch (format)
        {
            case ArchiveFormat.Zip:
                ConfigureZipEncryption();
                break;

            case ArchiveFormat.SevenZip:
                Configure7zEncryption();
                break;

            default:
                throw new NotSupportedException(
                    $"Password encryption is not supported for {format} format. " +
                    "Supported formats: Zip, SevenZip");
        }
    }

    private void ConfigureZipEncryption()
    {
        var actualType = encryptionType == EncryptionType.ZipCrypto ? EncryptionType.Traditional : encryptionType;

        string encryptionOption = actualType switch
        {
            EncryptionType.Default or EncryptionType.AES256 => "encryption=aes256",
            EncryptionType.AES192 => "encryption=aes192",
            EncryptionType.AES128 => "encryption=aes128",
            EncryptionType.Traditional => "encryption=traditional",
            EncryptionType.None => "encryption=none",
            _ => throw new ArgumentException($"Unsupported encryption type for ZIP: {actualType}")
        };

        using var optionBuffer = new SafeStringBuffer(encryptionOption);
        if (archive_write_set_options(handle, optionBuffer.Ptr) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();
    }

    private void Configure7zEncryption()
    {
        // 7-Zip always uses AES-256 when password is set
        if (encryptionType != EncryptionType.Default &&
            encryptionType != EncryptionType.AES256 &&
            encryptionType != EncryptionType.None)
        {
            throw new NotSupportedException(
                $"7-Zip only supports AES-256 encryption. Requested: {encryptionType}");
        }

        if (encryptionType == EncryptionType.None)
        {
            using var optionBuffer = new SafeStringBuffer("encryption=none");
            archive_write_set_options(handle, optionBuffer.Ptr);
        }
        // Default AES-256 is automatic when passphrase is set
    }

    /// <summary>
    /// Sets or changes the password and encryption type for the archive.
    /// Must be called before adding any entries.
    /// </summary>
    /// <param name="password">Password for encryption.</param>
    /// <param name="encryption">Encryption type (default: format-specific).</param>
    /// <returns>This LibArchiveWriter instance for fluent API.</returns>
    public LibArchiveWriter WithPassword(string password, EncryptionType encryption = EncryptionType.Default)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty", nameof(password));

        this.password = password;
        this.encryptionType = encryption;

        ConfigureEncryption();

        return this;
    }

    #endregion

    #region Error Handling

    private void Throw()
    {
        var errorMsg = PtrToStringUTF8(archive_error_string(handle)) ?? "Unknown error";
        throw new ApplicationException(errorMsg);
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
            archive_write_close(handle);

            // Free resources
            var result = archive_write_free(handle);

            // Cleanup callbacks if stream-based writer
            CleanupCallbacks();

            return result == (int)ARCHIVE_RESULT.ARCHIVE_OK;
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
    private static unsafe partial int archive_write_data(IntPtr archive, byte* buffer, int length);

    [LibraryImport("archive")]
    private static partial int archive_write_finish_entry(IntPtr archive);
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
    private static extern unsafe int archive_write_data(IntPtr archive, byte* buffer, int length);

    [DllImport("archive")]
    private static extern int archive_write_finish_entry(IntPtr archive);
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

    [DllImport("archive")]
    private static extern void archive_entry_set_mtime(IntPtr entry, long sec, long nsec);
#endif

    #endregion
}
