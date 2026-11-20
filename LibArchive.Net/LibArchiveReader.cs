using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
namespace LibArchive.Net;

/// <summary>
/// Provides read-only access to archive files using the libarchive library.
/// Supports various formats including zip, rar, 7zip, tar, gzip, bzip2, lzop (decoded by lzokay), and lzma.
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

    // Format codes, as archive_format() reports them and archive_read_set_format() takes them (archive.h)
    private const int ARCHIVE_FORMAT_BASE_MASK = 0xff0000;
    private const int ARCHIVE_FORMAT_MTREE = 0x80000;
    private const int ARCHIVE_FORMAT_RAW = 0x90000;

    // The formats a native reader is given: see Open() and ReadNextHeader()
    private enum ReaderFormats
    {
        // Every archive format
        All,
        // Every archive format, and the raw format, which bids only when none of them does
        AllAndRaw,
        // The raw format alone, set as the reader's format, so that formats do not bid at all
        RawOnly
    }

    // Set by Open(), and cleared once the first header has been read: see ReadNextHeader()
    private bool _firstHeaderPending;

    // Fields to store constructor parameters for Reset() support
    private readonly string? _filename;
    private readonly string[]? _filenames;
    private readonly uint _blockSize;
    private readonly string? _password;

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

    /// <summary>
    /// Opens the specified archive file for read access with the specified block size.
    /// </summary>
    /// <param name="filename">The path to the archive file.</param>
    /// <param name="blockSize">Block size in bytes for reading, default is 1 MiB (1048576 bytes).</param>
    /// <param name="password">Optional password for encrypted archives. Supports ZIP archives with traditional PKWARE or AES encryption. RAR and 7z encrypted archives are not supported by libarchive.</param>
    /// <remarks>
    /// A single compressed file that is not an archive, such as a plain <c>.gz</c>, <c>.bz2</c>
    /// or <c>.xz</c> file, is read as an archive holding one entry; see <see cref="Entries"/>. An
    /// empty file opens as an archive with no entries, and so does a compressed file whose
    /// decompressed data is empty.
    /// </remarks>
    /// <exception cref="ApplicationException">Thrown when the archive cannot be opened, including when the file is
    /// not empty and is neither an archive nor compressed (message "Unrecognized archive format").</exception>
    /// <exception cref="NotSupportedException">Thrown when attempting to read an encrypted RAR or 7z archive (not supported by libarchive).</exception>
    public LibArchiveReader(string filename, uint blockSize = 1<<20, string? password = null) : base(true)
    {
        _filename = filename;
        _blockSize = blockSize;
        _password = password;
        _sourceType = SourceType.File;
        Open();
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
        Open();
    }

    // Partial method for cleanup - implemented in LibArchiveReader.Stream.cs
    partial void CleanupCallbacks();

    private void Throw()
    {
        throw new ApplicationException(ErrorString());
    }

    private string ErrorString() => SafeStringBuffer.PtrToStringUTF8(archive_error_string(handle)) ?? "Unknown error";

    /// <summary>
    /// Creates the native reader and opens the source with every filter and archive format.
    /// </summary>
    /// <remarks>
    /// A plain compressed file, such as a <c>.gz</c> that is not a <c>.tar.gz</c>, holds no archive
    /// format, so every format rejects it. When that happens to compressed data, the source is
    /// opened again with libarchive's raw format as well, which reads the decompressed data as a
    /// single entry. The raw format is not simply registered with the others because it accepts
    /// any data, and libarchive only reports the format it chose once a header has been read: data
    /// that is neither an archive nor compressed would then open without error. Such data still
    /// fails here with libarchive's own error, "Unrecognized archive format", unless it is empty:
    /// libarchive's empty format reads empty data as an archive with no entries. It also claims
    /// compressed data that decompresses to nothing, which therefore opens here with no entries
    /// as well: the raw format could not give it an entry, as it needs at least one byte
    /// (archive_read_support_format_empty.c:67-68, archive_read_support_format_raw.c:99-100).
    /// Compressed text can also be claimed by the mtree format and fail only at its first header;
    /// <see cref="ReadNextHeader"/> then reads it as raw data.
    /// </remarks>
    private void Open()
    {
        CreateNativeReader(ReaderFormats.All);
        if (OpenSource() != (int)ARCHIVE_RESULT.ARCHIVE_OK)
        {
            var error = ErrorString();
            if (!IsCompressed() || !ReopenCompressed(ReaderFormats.AllAndRaw))
                throw new ApplicationException(error);
        }
        _firstHeaderPending = true;
    }

    // Frees the native reader and opens the source again from its start, with the given formats.
    // True when it opens, and as compressed data, as it did the first time.
    private bool ReopenCompressed(ReaderFormats formats)
    {
        archive_read_free(handle);
        handle = IntPtr.Zero;
        if (!RewindSource())
            return false;
        CreateNativeReader(formats);
        return OpenSource() == (int)ARCHIVE_RESULT.ARCHIVE_OK && IsCompressed();
    }

    private void CreateNativeReader(ReaderFormats formats)
    {
        handle = archive_read_new();
        if (handle == IntPtr.Zero)
            throw new ApplicationException("Failed to create archive reader");

        // The *_all functions register what they can and only warn when a filter needs an
        // external program, which libarchive then reports when it is used; their results are ignored.
        _ = archive_read_support_filter_all(handle);
        if (formats == ReaderFormats.RawOnly)
        {
            // Registers the raw format and makes it the reader's format: libarchive then only
            // checks that there is data, and no other format can claim it
            if (archive_read_set_format(handle, ARCHIVE_FORMAT_RAW) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }
        else
        {
            _ = archive_read_support_format_all(handle);
            if (formats == ReaderFormats.AllAndRaw && archive_read_support_format_raw(handle) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }

        if (_password != null)
        {
            using var uPassword = new SafeStringBuffer(_password);
            if (archive_read_add_passphrase(handle, uPassword.Ptr) != 0)
                Throw();
        }
    }

    // Archive files are opened by their UTF-16 name on Windows, with the _w variants of
    // libarchive's open functions. There archive_read_open_filename gives a narrow name to
    // libarchive's open() (archive_windows.c:288-400), which passes it to GetFileAttributesA(),
    // _sopen_s() and mbstowcs(): these take it to be in the ANSI code page or the C runtime's
    // locale, not UTF-8. LibArchiveWriter opens files the same way. Elsewhere the bytes go to
    // open(2) unchanged, and UTF-8 is what .NET itself uses for file names, so the narrow
    // functions are used there.
    internal static readonly bool UseWideFileNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private int OpenSource()
    {
        switch (_sourceType)
        {
            case SourceType.File:
            {
                if (UseWideFileNames)
                    return archive_read_open_filename_w(handle, _filename!, _blockSize);
                using var uName = new SafeStringBuffer(_filename!);
                return archive_read_open_filename(handle, uName.Ptr, _blockSize);
            }
            case SourceType.MultiVolume:
            {
                if (UseWideFileNames)
                {
                    using var wideNames = new DisposableWideStringArray(_filenames!);
                    return archive_read_open_filenames_w(handle, wideNames.Ptr, _blockSize);
                }
                using var names = new DisposableStringArray(_filenames!);
                return archive_read_open_filenames(handle, names.Ptr, _blockSize);
            }
            default:
                return OpenStream();
        }
    }

    // Prepares the source to be read again from its start by a second native reader.
    private bool RewindSource()
    {
        CleanupCallbacks();
        return _sourceType != SourceType.Stream || RewindStream();
    }

    // True when libarchive put a decompression filter (gzip, bzip2, xz and so on) in front of
    // the client reader, which is always the last filter in the chain. This also holds after a
    // failed open, until the native reader is freed.
    private bool IsCompressed() => archive_filter_count(handle) > 1;

    /// <summary>
    /// Reads the next header. When the first header of compressed data fails because libarchive's
    /// mtree format claimed text that is not an mtree specification, the source is opened again
    /// with the raw format alone, and the header read is that of its single entry.
    /// </summary>
    /// <remarks>
    /// Even without a <c>#mtree</c> signature, the mtree format bids on text whose first lines each
    /// hold one word, taking them for file names. A compressed list of words or numbers, such as
    /// <c>seq 1 1000 | gzip</c>, therefore opens as mtree rather than as raw data (see
    /// <see cref="Open"/>), and fails only at its first header, with "Missing type keyword in mtree
    /// specification". Registering the raw format with the others would not help: it bids only
    /// when no other format does. The fallback is limited to:
    /// <list type="bullet">
    /// <item>The first header after <see cref="Open"/>. Once an entry has been returned, a header
    /// that fails is an error in a specification that has been read, and is reported.</item>
    /// <item>Compressed data, as in <see cref="Open"/>. Uncompressed text still fails as before.</item>
    /// <item>The mtree format. A format is reported only by its own read_header, and mtree's reports
    /// it only after parsing the whole text, so the failure is never another format's. What can
    /// still fail is the first entry's keywords (a missing type, or a malformed or unknown keyword),
    /// which mtree reports with ARCHIVE_WARN, and this class as an error. Such text is then treated
    /// as <see cref="Open"/> treats compressed text that mtree's bid rejects outright.</item>
    /// </list>
    /// Text that the mtree format cannot parse at all, for example because a later line holds a byte
    /// that is not printable ASCII, fails before the format is reported, and still fails.
    /// </remarks>
    private int ReadNextHeader(out IntPtr entryHandle)
    {
        var r = archive_read_next_header(handle, out entryHandle);
        if (!_firstHeaderPending)
            return r;

        _firstHeaderPending = false;
        if (r != (int)ARCHIVE_RESULT.ARCHIVE_OK && r != (int)ARCHIVE_RESULT.ARCHIVE_EOF
            && (archive_format(handle) & ARCHIVE_FORMAT_BASE_MASK) == ARCHIVE_FORMAT_MTREE
            && IsCompressed())
        {
            var error = ErrorString();
            if (!ReopenCompressed(ReaderFormats.RawOnly)
                || (r = archive_read_next_header(handle, out entryHandle)) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                throw new ApplicationException(error);
        }
        StopRecording();
        return r;
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
        // archive_read_free calls a stream's close callback, so the callbacks must outlive it:
        // a delegate collected before then would be called all the same
        var result = archive_read_free(handle);
        CleanupCallbacks();
        return result == 0;
    }

    /// <summary>
    /// Enumerates all entries in the archive.
    /// </summary>
    /// <returns>An enumerable collection of archive entries.</returns>
    /// <remarks>
    /// A single compressed file that is not an archive, such as a plain <c>.gz</c>, <c>.bz2</c>,
    /// <c>.xz</c>, <c>.lzma</c>, <c>.zst</c>, <c>.lz4</c> or <c>.lzo</c> file, yields one entry holding
    /// the decompressed data. libarchive names that entry <c>data</c>, except that a gzip file which
    /// records its original name (<c>gzip file.txt</c> does unless given <c>-n</c>) gives that name.
    /// The entry is a regular file whose <see cref="Entry.LengthBytes"/> is null, because the
    /// decompressed size is not known in advance. This includes compressed text that libarchive
    /// takes for an mtree specification but cannot read as one, such as a list of words or numbers,
    /// one per line; a compressed mtree specification that it can read is read as one. A
    /// compressed file whose decompressed data is empty, such as a gzip of an empty file, yields no
    /// entry, as empty data does: libarchive reads both as an empty archive.
    /// </remarks>
    public IEnumerable<Entry> Entries()
    {
        int r;
        while ((r=ReadNextHeader(out var entryHandle))==0)
            yield return Entry.Create(entryHandle, handle);

        if (r != (int)ARCHIVE_RESULT.ARCHIVE_EOF)
            Throw();
    }

    /// <summary>
    /// Resets the archive reader to the beginning, allowing entries to be enumerated again.
    /// </summary>
    /// <remarks>
    /// For file-based archives, this closes and reopens the file.
    /// For stream-based archives, the stream must be seekable (CanSeek = true).
    /// Reset returns to the stream position captured when the reader was constructed.
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
        if (_sourceType == SourceType.Stream)
            ReopenStream();
        else
            Open();
    }

    private void ReopenStream()
    {
        if (_inputStream == null)
            throw new InvalidOperationException("No input stream available");

        if (!_inputStream.CanSeek)
            throw new NotSupportedException("Cannot reset: the underlying stream is not seekable. Use a seekable stream or reopen from file.");

        _inputStream.Seek(_streamOrigin, SeekOrigin.Begin);
        InitializeFromStream();
    }

    /// <summary>
    /// Gets the first entry in the archive without consuming the iterator.
    /// </summary>
    /// <returns>The first entry, or null if the archive is empty.</returns>
    /// <remarks>
    /// This is a convenience method for archives containing a single file, and for a single
    /// compressed file such as a plain <c>.gz</c>, which is read as one entry (see <see cref="Entries"/>).
    /// The entry's stream must be consumed before calling other methods on the reader.
    /// Unlike using Entries().First(), this method does not advance past the entry.
    /// </remarks>
    public Entry? FirstEntry()
    {
        int r = ReadNextHeader(out var entryHandle);
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
        /// <remarks>
        /// <para>A name whose character set the archive declares is decoded from it, and normalized
        /// to NFC (composed) by libarchive: pax <c>path</c> records, ZIP entries with the UTF-8 flag
        /// (or an Info-ZIP Unicode path field), XAR and Joliet names, and 7-Zip names.</para>
        /// <para>A name in a format that declares no character set, such as a ustar, GNU tar or cpio
        /// header, a ZIP entry without the UTF-8 flag, or a Rock Ridge name in an ISO 9660 image,
        /// is read as UTF-8, as it is stored (it is not normalized). A byte sequence in it that is
        /// not valid UTF-8, as in an archive written in a legacy code page, becomes U+FFFD.</para>
        /// <para>Names are read the same way on every platform, whatever the locale of the
        /// process.</para>
        /// <para>An entry is returned even if libarchive can give no name for it: the name is then
        /// empty.</para>
        /// </remarks>
        public string Name { get; }
        /// <summary>
        /// Gets or sets the type of the entry.
        /// </summary>
        public EntryType Type;
        /// <summary>
        /// Gets the extracted length, in bytes, of the entry, or null if the size is not known.
        /// </summary>
        public long? LengthBytes { get; }
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
            Name = GetPathname(entryHandle);
            Type = (EntryType)archive_entry_filetype(entryHandle);
            LengthBytes = archive_entry_size_is_set(entryHandle) != 0
                ? archive_entry_size(entryHandle)
                : null;
        }

        // A format reader keeps a name either decoded from the character set its format declares
        // (normalized to NFC), or, where the format declares none, as the bytes in the archive
        // (see Name). On every platform archive_entry_pathname returns the former as UTF-8 and the
        // latter unchanged (native/libarchive-narrow-utf8.patch makes libarchive's narrow strings
        // UTF-8, and leaves names that declare no character set unconverted, Windows included), so
        // its bytes are read as UTF-8, with U+FFFD for any invalid sequence. Without any form, as
        // when a declared name fails to decode, the name is empty, so that the entry is still
        // returned.
        private static string GetPathname(IntPtr entry)
            => SafeStringBuffer.PtrToStringUTF8(archive_entry_pathname(entry)) ?? string.Empty;

        internal static Entry Create(IntPtr entryHandle, IntPtr archiveHandle) => new(entryHandle, archiveHandle);

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
        private readonly long? _length;
        private bool _eof;

        internal FileStream(IntPtr archiveHandle, long? lengthBytes)
        {
            this.archiveHandle = archiveHandle;
            _length = lengthBytes;
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
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (_eof)
                return 0;

            if (count == 0)
                return 0;

            nint result = archive_read_data(archiveHandle, ref buffer[offset], (nuint)count);
            if (result < 0)
            {
                // Negative return indicates error (e.g., wrong password, encryption not supported)
                throw new ApplicationException(
                    SafeStringBuffer.PtrToStringUTF8(archive_error_string(archiveHandle))
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
        /// <summary>
        /// Gets the length in bytes of the stream, if known.
        /// </summary>
        /// <exception cref="NotSupportedException">Thrown when the entry size is not known.</exception>
        public override long Length => _length ?? throw new NotSupportedException("Entry size is not known");
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
    // libarchive's __LA_MODE_T is unsigned short on Windows and mode_t elsewhere, which is
    // 16 bits on macOS and 32 bits on Linux
    private static readonly bool ModeTIs32Bits = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static int archive_entry_filetype(IntPtr entry)
        => ModeTIs32Bits ? (int)archive_entry_filetype_u32(entry) : archive_entry_filetype_u16(entry);

    // Use DllImport for older frameworks (runtime marshalling)
#if NET7_0_OR_GREATER
    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial IntPtr archive_read_new();

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_support_filter_all(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_support_format_all(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_support_format_raw(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_set_format(IntPtr a, int code);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_filter_count(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_format(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_open_filename(IntPtr a, IntPtr filename, nuint blockSize);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_open_filenames(IntPtr a, IntPtr filename, nuint blockSize);

    // Windows only: wchar_t is UTF-16 there, and libarchive exports
    // archive_read_open_filenames_w on Windows alone
    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_open_filename_w(IntPtr a, [MarshalAs(UnmanagedType.LPWStr)] string filename, nuint blockSize);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_open_filenames_w(IntPtr a, IntPtr filenames, nuint blockSize);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial nint archive_read_data(IntPtr a, ref byte buff, nuint size);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_next_header(IntPtr a, out IntPtr entry);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial IntPtr archive_entry_pathname(IntPtr entry);

    [LibraryImport("archive", EntryPoint = "archive_entry_filetype")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial ushort archive_entry_filetype_u16(IntPtr entry);

    [LibraryImport("archive", EntryPoint = "archive_entry_filetype")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial uint archive_entry_filetype_u32(IntPtr entry);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial long archive_entry_size(IntPtr entry);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_entry_size_is_set(IntPtr entry);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_free(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial IntPtr archive_error_string(IntPtr a);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_add_passphrase(IntPtr a, IntPtr passphrase);

    [LibraryImport("archive")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int archive_read_has_encrypted_entries(IntPtr a);
#else
    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr archive_read_new();

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_support_filter_all(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_support_format_all(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_support_format_raw(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_set_format(IntPtr a, int code);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_filter_count(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_format(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_open_filename(IntPtr a, IntPtr filename, nuint blockSize);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_open_filenames(IntPtr a, IntPtr filename, nuint blockSize);

    // Windows only: wchar_t is UTF-16 there, and libarchive exports
    // archive_read_open_filenames_w on Windows alone
    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_open_filename_w(IntPtr a, [MarshalAs(UnmanagedType.LPWStr)] string filename, nuint blockSize);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_open_filenames_w(IntPtr a, IntPtr filenames, nuint blockSize);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint archive_read_data(IntPtr a, ref byte buff, nuint size);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_next_header(IntPtr a, out IntPtr entry);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr archive_entry_pathname(IntPtr entry);

    [DllImport("archive", EntryPoint = "archive_entry_filetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern ushort archive_entry_filetype_u16(IntPtr entry);

    [DllImport("archive", EntryPoint = "archive_entry_filetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint archive_entry_filetype_u32(IntPtr entry);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern long archive_entry_size(IntPtr entry);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_entry_size_is_set(IntPtr entry);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_free(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr archive_error_string(IntPtr a);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_add_passphrase(IntPtr a, IntPtr passphrase);

    [DllImport("archive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int archive_read_has_encrypted_entries(IntPtr a);
#endif

#if NETSTANDARD2_0
    // .NET Standard 2.0 Native Library Loading
    private static void PreloadNativeLibrary()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            PreloadWindowsLibrary();
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

    private static void PreloadWindowsLibrary()
    {
        // A RID-specific build or publish puts archive.dll next to this assembly, where the
        // assembly-wide DefaultDllImportSearchPaths(AssemblyDirectory) already finds it. An
        // assembly with no file location (single-file, Native AOT) has the application
        // directory treated as its assembly directory by the runtime, so do the same here.
        var assemblyDirectory = GetAssemblyDirectoryOrNull(Assembly.GetExecutingAssembly())
                                ?? AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(assemblyDirectory) && File.Exists(Path.Combine(assemblyDirectory, "archive.dll")))
            return;

        // Otherwise the package's .targets file has deployed it to runtimes\{rid}\native\.
        // .NET (Core) finds it there through the application's deps.json, but .NET Framework
        // never probes that folder. Load it by full path: when .NET Framework falls back from
        // the assembly directory to loading "archive" by name, Windows binds that to the
        // module which is already loaded.
        // If it is not there either, there is nothing to preload, and the imports resolve or
        // fail through the runtime's own probing exactly as they would without this method.
        // (Since .NET 10 that probing no longer includes the OS search, e.g. PATH.)
        if (TryGetNativeLibraryPath(out var libraryPath, out _))
            LoadWindowsLibrary(libraryPath);
    }

    private static string GetNativeLibraryPath()
    {
        if (TryGetNativeLibraryPath(out var libraryPath, out var notFoundMessage))
            return libraryPath;

        throw new DllNotFoundException(notFoundMessage);
    }

    // The directory an assembly was loaded from, or null if it has no file location.
    // Assembly.Location is an empty string when the assembly was not loaded from a file:
    // single-file and Native AOT apps, and Assembly.Load(byte[]). Path.GetDirectoryName must
    // never be given that empty string, because on .NET Framework it throws ArgumentException.
    private static string? GetAssemblyDirectoryOrNull(Assembly? assembly)
    {
        var location = assembly?.Location;
        return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
    }

    private static bool TryGetNativeLibraryPath(out string libraryPath, out string notFoundMessage)
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
            ("LibArchive.Net Assembly", GetAssemblyDirectoryOrNull(Assembly.GetExecutingAssembly())),
            ("Entry Assembly", GetAssemblyDirectoryOrNull(Assembly.GetEntryAssembly()))
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

            var candidatePath = Path.Combine(baseDir, "runtimes", rid, "native", filename);

            if (enableDiagnostics)
            {
                Console.Error.WriteLine($"[LibArchive.Net]     [{locName}] {candidatePath}");
                Console.Error.WriteLine($"[LibArchive.Net]       Exists: {File.Exists(candidatePath)}");
            }

            if (File.Exists(candidatePath))
            {
                if (enableDiagnostics)
                {
                    Console.Error.WriteLine($"[LibArchive.Net]   ✓ Found at: {candidatePath}");
                }
                libraryPath = candidatePath;
                notFoundMessage = string.Empty;
                return true;
            }
        }

        // Not found - build detailed error message
        var searchedPaths = searchLocations
            .Where(loc => !string.IsNullOrEmpty(loc.Item2))
            .Select(loc => $"  - [{loc.Item1}] {Path.Combine(loc.Item2, "runtimes", rid, "native", filename)}")
            .ToArray();

        libraryPath = string.Empty;
        notFoundMessage = $"Native library '{filename}' not found. Searched locations:\n{string.Join("\n", searchedPaths)}\n\n" +
                          $"Platform: {RuntimeInformation.OSDescription}\n" +
                          $"Architecture: {RuntimeInformation.ProcessArchitecture}\n" +
                          $"RID: {rid}\n\n" +
                          $"Tip: Set LIBARCHIVE_NET_DEBUG=1 environment variable for detailed diagnostics.";
        return false;
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

    private static void LoadWindowsLibrary(string libraryPath)
    {
        // LOAD_WITH_ALTERED_SEARCH_PATH (0x8): resolve the DLL's own dependencies relative to
        // its directory rather than the application's. Requires an absolute path.
        const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        var fullPath = Path.GetFullPath(libraryPath);
        var handle = LoadLibraryEx(fullPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new DllNotFoundException(
                $"Failed to load native library '{fullPath}'. Win32 error {error}: {new System.ComponentModel.Win32Exception(error).Message}");
        }

        if (Environment.GetEnvironmentVariable("LIBARCHIVE_NET_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[LibArchive.Net] Loaded {fullPath}");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

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

    // libdl is a system library, so these imports must not inherit the assembly-wide
    // DefaultDllImportSearchPaths(AssemblyDirectory): since .NET 10 that flag on its own
    // searches the assembly directory only, with no fallback to the OS search.
    // Linux: try multiple libdl versions for compatibility
    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr dlopen_linux_v2(string filename, int flags);

    [DllImport("libdl.so", EntryPoint = "dlopen")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr dlopen_linux(string filename, int flags);

    [DllImport("libdl.dylib", EntryPoint = "dlopen")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
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
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr dlerror_linux_v2();

    [DllImport("libdl.so", EntryPoint = "dlerror")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr dlerror_linux();

    [DllImport("libdl.dylib", EntryPoint = "dlerror")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
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
#endif
}
