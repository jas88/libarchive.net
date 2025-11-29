namespace LibArchive.Net;

/// <summary>
/// Specifies the archive format for writing.
/// </summary>
public enum ArchiveFormat
{
    /// <summary>
    /// ZIP format - widely supported, moderate compression.
    /// Supports encryption (AES-128, AES-192, AES-256, Traditional PKWARE).
    /// </summary>
    Zip,

    /// <summary>
    /// 7-Zip format - excellent compression ratio.
    /// Supports AES-256 encryption.
    /// </summary>
    SevenZip,

    /// <summary>
    /// TAR (Tape Archive) format - Unix standard, no compression or encryption built-in.
    /// Typically combined with compression filters (gzip, bzip2, xz).
    /// </summary>
    Tar,

    /// <summary>
    /// POSIX ustar format - TAR variant with extended metadata support.
    /// </summary>
    Ustar,

    /// <summary>
    /// PAX (Portable Archive Exchange) format - Modern TAR variant with Unicode support.
    /// </summary>
    Pax,

    /// <summary>
    /// CPIO format - Unix archive format.
    /// </summary>
    Cpio,

    /// <summary>
    /// ISO 9660 format - CD-ROM filesystem format.
    /// </summary>
    Iso9660,

    /// <summary>
    /// XAR (eXtensible Archive) format - Used by macOS installers.
    /// </summary>
    Xar,
}
