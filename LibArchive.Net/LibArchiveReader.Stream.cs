using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LibArchive.Net;

public partial class LibArchiveReader
{
    #region Stream-based Reading

    private Stream? _inputStream;
    private readonly long _streamOrigin;
    private GCHandle? _callbackHandle;
    private byte[]? _readBuffer;
    private GCHandle? _bufferHandle;

    // Callback delegates for stream reading
    private delegate int ArchiveOpenCallback(IntPtr archive, IntPtr clientData);
    private delegate int ArchiveCloseCallback(IntPtr archive, IntPtr clientData);
    private delegate IntPtr ArchiveReadCallback(IntPtr archive, IntPtr clientData, out IntPtr buffer);
    private delegate long ArchiveSeekCallback(IntPtr archive, IntPtr clientData, long offset, int whence);

    // Delegate instances must be kept alive to prevent GC while native code holds references
    private ArchiveOpenCallback? _openCallback;
    private ArchiveCloseCallback? _closeCallback;
    private ArchiveReadCallback? _readCallback;
    private ArchiveSeekCallback? _seekCallback;

    /// <summary>
    /// Creates a new archive reader that reads from a stream.
    /// </summary>
    /// <param name="inputStream">The stream to read the archive from. Must be readable.</param>
    /// <param name="password">Optional password for encrypted archives.</param>
    /// <param name="blockSize">Block size in bytes for reading (default: 1 MiB).</param>
    /// <remarks>
    /// The stream should be positioned at the start of the archive data.
    /// Its initial position is treated as archive offset zero, including after Reset().
    /// Seeking within formats such as 7z, and Reset(), require a seekable stream (CanSeek = true).
    /// </remarks>
    public LibArchiveReader(Stream inputStream, string? password = null, uint blockSize = 1 << 20)
        : base(true)
    {
        if (inputStream == null)
            throw new ArgumentNullException(nameof(inputStream));
        if (!inputStream.CanRead)
            throw new ArgumentException("Stream must be readable", nameof(inputStream));

        _inputStream = inputStream;
        _streamOrigin = inputStream.CanSeek ? inputStream.Position : 0;
        _password = password;
        _blockSize = blockSize;
        _sourceType = SourceType.Stream;

        InitializeFromStream();
    }

    private void InitializeFromStream()
    {
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
        if (_inputStream == null)
            throw new InvalidOperationException("No input stream available");

        // Allocate read buffer
        _readBuffer = new byte[_blockSize];
        _bufferHandle = GCHandle.Alloc(_readBuffer, GCHandleType.Pinned);

        // Store callback instances as class fields to prevent GC
        _openCallback = new ArchiveOpenCallback(OnOpen);
        _closeCallback = new ArchiveCloseCallback(OnClose);
        _readCallback = new ArchiveReadCallback(OnRead);

        // Keep the stream reference alive
        _callbackHandle = GCHandle.Alloc(_inputStream);

        if (_inputStream.CanSeek)
        {
            _seekCallback = OnSeek;
            if (archive_read_set_seek_callback(handle, _seekCallback) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }

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

    private long OnSeek(IntPtr archive, IntPtr clientData, long offset, int whence)
    {
        try
        {
            var stream = GCHandle.FromIntPtr(clientData).Target as Stream;
            if (stream == null || !stream.CanSeek)
                return (long)ARCHIVE_RESULT.ARCHIVE_FAILED;

            long basis;
            switch (whence)
            {
                case 0: // SEEK_SET
                    basis = _streamOrigin;
                    break;
                case 1: // SEEK_CUR
                    basis = stream.Position;
                    break;
                case 2: // SEEK_END
                    basis = stream.Length;
                    break;
                default:
                    return (long)ARCHIVE_RESULT.ARCHIVE_FAILED;
            }

            long target;
            try
            {
                target = checked(basis + offset);
            }
            catch (OverflowException)
            {
                return (long)ARCHIVE_RESULT.ARCHIVE_FAILED;
            }

            if (target < _streamOrigin || target > stream.Length)
                return (long)ARCHIVE_RESULT.ARCHIVE_FAILED;

            stream.Seek(target, SeekOrigin.Begin);

            // Use Position instead of the value returned by Seek.
            // https://learn.microsoft.com/en-us/dotnet/api/system.io.memorystream.seek?view=netframework-4.8.1#remarks
            return checked(stream.Position - _streamOrigin);
        }
        catch
        {
            return (long)ARCHIVE_RESULT.ARCHIVE_FATAL;
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
        _seekCallback = null;
    }

    #endregion

    #region P/Invoke Declarations - Stream Reading

#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    private static partial int archive_read_set_seek_callback(
        IntPtr archive,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveSeekCallback seekCallback);

    [LibraryImport("archive")]
    private static partial int archive_read_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveReadCallback? readCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#else
    [DllImport("archive")]
    private static extern int archive_read_set_seek_callback(
        IntPtr archive,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveSeekCallback seekCallback);

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
