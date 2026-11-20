namespace LibArchive.Net;

/// <summary>
/// Specifies how an archive is compressed: with a method of the format's own for ZIP, 7-Zip and XAR archives,
/// or with a filter that compresses the whole archive for the other formats.
/// </summary>
/// <remarks>
/// <para>
/// ZIP, 7-Zip and XAR archives compress each entry themselves. For these formats the compression type selects
/// the method, and no filter is added, so the archive stays one that ZIP, 7-Zip or XAR tools can open.
/// <see cref="None"/> keeps the format's default method: DEFLATE for ZIP, LZMA for 7-Zip and gzip for XAR. A type
/// that the format has no method for throws <see cref="System.NotSupportedException"/> when the writer is
/// created, before anything is written; the message lists the types the format supports. Not every ZIP tool can
/// extract entries compressed with BZIP2, LZMA, XZ or Zstandard, nor every 7-Zip build entries compressed with
/// Zstandard.
/// </para>
/// <para>
/// The tar, ustar, pax, cpio and ISO 9660 formats are compressed as a whole by a filter, as in <c>.tar.gz</c>,
/// and <see cref="None"/> leaves them uncompressed.
/// </para>
/// <para>
/// The compression level, 0 to 9, goes to the method or the filter. Level 0 stores the entries of a ZIP, 7-Zip
/// or XAR archive uncompressed, whatever the compression type, as 7-Zip's own <c>-mx0</c> does: ZIP with method
/// 0 (store), 7-Zip with its Copy method, and XAR with its none method, which the table of contents records as
/// <c>application/octet-stream</c>. A type the format has no method for still throws. Levels 1 to 9 go to the
/// method. For Zstandard, ZIP makes level 1 Zstandard's fastest level (<c>ZSTD_minCLevel()</c>, -131072) and
/// levels 2 to 9 Zstandard's levels 2, 5, 8, 11, 13, 16, 19 and 22, while 7-Zip gives Zstandard levels 1 to 9
/// as they are. A filter takes the level as libarchive's filter does (see libarchive's
/// <c>archive_write_set_options(3)</c>), and keeps its default where it refuses one: <see cref="Compress"/> has
/// no levels, and <see cref="Lz4"/> takes levels 1 to 9, so at level 0 it keeps its default, level 1.
/// </para>
/// </remarks>
public enum CompressionType
{
    /// <summary>
    /// The format's default. ZIP compresses its entries with DEFLATE, 7-Zip with LZMA and XAR with gzip, exactly
    /// as <see cref="Deflate"/>, <see cref="Lzma"/> and <see cref="Gzip"/> do; the compression level still
    /// applies. The other formats are not compressed.
    /// </summary>
    None,

    /// <summary>
    /// Gzip - widely supported, good compression/speed balance.
    /// Tar, ustar, pax, cpio and ISO 9660: a gzip filter (<c>.gz</c>).
    /// XAR: the gzip method, XAR's default, which stores each file as a zlib stream of DEFLATE data, recorded
    /// as <c>application/x-gzip</c>.
    /// ZIP and 7-Zip: not supported, as their DEFLATE method is <see cref="Deflate"/>.
    /// </summary>
    Gzip,

    /// <summary>
    /// Bzip2 - better compression than gzip, slower.
    /// Tar, ustar, pax, cpio and ISO 9660: a bzip2 filter (<c>.bz2</c>).
    /// ZIP: BZIP2, method 12. 7-Zip: BZip2. XAR: bzip2 (<c>application/x-bzip2</c>).
    /// </summary>
    Bzip2,

    /// <summary>
    /// XZ - excellent compression ratio, moderate speed, with the LZMA2 algorithm.
    /// Tar, ustar, pax, cpio and ISO 9660: an xz filter (<c>.xz</c>).
    /// ZIP: XZ, method 95. 7-Zip: LZMA2, the method xz uses, since a 7-Zip archive holds LZMA2 data rather than xz
    /// streams. XAR: xz (<c>application/x-xz</c>).
    /// </summary>
    Xz,

    /// <summary>
    /// LZMA - excellent compression ratio.
    /// Tar, ustar, pax, cpio and ISO 9660: an lzma filter (<c>.lzma</c>).
    /// ZIP: LZMA, method 14. 7-Zip: LZMA (LZMA1), 7-Zip's default. XAR: lzma (<c>application/x-lzma</c>).
    /// </summary>
    Lzma,

    /// <summary>
    /// LZ4 - extremely fast, moderate compression.
    /// Tar, ustar, pax, cpio and ISO 9660: an lz4 filter (<c>.lz4</c>).
    /// ZIP, 7-Zip and XAR: not supported.
    /// </summary>
    Lz4,

    /// <summary>
    /// Zstd (Zstandard) - excellent speed/ratio balance, modern algorithm.
    /// Tar, ustar, pax, cpio and ISO 9660: a zstd filter (<c>.zst</c>).
    /// ZIP: Zstandard, method 93. 7-Zip: Zstandard, with the method ID of 7-Zip-zstd (04 F7 11 01).
    /// XAR: not supported.
    /// </summary>
    Zstd,

    /// <summary>
    /// Compress (LZW) - legacy Unix compression.
    /// Tar, ustar, pax, cpio and ISO 9660: a compress filter (<c>.Z</c>), which has no levels.
    /// ZIP, 7-Zip and XAR: not supported.
    /// </summary>
    Compress,

    /// <summary>
    /// LZIP - LZMA-based with integrity checking.
    /// Tar, ustar, pax, cpio and ISO 9660: an lzip filter (<c>.lz</c>).
    /// ZIP, 7-Zip and XAR: not supported.
    /// </summary>
    Lzip,

    /// <summary>
    /// DEFLATE - the method of ZIP and gzip.
    /// ZIP: DEFLATE, method 8, ZIP's default. 7-Zip: Deflate.
    /// XAR: not supported, as libarchive calls XAR's DEFLATE method gzip: use <see cref="Gzip"/>.
    /// Tar, ustar, pax, cpio and ISO 9660: not supported, as libarchive has no DEFLATE filter: use
    /// <see cref="Gzip"/>, which compresses the archive with DEFLATE in the gzip format.
    /// </summary>
    Deflate,
}
