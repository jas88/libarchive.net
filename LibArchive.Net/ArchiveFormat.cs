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
    /// Entry names are stored as UTF-8, with the UTF-8 flag (general purpose bit 11) set on each
    /// name that is not ASCII.
    /// </summary>
    Zip,

    /// <summary>
    /// 7-Zip format - excellent compression ratio.
    /// No encryption: libarchive cannot encrypt 7-Zip archives, so a password with this format throws
    /// <see cref="System.NotSupportedException"/> unless the encryption type is <see cref="EncryptionType.None"/>.
    /// Entry names are stored as UTF-16LE.
    /// </summary>
    SevenZip,

    /// <summary>
    /// TAR (Tape Archive) format - Unix standard, no compression or encryption built-in.
    /// Typically combined with compression filters (gzip, bzip2, xz).
    /// Written as restricted pax: a pax extended header is added only for what the ustar header
    /// cannot hold, such as a name that is not ASCII, which it stores as UTF-8 (the ustar header
    /// holds the UTF-8 too, as far as it fits).
    /// </summary>
    Tar,

    /// <summary>
    /// POSIX ustar format - TAR variant with extended metadata support.
    /// Entry names are stored as UTF-8, which ustar has no way to declare, and must fit its
    /// 100-byte name and 155-byte prefix fields.
    /// </summary>
    Ustar,

    /// <summary>
    /// PAX (Portable Archive Exchange) format - Modern TAR variant with Unicode support.
    /// A name that is not ASCII is stored as UTF-8 in a pax extended header, as with <see cref="Tar"/>.
    /// </summary>
    Pax,

    /// <summary>
    /// CPIO format - Unix archive format, written as SVR4 "newc".
    /// Entry names are stored as UTF-8, which cpio has no way to declare.
    /// </summary>
    Cpio,

    /// <summary>
    /// ISO 9660 format - CD-ROM filesystem format.
    /// Written with Joliet names, in UTF-16BE, and Rock Ridge names, in UTF-8.
    /// A parent directory that was not added as an entry is added by libarchive, under the name
    /// it has in the entry's path.
    /// </summary>
    Iso9660,

    /// <summary>
    /// XAR (eXtensible Archive) format - Used by macOS installers.
    /// Not available on Linux.
    /// Entry names are stored as UTF-8 in the table of contents, base64-encoded if they have
    /// characters beyond Latin-1.
    /// </summary>
    /// <remarks>
    /// <para>libarchive's XAR support needs an XML library, and the Linux native libraries are built without one
    /// (libarchive is configured with --without-xml2 --without-expat). On Linux, creating a
    /// <see cref="LibArchiveWriter"/> with this format throws <see cref="System.ApplicationException"/>
    /// ("Xar not supported on this platform") before anything is written, and opening a XAR archive with
    /// <see cref="LibArchiveReader"/> throws <see cref="System.ApplicationException"/>
    /// ("Unrecognized archive format").</para>
    /// <para>A parent directory that was not added as an entry is added by libarchive, under the
    /// name it has in the entry's path.</para>
    /// </remarks>
    Xar,
}
