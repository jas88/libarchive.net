namespace LibArchive.Net;

/// <summary>
/// Specifies the compression algorithm (filter) to apply to the archive.
/// </summary>
public enum CompressionType
{
    /// <summary>
    /// No compression.
    /// </summary>
    None,

    /// <summary>
    /// Gzip compression - widely supported, good compression/speed balance.
    /// Common file extension: .gz
    /// </summary>
    Gzip,

    /// <summary>
    /// Bzip2 compression - better compression than gzip, slower.
    /// Common file extension: .bz2
    /// </summary>
    Bzip2,

    /// <summary>
    /// XZ compression - excellent compression ratio, moderate speed.
    /// Uses LZMA2 algorithm.
    /// Common file extension: .xz
    /// </summary>
    Xz,

    /// <summary>
    /// LZMA compression - excellent compression ratio.
    /// Common file extension: .lzma
    /// </summary>
    Lzma,

    /// <summary>
    /// LZ4 compression - extremely fast, moderate compression.
    /// Common file extension: .lz4
    /// </summary>
    Lz4,

    /// <summary>
    /// Zstd (Zstandard) compression - excellent speed/ratio balance, modern algorithm.
    /// Common file extension: .zst
    /// </summary>
    Zstd,

    /// <summary>
    /// Compress (LZW) compression - legacy Unix compression.
    /// Common file extension: .Z
    /// </summary>
    Compress,

    /// <summary>
    /// LZIP compression - LZMA-based with integrity checking.
    /// Common file extension: .lz
    /// </summary>
    Lzip,

    /// <summary>
    /// DEFLATE compression - Used internally by ZIP and gzip.
    /// </summary>
    Deflate,
}
