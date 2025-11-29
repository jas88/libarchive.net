namespace LibArchive.Net;

#if NETSTANDARD2_0 || NET462
/// <summary>
/// Represents progress information when adding files to an archive.
/// </summary>
public class FileProgress
{
    /// <summary>
    /// The path of the file currently being processed.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Total bytes processed so far across all files.
    /// </summary>
    public long BytesProcessed { get; }

    /// <summary>
    /// Total bytes to process across all files.
    /// </summary>
    public long TotalBytes { get; }

    /// <summary>
    /// Zero-based index of the current file being processed.
    /// </summary>
    public int FileIndex { get; }

    /// <summary>
    /// Total number of files to process.
    /// </summary>
    public int TotalFiles { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="FileProgress"/> class.
    /// </summary>
    public FileProgress(string filePath, long bytesProcessed, long totalBytes, int fileIndex, int totalFiles)
    {
        FilePath = filePath;
        BytesProcessed = bytesProcessed;
        TotalBytes = totalBytes;
        FileIndex = fileIndex;
        TotalFiles = totalFiles;
    }

    /// <summary>
    /// Gets the percentage of bytes processed (0-100).
    /// </summary>
    public double PercentComplete => TotalBytes > 0 ? (BytesProcessed * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Gets a value indicating whether all files have been processed.
    /// </summary>
    public bool IsComplete => FileIndex >= TotalFiles && TotalFiles > 0;
}
#else
/// <summary>
/// Represents progress information when adding files to an archive.
/// </summary>
/// <param name="FilePath">The path of the file currently being processed.</param>
/// <param name="BytesProcessed">Total bytes processed so far across all files.</param>
/// <param name="TotalBytes">Total bytes to process across all files.</param>
/// <param name="FileIndex">Zero-based index of the current file being processed.</param>
/// <param name="TotalFiles">Total number of files to process.</param>
public record FileProgress(
    string FilePath,
    long BytesProcessed,
    long TotalBytes,
    int FileIndex,
    int TotalFiles)
{
    /// <summary>
    /// Gets the percentage of bytes processed (0-100).
    /// </summary>
    public double PercentComplete => TotalBytes > 0 ? (BytesProcessed * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Gets a value indicating whether all files have been processed.
    /// </summary>
    public bool IsComplete => FileIndex >= TotalFiles && TotalFiles > 0;
}
#endif
