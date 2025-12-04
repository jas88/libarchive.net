using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;

namespace LibArchive.Net;

public partial class LibArchiveWriter
{
    #region Single File Addition

    /// <summary>
    /// Adds a single file to the archive.
    /// </summary>
    /// <param name="sourcePath">Path to the file on disk.</param>
    /// <param name="archivePath">Path to use within the archive. If null, uses the filename only.</param>
    public void AddFile(string sourcePath, string? archivePath = null)
    {
        var fileInfo = new FileInfo(sourcePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"File not found: {sourcePath}");

        archivePath ??= fileInfo.Name;
        WriteFileZeroCopy(fileInfo, archivePath);
    }

    /// <summary>
    /// Adds a file with the given data to the archive.
    /// </summary>
    /// <param name="archivePath">Path to use within the archive.</param>
    /// <param name="data">File content as byte array.</param>
    /// <param name="modificationTime">Optional modification time. If null, uses current time.</param>
    public void AddEntry(string archivePath, byte[] data, DateTime? modificationTime = null)
    {
        if (string.IsNullOrEmpty(archivePath))
            throw new ArgumentException("Archive path cannot be null or empty", nameof(archivePath));
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        WriteEntry(archivePath, data.Length, modificationTime ?? DateTime.UtcNow, 0644, stream =>
        {
            unsafe
            {
                fixed (byte* ptr = data)
                {
                    int offset = 0;
                    int remaining = data.Length;
                    while (remaining > 0)
                    {
                        nint written = archive_write_data(handle, ptr + offset, remaining);
                        if (written < 0)
                            Throw();
                        if (written == 0)
                            throw new ApplicationException("archive_write_data returned 0 bytes written");
                        offset += (int)written;
                        remaining -= (int)written;
                    }
                }
            }
        });
    }

