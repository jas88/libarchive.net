using System;
using System.Globalization;

namespace LibArchive.Net;

public partial class LibArchiveWriter
{
    #region Compression Configuration

    /// <summary>
    /// Works out how libarchive compresses an archive of the given format, rejecting what it cannot do.
    /// Touches neither native nor file system state, so it can run before the output is created.
    /// </summary>
    /// <remarks>
    /// ZIP, 7-Zip and XAR archives compress each entry with a method of their own, which libarchive's writer
    /// for the format takes as its <c>compression</c> and <c>compression-level</c> options: no filter is added
    /// to them. At level 0 they store the entries uncompressed, whatever the method, as 7-Zip's own
    /// <c>-mx0</c> does. Every other format is compressed as a whole by a filter (see <see cref="AddFilter"/>).
    /// </remarks>
    /// <returns>The options that select a ZIP, 7-Zip or XAR archive's method and level, in the order they are
    /// to be set; or <c>null</c> for the other formats, which <see cref="AddFilter"/> compresses.</returns>
    private static string[]? GetFormatCompressionOptions(ArchiveFormat format, CompressionType compression, int compressionLevel)
    {
        var module = FormatModule(format);
        if (module == null)
        {
            if (compression == CompressionType.Deflate)
                throw new NotSupportedException(
                    $"Deflate compression is not supported for the {format} format: libarchive has no DEFLATE filter. " +
                    "Use Gzip, which compresses the archive with DEFLATE in the gzip format.");
            if (compression != CompressionType.None && !IsFilter(compression))
                throw new NotSupportedException($"Compression type {compression} is not supported");

            // Only a filter uses the level
            if (compression != CompressionType.None)
                CheckCompressionLevel(compressionLevel);
            return null;
        }

        // The type is checked even at level 0, which stores the entries whatever the method
        var method = GetMethod(format, compression);
        CheckCompressionLevel(compressionLevel);

        // At level 0, 7-Zip stores with its Copy method and XAR with "none", which take no level, so none is
        // given (archive_write_set_format_7zip.c:418-422, archive_write_set_format_xar.c:470-473)
        if (compressionLevel == 0 && format != ArchiveFormat.Zip)
            return new[] { $"{module}:compression={(format == ArchiveFormat.SevenZip ? "store" : "none")}" };

        // The method goes first. At level 0 the ZIP writer switches to store, whatever the method set before
        // it (archive_write_set_format_zip.c:410-412), whereas a method set after it would be used at level 0.
        var level = $"{module}:compression-level={compressionLevel.ToString(CultureInfo.InvariantCulture)}";
        return method == null ? new[] { level } : new[] { $"{module}:compression={method}", level };
    }

    /// <summary>
    /// The name of libarchive's writer for a format that compresses its own entries, which prefixes its
    /// options, or <c>null</c> for a format that a filter compresses.
    /// </summary>
    private static string? FormatModule(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.SevenZip => "7zip",
        ArchiveFormat.Xar => "xar",
        _ => null,
    };

