using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LibArchive.Net;

public partial class LibArchiveWriter
{
    #region Stream-based Writing

    private Stream? outputStream;
    private GCHandle? callbackHandle;

    // Callback delegates for stream writing
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArchiveOpenCallback(IntPtr archive, IntPtr clientData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArchiveCloseCallback(IntPtr archive, IntPtr clientData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ArchiveWriteCallback(IntPtr archive, IntPtr clientData, IntPtr buffer, UIntPtr length);

    // Delegate instances must be kept alive to prevent GC while native code holds references
    private ArchiveOpenCallback? _openCallback;
    private ArchiveCloseCallback? _closeCallback;
    private ArchiveWriteCallback? _writeCallback;

    /// <summary>
    /// Creates a new archive writer that writes to a stream.
    /// </summary>
    /// <param name="outputStream">The stream to write the archive to. Must be writable.</param>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">How to compress the archive (default: None). ZIP, 7-Zip and XAR archives
    /// compress their entries themselves: for them this selects the method, and
    /// <see cref="CompressionType.None"/> keeps the format's default (DEFLATE, LZMA and gzip respectively).
    /// Every other format is compressed as a whole by this filter, or not at all with
    /// <see cref="CompressionType.None"/>. See <see cref="CompressionType"/> for what each value does with each
    /// format.</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6), for
    /// the method or the filter. Level 0 stores a ZIP archive's entries uncompressed, whatever the method;
    /// 7-Zip and XAR give the level to the method as it is (see <see cref="CompressionType"/>). A tar, ustar,
    /// pax, cpio or ISO 9660 archive without a filter does not use the level, and does not check it.</param>
    /// <param name="password">Optional password for encryption. libarchive can only encrypt ZIP archives.</param>
    /// <param name="encryption">Encryption type when a password is provided (default: AES-256).</param>
    /// <param name="blockSize">Size in bytes of the blocks libarchive writes the archive in (default: 1 MiB);
    /// 0 writes data as it is produced. The last block is not padded, so the block size changes how the
    /// archive is written, not its size or contents.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is greater than
    /// <see cref="int.MaxValue"/>, the largest block size libarchive takes; or <paramref name="compressionLevel"/>
    /// is used and is less than 0 or greater than 9, or is 0 with <see cref="CompressionType.Bzip2"/> for a
    /// 7-Zip or XAR archive, as BZIP2 takes levels 1 to 9. Nothing is written to the stream.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="compression"/> is a method that a ZIP, 7-Zip or XAR archive does not have, or
    /// <see cref="CompressionType.Deflate"/> for another format, as libarchive has no DEFLATE filter (see
    /// <see cref="CompressionType"/>); or a password was supplied for a format other than ZIP (unless the
    /// encryption type is <see cref="EncryptionType.None"/>, which ignores the password), or this native library
    /// build cannot encrypt with the encryption type requested, as when it was built without a cryptography
    /// backend. Nothing is written to the stream.
    /// </exception>
    public LibArchiveWriter(
        Stream outputStream,
        ArchiveFormat format,
        CompressionType compression = CompressionType.None,
        int compressionLevel = 6,
        string? password = null,
        EncryptionType encryption = EncryptionType.Default,
        uint blockSize = DEFAULT_BLOCK_SIZE)
        : base(true)
    {
        if (outputStream == null)
            throw new ArgumentNullException(nameof(outputStream));
        if (!outputStream.CanWrite)
            throw new ArgumentException("Stream must be writable", nameof(outputStream));
        CheckBlockSize(blockSize);

        // Reject encryption and compression libarchive cannot provide before anything is written to the stream
        var encryptionOption = GetEncryptionOption(format, password, encryption);
        var compressionOptions = GetFormatCompressionOptions(format, compression, compressionLevel);

        this.outputStream = outputStream;
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
            ConfigureCompression(compressionOptions, compression, compressionLevel);

            if (encryptionOption != null)
                ConfigureEncryption(encryptionOption, password!);

            // Open with callbacks for stream writing
            OpenWithCallbacks();
        }
        catch
        {
            // Clean up on error
            if (callbackHandle.HasValue)
                callbackHandle.Value.Free();
            archive_write_free(handle);
            handle = IntPtr.Zero;
            throw;
        }
    }

    private void OpenWithCallbacks()
    {
        // Store callback instances as class fields to prevent GC while native code holds references
        _openCallback = new ArchiveOpenCallback(OnOpen);
        _closeCallback = new ArchiveCloseCallback(OnClose);
        _writeCallback = new ArchiveWriteCallback(OnWrite);

        // Keep the stream reference alive
        callbackHandle = GCHandle.Alloc(outputStream);

        SetBlockSize();

        // Open archive with callbacks
        var result = archive_write_open(
            handle,
            GCHandle.ToIntPtr(callbackHandle.Value),
            _openCallback,
            _writeCallback,
            _closeCallback);

        ThrowUnlessOk(result);
    }

    private int OnOpen(IntPtr archive, IntPtr clientData)
    {
        // Stream is already open, nothing to do
        return (int)ARCHIVE_RESULT.ARCHIVE_OK;
    }

    private int OnClose(IntPtr archive, IntPtr clientData)
    {
        // Flush the stream
        if (outputStream != null && outputStream.CanWrite)
        {
            try
            {
                outputStream.Flush();
                return (int)ARCHIVE_RESULT.ARCHIVE_OK;
            }
            catch
            {
                return (int)ARCHIVE_RESULT.ARCHIVE_FATAL;
            }
        }
        return (int)ARCHIVE_RESULT.ARCHIVE_OK;
    }

    private unsafe IntPtr OnWrite(IntPtr archive, IntPtr clientData, IntPtr buffer, UIntPtr length)
    {
        try
        {
            var stream = GCHandle.FromIntPtr(clientData).Target as Stream;
            if (stream == null || !stream.CanWrite)
                return new IntPtr(-1); // Signal error to libarchive

            int len = checked((int)length.ToUInt64());

            // Copy data from native buffer to managed byte array and write to stream
            var managedBuffer = new byte[len];
            Marshal.Copy(buffer, managedBuffer, 0, len);
            stream.Write(managedBuffer, 0, len);

            return new IntPtr(len);
        }
        catch
        {
            return new IntPtr(-1); // Signal error to libarchive
        }
    }

    partial void CleanupCallbacks()
    {
        // Free the callback handle if it exists
        if (callbackHandle.HasValue)
        {
            callbackHandle.Value.Free();
            callbackHandle = null;
        }

        // Clear delegate references (allows GC after native code is done)
        _openCallback = null;
        _closeCallback = null;
        _writeCallback = null;
    }

    #endregion

    #region Memory Writing

    /// <summary>
    /// Creates a new archive writer that writes to a memory buffer.
    /// Use <see cref="ToArray"/> or <see cref="ToMemoryStream"/> to retrieve the archive data.
    /// </summary>
    /// <remarks>
    /// libarchive writes the archive to the buffer in blocks of 1 MiB. To choose the block size, use
    /// <see cref="CreateMemoryWriter(uint, ArchiveFormat, CompressionType, int, string, EncryptionType, int)"/>.
    /// </remarks>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">How to compress the archive (default: None). ZIP, 7-Zip and XAR archives
    /// compress their entries themselves: for them this selects the method, and
    /// <see cref="CompressionType.None"/> keeps the format's default (DEFLATE, LZMA and gzip respectively).
    /// Every other format is compressed as a whole by this filter, or not at all with
    /// <see cref="CompressionType.None"/>. See <see cref="CompressionType"/> for what each value does with each
    /// format.</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6), for
    /// the method or the filter. Level 0 stores a ZIP archive's entries uncompressed, whatever the method;
    /// 7-Zip and XAR give the level to the method as it is (see <see cref="CompressionType"/>). A tar, ustar,
    /// pax, cpio or ISO 9660 archive without a filter does not use the level, and does not check it.</param>
    /// <param name="password">Optional password for encryption. libarchive can only encrypt ZIP archives.</param>
    /// <param name="encryption">Encryption type when a password is provided (default: AES-256).</param>
    /// <param name="initialCapacity">Initial capacity of the memory buffer in bytes (default: 64 KB).</param>
    /// <returns>A LibArchiveWriter that writes to memory.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="compressionLevel"/> is used and is less
    /// than 0 or greater than 9, or is 0 with <see cref="CompressionType.Bzip2"/> for a 7-Zip or XAR archive,
    /// as BZIP2 takes levels 1 to 9.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="compression"/> is a method that a ZIP, 7-Zip or XAR archive does not have, or
    /// <see cref="CompressionType.Deflate"/> for another format, as libarchive has no DEFLATE filter (see
    /// <see cref="CompressionType"/>); or a password was supplied for a format other than ZIP (unless the
    /// encryption type is <see cref="EncryptionType.None"/>, which ignores the password), or this native library
    /// build cannot encrypt with the encryption type requested, as when it was built without a cryptography
    /// backend.
    /// </exception>
    public static LibArchiveWriter CreateMemoryWriter(
        ArchiveFormat format,
        CompressionType compression = CompressionType.None,
        int compressionLevel = 6,
        string? password = null,
        EncryptionType encryption = EncryptionType.Default,
        int initialCapacity = 64 * 1024)
    {
        return CreateMemoryWriter(DEFAULT_BLOCK_SIZE, format, compression, compressionLevel, password, encryption, initialCapacity);
    }

    /// <summary>
    /// Creates a new archive writer that writes to a memory buffer in blocks of the given size.
    /// Use <see cref="ToArray"/> or <see cref="ToMemoryStream"/> to retrieve the archive data.
    /// </summary>
    /// <remarks>
    /// The block size comes first so that <c>CreateMemoryWriter(format, 0)</c> still calls the overload without
    /// it, with <see cref="CompressionType.None"/>. To give the block size after the format, name every
    /// argument: <c>CreateMemoryWriter(format: ArchiveFormat.Zip, blockSize: 4096)</c>.
    /// </remarks>
    /// <param name="blockSize">Size in bytes of the blocks libarchive writes the archive to the buffer in
    /// (1 MiB in the overload without this parameter); 0 writes data as it is produced. The last block is not
    /// padded, so the block size changes how the archive is written, not its size or contents.</param>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">How to compress the archive (default: None). ZIP, 7-Zip and XAR archives
    /// compress their entries themselves: for them this selects the method, and
    /// <see cref="CompressionType.None"/> keeps the format's default (DEFLATE, LZMA and gzip respectively).
    /// Every other format is compressed as a whole by this filter, or not at all with
    /// <see cref="CompressionType.None"/>. See <see cref="CompressionType"/> for what each value does with each
    /// format.</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6), for
    /// the method or the filter. Level 0 stores a ZIP archive's entries uncompressed, whatever the method;
    /// 7-Zip and XAR give the level to the method as it is (see <see cref="CompressionType"/>). A tar, ustar,
    /// pax, cpio or ISO 9660 archive without a filter does not use the level, and does not check it.</param>
    /// <param name="password">Optional password for encryption. libarchive can only encrypt ZIP archives.</param>
    /// <param name="encryption">Encryption type when a password is provided (default: AES-256).</param>
    /// <param name="initialCapacity">Initial capacity of the memory buffer in bytes (default: 64 KB).</param>
    /// <returns>A LibArchiveWriter that writes to memory.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is greater than
    /// <see cref="int.MaxValue"/>, the largest block size libarchive takes; or <paramref name="compressionLevel"/>
    /// is used and is less than 0 or greater than 9, or is 0 with <see cref="CompressionType.Bzip2"/> for a
    /// 7-Zip or XAR archive, as BZIP2 takes levels 1 to 9.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="compression"/> is a method that a ZIP, 7-Zip or XAR archive does not have, or
    /// <see cref="CompressionType.Deflate"/> for another format, as libarchive has no DEFLATE filter (see
    /// <see cref="CompressionType"/>); or a password was supplied for a format other than ZIP (unless the
    /// encryption type is <see cref="EncryptionType.None"/>, which ignores the password), or this native library
    /// build cannot encrypt with the encryption type requested, as when it was built without a cryptography
    /// backend.
    /// </exception>
    public static LibArchiveWriter CreateMemoryWriter(
        uint blockSize,
        ArchiveFormat format,
        CompressionType compression = CompressionType.None,
        int compressionLevel = 6,
        string? password = null,
        EncryptionType encryption = EncryptionType.Default,
        int initialCapacity = 64 * 1024)
    {
        CheckBlockSize(blockSize);
        var memoryStream = new MemoryStream(initialCapacity);
        return new LibArchiveWriter(memoryStream, format, compression, compressionLevel, password, encryption, blockSize);
    }

    /// <summary>
    /// Gets the archive data as a byte array.
    /// Only valid for archives created with <see cref="O:LibArchive.Net.LibArchiveWriter.CreateMemoryWriter"/>.
    /// The archive must be disposed before calling this method.
    /// </summary>
    /// <returns>The complete archive as a byte array.</returns>
    /// <exception cref="InvalidOperationException">If the archive was not created with CreateMemoryWriter.</exception>
    public byte[] ToArray()
    {
        if (outputStream is not MemoryStream memoryStream)
            throw new InvalidOperationException("ToArray() is only valid for memory-based writers created with CreateMemoryWriter");

        if (!IsInvalid && !IsClosed)
            throw new InvalidOperationException("Archive must be disposed before retrieving data. Use 'using' statement or call Dispose().");

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Gets the archive data as a MemoryStream.
    /// Only valid for archives created with <see cref="O:LibArchive.Net.LibArchiveWriter.CreateMemoryWriter"/>.
    /// The archive must be disposed before calling this method.
    /// </summary>
    /// <returns>The MemoryStream containing the archive data.</returns>
    /// <exception cref="InvalidOperationException">If the archive was not created with CreateMemoryWriter.</exception>
    public MemoryStream ToMemoryStream()
    {
        if (outputStream is not MemoryStream memoryStream)
            throw new InvalidOperationException("ToMemoryStream() is only valid for memory-based writers created with CreateMemoryWriter");

        if (!IsInvalid && !IsClosed)
            throw new InvalidOperationException("Archive must be disposed before retrieving data. Use 'using' statement or call Dispose().");

        return memoryStream;
    }

    #endregion

    #region P/Invoke Declarations - Stream Writing

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_write_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveWriteCallback? writeCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#else
    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_write_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveWriteCallback? writeCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#endif

    #endregion
}
