using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LibArchive.Net;

public partial class LibArchiveReader
{
    #region Stream-based Reading

    private Stream? _inputStream;
    private GCHandle? _callbackHandle;
    private byte[]? _readBuffer;
    private GCHandle? _bufferHandle;

    // Callback delegates for stream reading
    private delegate int ArchiveOpenCallback(IntPtr archive, IntPtr clientData);
    private delegate int ArchiveCloseCallback(IntPtr archive, IntPtr clientData);
    private delegate IntPtr ArchiveReadCallback(IntPtr archive, IntPtr clientData, out IntPtr buffer);

    // Delegate instances must be kept alive to prevent GC while native code holds references
    private ArchiveOpenCallback? _openCallback;
    private ArchiveCloseCallback? _closeCallback;
    private ArchiveReadCallback? _readCallback;

    /// <summary>
    /// Creates a new archive reader that reads from a stream.
    /// </summary>
    /// <param name="inputStream">The stream to read the archive from. Must be readable.</param>
    /// <param name="password">Optional password for encrypted archives.</param>
    /// <param name="blockSize">Block size in bytes for reading (default: 1 MiB).</param>
    /// <remarks>
    /// The stream should be positioned at the start of the archive data.
    /// For Reset() support, the stream must be seekable (CanSeek = true).
    /// </remarks>
    public LibArchiveReader(Stream inputStream, string? password = null, uint blockSize = 1 << 20)
        : base(true)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (!inputStream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(inputStream));

        _inputStream = inputStream;
        _password = password;
        _blockSize = blockSize;

        InitializeFromStream();
    }

    private void InitializeFromStream()
    {
        if (_inputStream == null)
            throw new InvalidOperationException("No input stream available");

        handle = archive_read_new();
        if (handle == IntPtr.Zero)
            throw new ApplicationException("Failed to create archive reader");

        try
        {
            archive_read_support_filter_all(handle);
            archive_read_support_format_all(handle);

            if (_password != null)
            {
                using var uPassword = new SafeStringBuffer(_password);
                if (archive_read_add_passphrase(handle, uPassword.Ptr) != 0)
                    Throw();
            }

            OpenWithCallbacks();
        }
        catch
        {
            CleanupCallbacks();
            archive_read_free(handle);
            handle = IntPtr.Zero;
            throw;
        }
    }

    private void OpenWithCallbacks()
    {
        // Allocate read buffer
        _readBuffer = new byte[_blockSize];
        _bufferHandle = GCHandle.Alloc(_readBuffer, GCHandleType.Pinned);

        // Store callback instances as class fields to prevent GC
        _openCallback = new ArchiveOpenCallback(OnOpen);
        _closeCallback = new ArchiveCloseCallback(OnClose);
        _readCallback = new ArchiveReadCallback(OnRead);

        // Keep the stream reference alive
        _callbackHandle = GCHandle.Alloc(_inputStream);

        var result = archive_read_open(
            handle,
            GCHandle.ToIntPtr(_callbackHandle.Value),
            _openCallback,
            _readCallback,
            _closeCallback);

        if (result != (int)ARCHIVE_RESULT.ARCHIVE_OK)
            Throw();
    }

    private int OnOpen(IntPtr archive, IntPtr clientData)
    {
        return (int)ARCHIVE_RESULT.ARCHIVE_OK;
    }

    private int OnClose(IntPtr archive, IntPtr clientData)
    {
        return (int)ARCHIVE_RESULT.ARCHIVE_OK;
    }

    private IntPtr OnRead(IntPtr archive, IntPtr clientData, out IntPtr buffer)
    {
        try
        {
            var stream = GCHandle.FromIntPtr(clientData).Target as Stream;
            if (stream == null || !stream.CanRead)
            {
                buffer = IntPtr.Zero;
                return new IntPtr(-1);
            }

            int bytesRead = stream.Read(_readBuffer!, 0, _readBuffer!.Length);

            if (bytesRead == 0)
            {
                buffer = IntPtr.Zero;
                return IntPtr.Zero; // EOF
            }

            buffer = _bufferHandle!.Value.AddrOfPinnedObject();
            return new IntPtr(bytesRead);
        }
        catch
        {
            buffer = IntPtr.Zero;
            return new IntPtr(-1);
        }
    }

    partial void CleanupCallbacks()
    {
        if (_bufferHandle.HasValue)
        {
            _bufferHandle.Value.Free();
            _bufferHandle = null;
        }
        _readBuffer = null;

        if (_callbackHandle.HasValue)
        {
            _callbackHandle.Value.Free();
            _callbackHandle = null;
        }

        _openCallback = null;
        _closeCallback = null;
        _readCallback = null;
    }

    #endregion

    #region P/Invoke Declarations - Stream Reading

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial int archive_read_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveReadCallback? readCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#else
    [DllImport("archive")]
    private static extern int archive_read_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveReadCallback? readCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#endif

    #endregion
}
