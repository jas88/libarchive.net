namespace LibArchive.Net;

/// <summary>
/// Specifies the archive format for writing.
/// </summary>
public enum ArchiveFormat
{
    /// <summary>
    /// ZIP format - widely supported, moderate compression.
    /// ZIP compresses each entry itself, with DEFLATE by default: the compression type selects the method
    /// (<see cref="CompressionType.Deflate"/>, <see cref="CompressionType.Bzip2"/>,
    /// <see cref="CompressionType.Lzma"/>, <see cref="CompressionType.Xz"/> or <see cref="CompressionType.Zstd"/>)
    /// and no filter is added; any other type throws <see cref="System.NotSupportedException"/>. Compression
    /// level 0 stores the entries uncompressed, whatever the method.
    /// Supports encryption (AES-128, AES-192, AES-256, Traditional PKWARE) with every method: the only format
    /// libarchive can encrypt.
    /// Entry names are stored as UTF-8, with the UTF-8 flag (general purpose bit 11) set on each
    /// name that is not ASCII.
    /// </summary>
    Zip,

    /// <summary>
    /// 7-Zip format - excellent compression ratio.
    /// 7-Zip compresses the entries itself, with LZMA by default: the compression type selects the method
    /// (<see cref="CompressionType.Lzma"/>, <see cref="CompressionType.Xz"/> for LZMA2,
    /// <see cref="CompressionType.Deflate"/>, <see cref="CompressionType.Bzip2"/> or
    /// <see cref="CompressionType.Zstd"/>) and no filter is added; any other type throws
    /// <see cref="System.NotSupportedException"/>. Compression level 0 stores the entries uncompressed, with
    /// 7-Zip's Copy method, whatever the compression type; levels 1 to 9 go to the method.
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
    /// XAR compresses each file itself, with gzip by default: the compression type selects the method
    /// (<see cref="CompressionType.Gzip"/>, <see cref="CompressionType.Bzip2"/>, <see cref="CompressionType.Lzma"/>
    /// or <see cref="CompressionType.Xz"/>) and no filter is added; any other type, including
    /// <see cref="CompressionType.Deflate"/>, throws <see cref="System.NotSupportedException"/>. Compression
    /// level 0 stores the files uncompressed, with XAR's none method (<c>application/octet-stream</c>),
    /// whatever the compression type; levels 1 to 9 go to the method.
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
