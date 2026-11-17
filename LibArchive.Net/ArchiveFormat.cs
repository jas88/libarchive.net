namespace LibArchive.Net;

/// <summary>
/// Specifies the archive format for writing.
/// </summary>
public enum ArchiveFormat
{
    /// <summary>
    /// ZIP format - widely supported, moderate compression.
    /// Supports encryption (AES-128, AES-256, Traditional PKWARE): the only format libarchive can encrypt.
    /// AES-192 cannot be written (see <see cref="EncryptionType.AES192"/>).
    /// </summary>
    Zip,

    /// <summary>
    /// 7-Zip format - excellent compression ratio.
    /// No encryption: libarchive cannot encrypt 7-Zip archives, so a password with this format throws
    /// <see cref="System.NotSupportedException"/> unless the encryption type is <see cref="EncryptionType.None"/>.
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
    /// Not available on Linux.
    /// </summary>
    /// <remarks>
    /// libarchive's XAR support needs an XML library, and the Linux native libraries are built without one
    /// (libarchive is configured with --without-xml2 --without-expat). On Linux, creating a
    /// <see cref="LibArchiveWriter"/> with this format throws <see cref="System.ApplicationException"/>
    /// ("Xar not supported on this platform") before anything is written, and opening a XAR archive with
    /// <see cref="LibArchiveReader"/> throws <see cref="System.ApplicationException"/>
    /// ("Unrecognized archive format").
    /// </remarks>
    Xar,
}