    /// <summary>
    /// Adds a directory entry to the archive.
    /// </summary>
    /// <param name="archivePath">Path to use within the archive.</param>
    public void AddDirectoryEntry(string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath))
            throw new ArgumentException("Archive path cannot be null or empty", nameof(archivePath));

        // Ensure directory path ends with /
        if (!archivePath.EndsWith("/"))
            archivePath += "/";

        var entry = archive_entry_new();
        try
        {
            using var pathBuffer = new SafeStringBuffer(archivePath);
            archive_entry_set_pathname(entry, pathBuffer.Ptr);
            archive_entry_set_filetype(entry, AE_IFDIR);
            archive_entry_set_perm(entry, 0755); // rwxr-xr-x
            archive_entry_set_size(entry, 0);

            var (seconds, nanoseconds) = ToUnixTime(DateTime.UtcNow);
            archive_entry_set_mtime(entry, seconds, nanoseconds);

            if (archive_write_header(handle, entry) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();

            if (archive_write_finish_entry(handle) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }
        finally
        {
            archive_entry_free(entry);
        }
    }

    #endregion

    #region Batch File Addition

    /// <summary>
    /// Adds multiple files to the archive with zero-copy I/O optimization.
    /// </summary>
    /// <param name="files">Files to add to the archive.</param>
    /// <param name="pathMapper">Function to map FileInfo to archive path. If null, uses filename only.</param>
    /// <param name="progress">Optional progress reporter.</param>
    public void AddFiles(
        IEnumerable<FileInfo> files,
        Func<FileInfo, string>? pathMapper = null,
        IProgress<FileProgress>? progress = null)
    {
        if (files == null)
            throw new ArgumentNullException(nameof(files));

        pathMapper ??= f => f.Name;

        // Materialize for progress tracking
        var fileList = files as IList<FileInfo> ?? files.ToList();
        int totalFiles = fileList.Count;
        long totalBytes = fileList.Sum(f => f.Exists ? f.Length : 0);
        long processedBytes = 0;

        for (int i = 0; i < totalFiles; i++)
        {
            var file = fileList[i];

            if (!file.Exists)
                throw new FileNotFoundException($"File not found: {file.FullName}");

            // Report progress before processing
            progress?.Report(new FileProgress(
                file.FullName,
                processedBytes,
                totalBytes,
                i,
                totalFiles));

            var archivePath = pathMapper(file);
            WriteFileZeroCopy(file, archivePath);

            processedBytes += file.Length;
        }

        // Final progress report
        progress?.Report(new FileProgress(
            string.Empty,
            processedBytes,
            totalBytes,
            totalFiles,
            totalFiles));
    }

    /// <summary>
    /// Adds all files from a directory to the archive.
    /// </summary>
    /// <param name="directoryPath">Path to the directory.</param>
    /// <param name="recursive">Whether to include subdirectories.</param>
    /// <param name="searchPattern">File pattern to match (e.g., "*.txt").</param>
    /// <param name="filter">Optional filter function to exclude files.</param>
    /// <param name="preserveDirectoryStructure">Whether to preserve the directory structure in the archive.</param>
    /// <param name="progress">Optional progress reporter.</param>
    public void AddDirectory(
        string directoryPath,
        bool recursive = true,
        string searchPattern = "*",
        Func<FileInfo, bool>? filter = null,
        bool preserveDirectoryStructure = true,
        IProgress<FileProgress>? progress = null)
    {
        var dirInfo = new DirectoryInfo(directoryPath);
        if (!dirInfo.Exists)
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = dirInfo.EnumerateFiles(searchPattern, searchOption);

        if (filter != null)
            files = files.Where(filter);

        Func<FileInfo, string> pathMapper = preserveDirectoryStructure
            ? file => PreserveStructureMapper(directoryPath, file)
            : file => file.Name;

        AddFiles(files, pathMapper, progress);
    }

    #endregion

    #region Zero-Copy I/O Implementation

    private unsafe void WriteFileZeroCopy(FileInfo fileInfo, string archivePath)
    {
        var entry = archive_entry_new();
        try
        {
            // Set entry metadata
            SetEntryMetadata(entry, fileInfo, archivePath);

            // Write header
            if (archive_write_header(handle, entry) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();

            // Write data using optimal strategy based on file size
            if (fileInfo.Length == 0)
            {
                // Empty file - just header, no data
            }
#if NET9_0_OR_GREATER
            else if (fileInfo.Length <= SMALL_FILE_THRESHOLD)
            {
                WriteWithNet9Span(fileInfo);
            }
#endif
            else if (fileInfo.Length >= MEMORY_MAP_THRESHOLD)
            {
                WriteWithMemoryMappedFile(fileInfo);
            }
            else
            {
                WriteWithPooledBuffer(fileInfo);
            }

            // Finish entry
            if (archive_write_finish_entry(handle) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }
        finally
        {
            archive_entry_free(entry);
        }
    }

#if NET9_0_OR_GREATER
    private unsafe void WriteWithNet9Span(FileInfo fileInfo)
    {
        const int stackallocThreshold = 85000; // Just below LOH threshold

        if (fileInfo.Length <= stackallocThreshold)
        {
            // Small files - use stack allocation for maximum performance
            Span<byte> buffer = stackalloc byte[(int)fileInfo.Length];

            using var fileHandle = File.OpenHandle(
                fileInfo.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);

            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int bytesRead = RandomAccess.Read(fileHandle, buffer.Slice(totalRead), totalRead);
                if (bytesRead == 0)
                    throw new EndOfStreamException($"Unexpected end of file: {fileInfo.FullName}");
                totalRead += bytesRead;
            }

            fixed (byte* ptr = buffer)
            {
                int offset = 0;
                int remaining = buffer.Length;
                while (remaining > 0)
                {
                    nint written = archive_write_data(handle, ptr + offset, remaining);
                    if (written < 0)
                        Throw();
                    if (written == 0)
                        throw new ApplicationException("archive_write_data returned 0 bytes written");
                    offset += (int)written;
                    remaining -= (int)written;
                }
            }
        }
        else
        {
            // Medium files - use ArrayPool with chunked reading
            const int chunkSize = 1 << 20; // 1 MB chunks
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

            try
            {
                using var fileHandle = File.OpenHandle(
                    fileInfo.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.SequentialScan);

                long offset = 0;
                long remaining = fileInfo.Length;

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, chunkSize);
                    var span = buffer.AsSpan(0, toRead);

                    int bytesRead = RandomAccess.Read(fileHandle, span, offset);
                    if (bytesRead <= 0)
                        break;

                    fixed (byte* ptr = span.Slice(0, bytesRead))
                    {
                        int writeOffset = 0;
                        int writeRemaining = bytesRead;
                        while (writeRemaining > 0)
                        {
                            nint written = archive_write_data(handle, ptr + writeOffset, writeRemaining);
                            if (written < 0)
                                Throw();
                            if (written == 0)
                                throw new ApplicationException("archive_write_data returned 0 bytes written");
                            writeOffset += (int)written;
                            writeRemaining -= (int)written;
                        }
                    }

                    offset += bytesRead;
                    remaining -= bytesRead;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
#endif

    private unsafe void WriteWithMemoryMappedFile(FileInfo fileInfo)
    {
        using var mmf = MemoryMappedFile.CreateFromFile(
            fileInfo.FullName,
            FileMode.Open,
            null,
            0,
            MemoryMappedFileAccess.Read);

        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            long remaining = fileInfo.Length;
            long offset = 0;

            // Write in chunks to avoid int.MaxValue limitation
            while (remaining > 0)
            {
                int chunkSize = (int)Math.Min(remaining, int.MaxValue);

                int chunkOffset = 0;
                int chunkRemaining = chunkSize;
                while (chunkRemaining > 0)
                {
                    nint written = archive_write_data(handle, ptr + offset + chunkOffset, chunkRemaining);
                    if (written < 0)
                        Throw();
                    if (written == 0)
                        throw new ApplicationException("archive_write_data returned 0 bytes written");
                    chunkOffset += (int)written;
                    chunkRemaining -= (int)written;
                }

                offset += chunkSize;
                remaining -= chunkSize;
            }
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private unsafe void WriteWithPooledBuffer(FileInfo fileInfo)
    {
        const int bufferSize = 1 << 20; // 1 MB
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            using var fs = new FileStream(
                fileInfo.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);

            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                fixed (byte* ptr = buffer)
                {
                    int writeOffset = 0;
                    int writeRemaining = read;
                    while (writeRemaining > 0)
                    {
                        nint written = archive_write_data(handle, ptr + writeOffset, writeRemaining);
                        if (written < 0)
                            Throw();
                        if (written == 0)
                            throw new ApplicationException("archive_write_data returned 0 bytes written");
                        writeOffset += (int)written;
                        writeRemaining -= (int)written;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Helper Methods

    private void SetEntryMetadata(IntPtr entry, FileInfo fileInfo, string archivePath)
    {
        using var pathBuffer = new SafeStringBuffer(archivePath);
        archive_entry_set_pathname(entry, pathBuffer.Ptr);
        archive_entry_set_size(entry, fileInfo.Length);
        archive_entry_set_filetype(entry, AE_IFREG);
        archive_entry_set_perm(entry, GetUnixPermissions(fileInfo));

        var (seconds, nanoseconds) = ToUnixTime(fileInfo.LastWriteTimeUtc);
        archive_entry_set_mtime(entry, seconds, nanoseconds);
    }

    private void WriteEntry(string archivePath, long size, DateTime modificationTime, int permissions, Action<Stream> writeData)
    {
        var entry = archive_entry_new();
        try
        {
            using var pathBuffer = new SafeStringBuffer(archivePath);
            archive_entry_set_pathname(entry, pathBuffer.Ptr);
            archive_entry_set_size(entry, size);
            archive_entry_set_filetype(entry, AE_IFREG);
            archive_entry_set_perm(entry, permissions);

            var (seconds, nanoseconds) = ToUnixTime(modificationTime);
            archive_entry_set_mtime(entry, seconds, nanoseconds);

            if (archive_write_header(handle, entry) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();

            writeData(null!); // Stream parameter not used in current implementation

            if (archive_write_finish_entry(handle) != (int)ARCHIVE_RESULT.ARCHIVE_OK)
                Throw();
        }
        finally
        {
            archive_entry_free(entry);
        }
    }

    private static int GetUnixPermissions(FileInfo fileInfo)
    {
        // Default: rw-r--r-- (0644) for files
        int permissions = 0644;

        // If read-only, remove write permissions
        if (fileInfo.Attributes.HasFlag(FileAttributes.ReadOnly))
            permissions = 0444;

        // TODO: On Unix platforms, could use Mono.Unix or P/Invoke to get actual permissions
        // For now, use sensible defaults

        return permissions;
    }

    private static (long seconds, long nanoseconds) ToUnixTime(DateTime dateTime)
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var diff = dateTime.ToUniversalTime() - epoch;
        long seconds = (long)diff.TotalSeconds;
        long nanoseconds = (diff.Ticks % TimeSpan.TicksPerSecond) * 100; // Convert ticks to nanoseconds
        return (seconds, nanoseconds);
    }

    private static string PreserveStructureMapper(string baseDirectory, FileInfo file)
    {
        var fullPath = file.FullName;
        var basePath = Path.GetFullPath(baseDirectory);

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = fullPath.Substring(basePath.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Convert Windows backslashes to forward slashes for archive compatibility
            return relativePath.Replace(Path.DirectorySeparatorChar, '/');
        }

        return file.Name;
    }

    #endregion
}