    /// <summary>
    /// The method of a ZIP, 7-Zip or XAR archive that a compression type selects, as the writer's
    /// <c>compression</c> option names it (archive_write_set_format_zip.c:342-395,
    /// archive_write_set_format_7zip.c:415-478, archive_write_set_format_xar.c:467-510), or <c>null</c> for
    /// <see cref="CompressionType.None"/>, which leaves the format's default.
    /// </summary>
    /// <exception cref="NotSupportedException">The format has no method for the compression type.</exception>
    private static string? GetMethod(ArchiveFormat format, CompressionType compression) => format switch
    {
        ArchiveFormat.Zip => compression switch
        {
            CompressionType.None => null,
            CompressionType.Deflate => "deflate",
            CompressionType.Bzip2 => "bzip2",
            CompressionType.Lzma => "lzma",
            CompressionType.Xz => "xz",
            CompressionType.Zstd => "zstd",
            _ => throw MethodNotSupported(format, compression,
                "ZIP archives compress their entries themselves, and support None (DEFLATE, the default), " +
                "Deflate, Bzip2, Lzma, Xz and Zstd." +
                (compression == CompressionType.Gzip ? " Deflate is the method gzip uses." : "")),
        },
        ArchiveFormat.SevenZip => compression switch
        {
            CompressionType.None => null,
            CompressionType.Deflate => "deflate",
            CompressionType.Bzip2 => "bzip2",
            CompressionType.Lzma => "lzma1",
            // The method of xz: a 7-Zip archive holds LZMA2 data, not xz streams
            CompressionType.Xz => "lzma2",
            CompressionType.Zstd => "zstd",
            _ => throw MethodNotSupported(format, compression,
                "7-Zip archives compress their entries themselves, and support None (LZMA, the default), " +
                "Deflate, Bzip2, Lzma, Xz (as LZMA2) and Zstd." +
                (compression == CompressionType.Gzip ? " Deflate is the method gzip uses." : "")),
        },
        ArchiveFormat.Xar => compression switch
        {
            CompressionType.None => null,
            CompressionType.Gzip => "gzip",
            CompressionType.Bzip2 => "bzip2",
            CompressionType.Lzma => "lzma",
            CompressionType.Xz => "xz",
            _ => throw MethodNotSupported(format, compression,
                "XAR archives compress their files themselves, and support None (gzip, the default), " +
                "Gzip, Bzip2, Lzma and Xz." +
                (compression == CompressionType.Deflate ? " libarchive calls XAR's DEFLATE method gzip: use Gzip." : "")),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The format has no compression methods"),
    };

    private static NotSupportedException MethodNotSupported(ArchiveFormat format, CompressionType compression, string methods)
        => new($"{compression} compression is not supported for the {format} format: {methods}");

    private static bool IsFilter(CompressionType compression) => compression is
        CompressionType.Gzip or CompressionType.Bzip2 or CompressionType.Xz or CompressionType.Lzma or
        CompressionType.Lz4 or CompressionType.Zstd or CompressionType.Compress or CompressionType.Lzip;

    private static void CheckCompressionLevel(int compressionLevel)
    {
        if (compressionLevel < 0 || compressionLevel > 9)
            throw new ArgumentOutOfRangeException(nameof(compressionLevel), compressionLevel,
                "Compression level must be between 0 and 9");
    }

    /// <summary>
    /// Sets the method and level of a format that compresses its own entries, from
    /// <see cref="GetFormatCompressionOptions"/>, or adds the filter that compresses any other format.
    /// </summary>
    private void ConfigureCompression(string[]? formatOptions, CompressionType compression, int compressionLevel)
    {
        if (formatOptions == null)
        {
            AddFilter(compression, compressionLevel);
            return;
        }

        foreach (var option in formatOptions)
        {
            using var optionBuffer = new SafeStringBuffer(option);
            ThrowUnlessOk(archive_write_set_options(handle, optionBuffer.Ptr));
        }
    }

    /// <summary>
    /// Adds the filter that compresses the whole archive, as in <c>.tar.gz</c>, for a format that does not
    /// compress its own entries. The compression type and level have been checked by
    /// <see cref="GetFormatCompressionOptions"/>.
    /// </summary>
    private void AddFilter(CompressionType compression, int level)
    {
        if (compression == CompressionType.None)
            return;

        var result = compression switch
        {
            CompressionType.Gzip => archive_write_add_filter_gzip(handle),
            CompressionType.Bzip2 => archive_write_add_filter_bzip2(handle),
            CompressionType.Xz => archive_write_add_filter_xz(handle),
            CompressionType.Lzma => archive_write_add_filter_lzma(handle),
            CompressionType.Lz4 => archive_write_add_filter_lz4(handle),
            CompressionType.Zstd => archive_write_add_filter_zstd(handle),
            CompressionType.Compress => archive_write_add_filter_compress(handle),
            CompressionType.Lzip => archive_write_add_filter_lzip(handle),
            _ => throw new NotSupportedException($"Compression type {compression} is not supported")
        };

        ThrowUnlessOk(result);

        // Set compression level via filter options
        // The option name varies by filter: "compression-level" is the common name
        using var optionName = new SafeStringBuffer("compression-level");
        using var optionValue = new SafeStringBuffer(level.ToString());
        // Ignore result - not all filters support compression-level, and that's ok
        archive_write_set_filter_option(handle, IntPtr.Zero, optionName.Ptr, optionValue.Ptr);
    }

    #endregion
}
