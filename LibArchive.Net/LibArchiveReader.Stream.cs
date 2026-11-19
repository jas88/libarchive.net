using System;
using System.Collections.Generic;
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

    // A non-seekable stream cannot be read twice, so the chunks the first native reader reads
    // while opening it, and for compressed data while reading the first header, are recorded,
    // and replayed to a second native reader if Open() or ReadNextHeader() needs one.
    private List<byte[]>? _recordedChunks;
    private Queue<byte[]>? _replayChunks;

    // Callback delegates for stream reading
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArchiveOpenCallback(IntPtr archive, IntPtr clientData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArchiveCloseCallback(IntPtr archive, IntPtr clientData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ArchiveReadCallback(IntPtr archive, IntPtr clientData, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
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
    /// A single compressed file that is not an archive, such as a plain gzip stream, is read as
    /// an archive holding one entry (see <see cref="Entries"/>); for a non-seekable stream, the
    /// bytes read while detecting this are kept in memory until the reader has opened, and for
    /// compressed data until the first entry's header has been read. Empty data opens as an archive
    /// with no entries.
    /// </remarks>
    /// <exception cref="ApplicationException">Thrown when the archive cannot be opened, including when the
    /// data is not empty and is neither an archive nor compressed (message "Unrecognized archive
    /// format").</exception>
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
        try
        {
            Open();
        }
        catch
        {
            // Freed first: archive_read_free can call the close callback (see ReleaseHandle())
            if (handle != IntPtr.Zero)
                archive_read_free(handle);
            handle = IntPtr.Zero;
            CleanupCallbacks();
            _recordedChunks = null;
            _replayChunks = null;
            throw;
        }
    }

    // Opens the stream with the native reader that Open() created. While the first native reader
    // opens a non-seekable stream, the chunks it reads are recorded so that RewindStream() can
    // hand the same bytes to a second one. Once it has opened, only compressed data can need
    // that, if its first header fails (see ReadNextHeader()), so only that is still recorded.
    private int OpenStream()
    {
        _recordedChunks = _inputStream is { CanSeek: false } && _replayChunks == null
            ? new List<byte[]>()
            : null;

        var result = OpenWithCallbacks();
        if (result == (int)ARCHIVE_RESULT.ARCHIVE_OK && !IsCompressed())
            StopRecording();
        return result;
    }

    // Stops recording what a non-seekable stream gives the native reader, and drops the record.
    private void StopRecording() => _recordedChunks = null;

    // Returns the stream to the point where the first native reader started.
    private bool RewindStream()
    {
        if (_inputStream == null)
            return false;

        if (_inputStream.CanSeek)
        {
            _inputStream.Seek(_streamOrigin, SeekOrigin.Begin);
            return true;
        }

        if (_recordedChunks == null)
            return false;
        _replayChunks = new Queue<byte[]>(_recordedChunks);
        _recordedChunks = null;
        return true;
    }

    private int OpenWithCallbacks()
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

        return archive_read_open(
            handle,
            GCHandle.ToIntPtr(_callbackHandle.Value),
            _openCallback,
            _readCallback,
            _closeCallback);
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

            int bytesRead;
            if (_replayChunks is { Count: > 0 })
            {
                var chunk = _replayChunks.Dequeue();
                Buffer.BlockCopy(chunk, 0, _readBuffer!, 0, chunk.Length);
                bytesRead = chunk.Length;
            }
            else
            {
                bytesRead = stream.Read(_readBuffer!, 0, _readBuffer!.Length);
                if (bytesRead > 0 && _recordedChunks != null)
                {
                    var chunk = new byte[bytesRead];
                    Buffer.BlockCopy(_readBuffer!, 0, chunk, 0, bytesRead);
                    _recordedChunks.Add(chunk);
                }
            }

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
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_set_seek_callback(
        IntPtr archive,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveSeekCallback seekCallback);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveReadCallback? readCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#else
    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_set_seek_callback(
        IntPtr archive,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveSeekCallback seekCallback);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_open(
        IntPtr archive,
        IntPtr clientData,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveOpenCallback? openCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveReadCallback? readCallback,
        [MarshalAs(UnmanagedType.FunctionPtr)] ArchiveCloseCallback? closeCallback);
#endif

    #endregion
}
