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
    private delegate int ArchiveOpenCallback(IntPtr archive, IntPtr clientData);
    private delegate int ArchiveCloseCallback(IntPtr archive, IntPtr clientData);
    private delegate IntPtr ArchiveWriteCallback(IntPtr archive, IntPtr clientData, IntPtr buffer, IntPtr length);

    /// <summary>
    /// Creates a new archive writer that writes to a stream.
    /// </summary>
    /// <param name="outputStream">The stream to write the archive to. Must be writable.</param>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">Compression algorithm to apply (default: None).</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6).</param>
    /// <param name="password">Optional password for encryption (ZIP and 7-Zip only).</param>
    /// <param name="encryption">Encryption type when password is provided (default: format-specific).</param>
    /// <param name="blockSize">Block size in bytes for writing (default: 1 MiB).</param>
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

        this.outputStream = outputStream;
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
        // Create callback instances that won't be garbage collected
        var openCallback = new ArchiveOpenCallback(OnOpen);
        var closeCallback = new ArchiveCloseCallback(OnClose);
        var writeCallback = new ArchiveWriteCallback(OnWrite);

        // Keep the stream reference alive
        callbackHandle = GCHandle.Alloc(outputStream);

        // Open archive with callbacks
        var result = archive_write_open(
            handle,
            GCHandle.ToIntPtr(callbackHandle.Value),
            openCallback,
            writeCallback,
            closeCallback);

        if (result != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();
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

    private unsafe IntPtr OnWrite(IntPtr archive, IntPtr clientData, IntPtr buffer, IntPtr length)
    {
        try
        {
            var stream = GCHandle.FromIntPtr(clientData).Target as Stream;
            if (stream == null || !stream.CanWrite)
                return IntPtr.Zero;

            int len = length.ToInt32();

            // Copy data from native buffer to managed byte array and write to stream
            var managedBuffer = new byte[len];
            Marshal.Copy(buffer, managedBuffer, 0, len);
            stream.Write(managedBuffer, 0, len);

            return length;
        }
        catch
        {
            return IntPtr.Zero; // Signal error
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
    }

    #endregion

    #region Memory Writing

    /// <summary>
    /// Creates a new archive writer that writes to a memory buffer.
    /// Use <see cref="ToArray"/> or <see cref="ToMemoryStream"/> to retrieve the archive data.
    /// </summary>
    /// <param name="format">Archive format to use.</param>
    /// <param name="compression">Compression algorithm to apply (default: None).</param>
    /// <param name="compressionLevel">Compression level 0-9, where 9 is maximum compression (default: 6).</param>
    /// <param name="password">Optional password for encryption (ZIP and 7-Zip only).</param>
    /// <param name="encryption">Encryption type when password is provided (default: format-specific).</param>
    /// <param name="initialCapacity">Initial capacity of the memory buffer in bytes (default: 64 KB).</param>
    /// <returns>A LibArchiveWriter that writes to memory.</returns>
    public static LibArchiveWriter CreateMemoryWriter(
        ArchiveFormat format,
        CompressionType compression = CompressionType.None,
        int compressionLevel = 6,
        string? password = null,
        EncryptionType encryption = EncryptionType.Default,
        int initialCapacity = 64 * 1024)
    {
        var memoryStream = new MemoryStream(initialCapacity);
        return new LibArchiveWriter(memoryStream, format, compression, compressionLevel, password, encryption);
    }

    /// <summary>
    /// Gets the archive data as a byte array.
    /// Only valid for archives created with <see cref="CreateMemoryWriter"/>.
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
    /// Only valid for archives created with <see cref="CreateMemoryWriter"/>.
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
    private static partial int archive_write_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveWriteCallback? writeCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#else
    [DllImport("archive")]
    private static extern int archive_write_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveWriteCallback? writeCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#endif

    #endregion
}
