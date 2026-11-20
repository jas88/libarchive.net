# LibArchive.Net Write Support

## Overview

LibArchive.Net now provides comprehensive write support for creating archives in multiple formats with compression and encryption capabilities.

## Features

### ✅ Supported Archive Formats

- **ZIP** - Widely compatible, supports AES encryption
- **7-Zip (.7z)** - Excellent compression (no encryption: libarchive cannot encrypt 7-Zip archives)
- **TAR** - Unix standard, combine with compression filters (written as restricted pax)
- **Ustar** - TAR variant with extended metadata
- **PAX** - Modern TAR, with pax extended headers for what ustar cannot hold
- **CPIO** - Unix archive format (SVR4 "newc")
- **ISO 9660** - CD-ROM filesystem, with Joliet and Rock Ridge extensions
- **XAR** - macOS package format

Every format stores names with any Unicode characters: see [Entry Names](#entry-names).

### ✅ Compression

ZIP, 7-Zip and XAR archives compress each entry themselves, with a method of their own. For these
formats, `compression` selects the method and `compressionLevel` its level, and no filter is added, so
the archive is still one that ZIP, 7-Zip or XAR tools can open. `CompressionType.None` keeps the
format's default method. The other formats are compressed as a whole by a filter, as in `.tar.gz`, and
`CompressionType.None` leaves them uncompressed.

| `CompressionType` | ZIP | 7-Zip | XAR | Tar, Ustar, Pax, Cpio, ISO 9660 |
|-------------------|-----|-------|-----|---------------------------------|
| `None` | DEFLATE (method 8), the default | LZMA, the default | gzip, the default | No filter |
| `Deflate` | DEFLATE (method 8) | Deflate | Not supported: use `Gzip` | Not supported: use `Gzip` |
| `Gzip` | Not supported: use `Deflate` | Not supported: use `Deflate` | gzip (`application/x-gzip`) | gzip filter (`.gz`) |
| `Bzip2` | BZIP2 (method 12) | BZip2 | bzip2 (`application/x-bzip2`) | bzip2 filter (`.bz2`) |
| `Lzma` | LZMA (method 14) | LZMA | lzma (`application/x-lzma`) | lzma filter (`.lzma`) |
| `Xz` | XZ (method 95) | LZMA2 | xz (`application/x-xz`) | xz filter (`.xz`) |
| `Zstd` | Zstandard (method 93) | Zstandard | Not supported | zstd filter (`.zst`) |
| `Lz4` | Not supported | Not supported | Not supported | lz4 filter (`.lz4`) |
| `Compress` | Not supported | Not supported | Not supported | compress filter (`.Z`) |
| `Lzip` | Not supported | Not supported | Not supported | lzip filter (`.lz`) |

A type that the format does not support throws `NotSupportedException` when the writer is created,
before the output file is created (or an existing one truncated) or anything is written to the stream.
For ZIP, 7-Zip and XAR, the message lists the types the format supports, and names the format's method
for the one asked for when that is another name for it: `Deflate` for `Gzip` with ZIP or 7-Zip, and
`Gzip` for `Deflate` with XAR. For the other formats, whose only unsupported type is `Deflate`, the
message says that libarchive has no DEFLATE filter, and to use `Gzip`.

- `Deflate` is the method of ZIP and gzip, but libarchive has no DEFLATE filter: use `Gzip` for a
  gzip-compressed tar, cpio or ISO 9660 archive. XAR's DEFLATE method is the one libarchive calls gzip,
  which stores each file as a zlib stream that XAR records as `application/x-gzip`.
- 7-Zip's `Xz` is LZMA2, the method xz uses: a 7-Zip archive holds LZMA2 data, not xz streams. 7-Zip's
  Zstandard method has the method ID that 7-Zip-zstd uses (`04 F7 11 01`).
- Not every ZIP tool can extract BZIP2, LZMA, XZ or Zstandard entries, nor every 7-Zip build Zstandard
  ones: the defaults, DEFLATE for ZIP and LZMA for 7-Zip, are the most widely read.
- libarchive's ZIP reader cannot read a Zstandard entry whose size is a multiple of 128 KiB from a file or
  a seekable stream, as `LibArchiveReader` reads them: it reports "Truncated zstd file body", although the
  entry is valid and other tools, such as 7-Zip, read it. It reads such an entry from a stream that cannot
  seek.

```csharp
// A ZIP archive whose entries are compressed with Zstandard at level 9
using var writer = new LibArchiveWriter(
    "archive.zip",
    ArchiveFormat.Zip,
    compression: CompressionType.Zstd,
    compressionLevel: 9);
```

### ✅ Encryption Support

- **ZIP**: AES-128, AES-192, AES-256, Traditional PKWARE
- **7-Zip**: Not supported (libarchive can neither write nor read encrypted 7-Zip archives)
- **TAR and every other format**: Not supported (use external encryption)

libarchive can only encrypt ZIP archives. A password combined with any other format throws
`NotSupportedException` instead of writing an archive that is not protected, unless the encryption
type is `EncryptionType.None`, which ignores the password and writes an unencrypted archive.

### ✅ Zero-Copy I/O Optimization

`AddFile`, `AddFiles` and `AddDirectory` read each file in one of these ways, depending on its size and on
which build of the package the application uses:

| File Size | .NET Standard 2.0 build | .NET builds |
|-----------|-------------------------|-------------|
| Up to 85,000 bytes | FileStream + ArrayPool | **stackalloc + RandomAccess** ⚡ |
| 85,000 bytes - 2 MiB | FileStream + ArrayPool | **ArrayPool + RandomAccess** ⚡ |
| 2 MiB - 64 MiB | FileStream + ArrayPool | FileStream + ArrayPool |
| 64 MiB and over | Memory-mapped ⚡ | Memory-mapped ⚡ |

The .NET builds are the package's target frameworks other than .NET Standard 2.0 (see .NET Targets
below). Applications on .NET Framework, and on .NET versions that have no build of their own, use the
.NET Standard 2.0 build.

## Basic Usage

### Creating a Simple Archive

```csharp
using System.Text;
using LibArchive.Net;

// Create a ZIP archive
using var writer = new LibArchiveWriter("backup.zip", ArchiveFormat.Zip);
writer.AddFile("document.pdf");
writer.AddEntry("readme.txt", Encoding.UTF8.GetBytes("Hello!"));
```

### Compressed Archives

```csharp
// Create compressed TAR archive
using var writer = new LibArchiveWriter(
    "backup.tar.gz",
    ArchiveFormat.Tar,
    compression: CompressionType.Gzip,
    compressionLevel: 9);  // Maximum compression

writer.AddDirectory(@"C:\MyProject", recursive: true);
```

### Password-Protected Archives

```csharp
// ZIP with AES-256 encryption
using var writer = new LibArchiveWriter(
    "secure.zip",
    ArchiveFormat.Zip,
    password: "MySecurePassword123!",
    encryption: EncryptionType.AES256);

writer.AddFile("sensitive-data.xlsx");
```

Only ZIP archives can be encrypted:

- A password with any other format, 7-Zip included, throws `NotSupportedException` unless the encryption
  type is `EncryptionType.None`.
- `EncryptionType.None` ignores the password and writes an unencrypted archive.
- `NotSupportedException` is also thrown when the native library was built without a cryptography backend.

## Advanced Usage

### Batch File Addition

```csharp
// Add multiple files efficiently
var files = Directory.GetFiles(@"C:\Data", "*.pdf", SearchOption.AllDirectories)
    .Select(f => new FileInfo(f));

using var writer = new LibArchiveWriter("documents.zip", ArchiveFormat.Zip);
writer.AddFiles(files);
```

### Custom Path Mapping

```csharp
using var writer = new LibArchiveWriter("archive.zip", ArchiveFormat.Zip);

var files = Directory.GetFiles(@"C:\Source").Select(f => new FileInfo(f));

// Custom path mapper: organize by date
writer.AddFiles(files,
    pathMapper: f => $"{DateTime.Now:yyyy-MM-dd}/{f.Name}");
```

### Progress Reporting

```csharp
var progress = new Progress<FileProgress>(p =>
{
    Console.WriteLine($"Progress: {p.PercentComplete:F1}% - {p.FilePath}");
    Console.WriteLine($"File {p.FileIndex + 1} of {p.TotalFiles}");
});

using var writer = new LibArchiveWriter("large-backup.zip", ArchiveFormat.Zip);
var files = new DirectoryInfo(@"C:\LargeDataset").EnumerateFiles("*", SearchOption.AllDirectories);

writer.AddFiles(files, progress: progress);
```

### Directory Addition with Filters

```csharp
using var writer = new LibArchiveWriter("source.zip", ArchiveFormat.Zip);

// Add only C# files, excluding bin and obj directories
writer.AddDirectory(
    @"C:\Projects\MyApp",
    recursive: true,
    searchPattern: "*.cs",
    filter: f => !f.FullName.Contains("\\bin\\") && !f.FullName.Contains("\\obj\\"));
```

### Stream-Based Writing

```csharp
// Write to MemoryStream
using var memoryStream = new MemoryStream();
using (var writer = new LibArchiveWriter(memoryStream, ArchiveFormat.Zip))
{
    writer.AddEntry("data.json", Encoding.UTF8.GetBytes("{\"key\":\"value\"}"));
}

byte[] archiveBytes = memoryStream.ToArray();
// Send archiveBytes over network, save to database, etc.
```

```csharp
// Write to FileStream with explicit control
using var fileStream = File.Create("output.7z");
using var writer = new LibArchiveWriter(
    fileStream,
    ArchiveFormat.SevenZip);

writer.AddDirectory(@"C:\Data");
```

### Memory Writer (Convenience API)

```csharp
// Create archive in memory
byte[] archiveBytes;

using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
{
    writer.AddEntry("file1.txt", data1);
    writer.AddEntry("file2.txt", data2);
    writer.Dispose();

    archiveBytes = writer.ToArray();
}

// Use archiveBytes (e.g., HTTP response)
return File(archiveBytes, "application/zip", "download.zip");
```

By default the memory writer, like the other writers, hands the archive to its buffer in 1 MiB blocks. An
overload takes the block size as its first argument:

```csharp
// libarchive collects 64 KiB, rather than 1 MiB, before copying it to the buffer
using var writer = LibArchiveWriter.CreateMemoryWriter(64 * 1024, ArchiveFormat.Tar, CompressionType.Gzip);
```

The block size comes first so that existing calls such as `CreateMemoryWriter(ArchiveFormat.Zip, 0)` keep
their meaning (`CompressionType.None`). To give it after the format, name every argument:
`CreateMemoryWriter(format: ArchiveFormat.Zip, blockSize: 4096)`.

## Performance Tips

### 1. Choose the Right Format

- **ZIP**: Best compatibility, moderate compression
- **ZIP with `Zstd`, `Xz` or `Bzip2`**: Better compression than DEFLATE, read by fewer tools
- **7-Zip**: Best compression ratio, slower
- **TAR + Gzip**: Good for Unix systems, streaming-friendly
- **TAR + Zstd**: Modern, excellent speed/ratio balance

### 2. Compression Level Guidelines

`compressionLevel` goes to the method or the filter that `compression` selects, including a ZIP, 7-Zip or
XAR archive's default method with `CompressionType.None`. It must be 0 to 9 (default 6), or the writer
throws `ArgumentOutOfRangeException` before anything is written; a tar, ustar, pax, cpio or ISO 9660
archive without a filter does not use it, and does not check it.

```csharp
// Fast compression (level 1-3)
compressionLevel: 1  // Fastest, larger files

// Balanced (level 4-6)
compressionLevel: 6  // Default, good balance

// Maximum compression (level 7-9)
compressionLevel: 9  // Slowest, smallest files
```

Level 0 stores the entries of a ZIP, 7-Zip or XAR archive uncompressed, whatever the compression type, as
7-Zip's own `-mx0` does. A compression type that the format does not support still throws.

| Format | Level 0 | Levels 1 to 9 |
|--------|---------|---------------|
| ZIP | Method 0 (store): libarchive's ZIP writer switches to it at level 0, and the writer sets the level after the method | The method's own levels. For LZMA and XZ, level 9 is preset 9 with the extreme flag. For Zstandard, ZIP makes level 1 Zstandard's fastest level (`ZSTD_minCLevel()`, -131072, which hardly compresses) and levels 2 to 9 Zstandard's levels 2, 5, 8, 11, 13, 16, 19 and 22 |
| 7-Zip | The Copy method, whose packed data is each entry's data as it is | The method's own levels; Zstandard gets levels 1 to 9 as they are, of its 22 |
| XAR | The none method, which the table of contents records as `application/octet-stream` | The method's own levels |
| Tar, Ustar, Pax, Cpio, ISO 9660 | Depends on the filter, as below | Depends on the filter, as below |

Each filter takes the level as libarchive's filter does. A filter that has no levels, or does not take
level 0, refuses it, and the writer ignores the refusal, so the filter keeps its default:

| Filter | Level 0 | Levels 1 to 9 |
|--------|---------|---------------|
| `Gzip` | No compression: DEFLATE's stored blocks | DEFLATE's levels |
| `Bzip2` | Level 1 | BZIP2's levels: blocks of 100 to 900 kB |
| `Xz`, `Lzma` | Preset 0, the fastest | Presets 1 to 9 |
| `Lzip` | libarchive's lzip level 0, with a 64 KiB dictionary | libarchive's lzip levels, with dictionaries of 1 to 32 MiB |
| `Zstd` | Zstandard's default level, 3 | Zstandard's levels 1 to 9, of its 22 |
| `Lz4` | Refused, as LZ4 takes levels 1 to 9: LZ4's default, level 1 | LZ4's levels; levels 3 to 9 use LZ4 HC |
| `Compress` | Ignored: compress has no levels | Ignored |

The default level, 6, is also the level that libarchive's ZIP, 7-Zip and XAR writers use by default, so a
writer created without compression arguments writes the archive that libarchive writes by default.

### 3. Batch Operations

`AddFiles` is not faster than calling `AddFile` for each file: both write each file the same way.
`AddFiles` adds progress reporting and path mapping:

```csharp
writer.AddFiles(fileInfos, progress: progress);
```

### 4. Small Files in the .NET Builds

In the .NET builds, files of up to 85,000 bytes use stackalloc for maximum performance:

```csharp
// Optimal for many small files in the .NET builds
writer.AddFiles(smallConfigFiles);  // Each up to 85,000 bytes
```

## Security Considerations

### Encryption Strength

```csharp
// ✅ Recommended: AES-256
encryption: EncryptionType.AES256

// ⚠️ Legacy only: Traditional PKWARE (weak)
encryption: EncryptionType.Traditional  // Only for compatibility
```

### Password Complexity

Use strong passwords for encrypted archives:

```csharp
// ✅ Strong password
password: "MyS3cur3P@ssw0rd!2024"

// ❌ Weak password
password: "password123"
```

### Supported Encryption by Format

| Format | Encryption Support |
|--------|-------------------|
| ZIP | AES-128, AES-192, AES-256, Traditional, for writing and reading |
| 7-Zip | None (not supported by libarchive) |
| TAR and every other format | None (use external encryption) |

## Examples

### Web Application: Download as ZIP

```csharp
[HttpGet]
public IActionResult DownloadFiles()
{
    byte[] archiveBytes;

    using (var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip))
    {
        foreach (var document in GetUserDocuments())
        {
            writer.AddEntry(document.FileName, document.Content);
        }
        writer.Dispose();
        archiveBytes = writer.ToArray();
    }

    return File(archiveBytes, "application/zip", "documents.zip");
}
```

### Automated Backup Script

```csharp
var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var backupFile = $"backup-{timestamp}.7z";

var progress = new Progress<FileProgress>(p =>
{
    if (p.FileIndex % 100 == 0)  // Report every 100 files
        Console.WriteLine($"Backed up {p.FileIndex} files...");
});

using var writer = new LibArchiveWriter(
    backupFile,
    ArchiveFormat.SevenZip);

writer.AddDirectory(
    @"C:\ImportantData",
    recursive: true,
    filter: f => f.LastWriteTime > DateTime.Now.AddDays(-7),  // Last 7 days
    progress: progress);

Console.WriteLine($"Backup completed: {backupFile}");
```

### Multi-Format Export

```csharp
void ExportData(string outputPath, ArchiveFormat format)
{
    var compression = format switch
    {
        ArchiveFormat.Tar => CompressionType.Gzip,
        _ => CompressionType.None  // ZIP and 7-Zip use their default methods, DEFLATE and LZMA
    };

    using var writer = new LibArchiveWriter(outputPath, format, compression);

    // Export data in chosen format
    writer.AddEntry("data.json", GetJsonData());
    writer.AddEntry("report.pdf", GetPdfReport());
}

// Usage
ExportData("export.zip", ArchiveFormat.Zip);       // For Windows users
ExportData("export.tar.gz", ArchiveFormat.Tar);    // For Unix users
ExportData("export.7z", ArchiveFormat.SevenZip);   // For maximum compression
```

## Error Handling

```csharp
try
{
    using var writer = new LibArchiveWriter("archive.zip", ArchiveFormat.Zip);
    writer.AddDirectory(@"C:\Data");
}
catch (DirectoryNotFoundException ex)
{
    Console.WriteLine($"Directory not found: {ex.Message}");
}
catch (FileNotFoundException ex)
{
    Console.WriteLine($"File not found: {ex.Message}");
}
catch (ApplicationException ex)
{
    Console.WriteLine($"Archive error: {ex.Message}");
}
catch (UnauthorizedAccessException ex)
{
    Console.WriteLine($"Access denied: {ex.Message}");
}
```

## API Reference

### LibArchiveWriter Constructor

```csharp
public LibArchiveWriter(
    string filename,                          // Output file path
    ArchiveFormat format,                     // Archive format
    CompressionType compression = None,       // Method (ZIP, 7-Zip, XAR) or filter (other formats)
    int compressionLevel = 6,                 // Level of the method or filter (0-9)
    uint blockSize = 1048576,                 // Size of libarchive's writes (1 MiB default)
    string? password = null,                  // Optional password (ZIP only)
    EncryptionType encryption = Default)      // Encryption type
```

`blockSize` sets the size of the blocks libarchive hands to the file, stream or memory buffer (`archive_write_set_bytes_per_block`); `0` writes data as it is produced. The stream constructor takes the same parameter, and so does an overload of the memory writer, as its first parameter: `CreateMemoryWriter(blockSize, format, ...)`. The memory writer without it uses the 1 MiB default. The last block is never padded (`archive_write_set_bytes_in_last_block(1)`), so the block size changes how an archive is written, not its size or contents: libarchive would otherwise pad tar and cpio archives written to a FIFO or device to a whole block.

### Key Methods

```csharp
// Add single file
void AddFile(string sourcePath, string? archivePath = null)

// Add file from byte array
void AddEntry(string archivePath, byte[] data, DateTime? modificationTime = null)

// Add directory entry
void AddDirectoryEntry(string archivePath)

// Add multiple files
void AddFiles(
    IEnumerable<FileInfo> files,
    Func<FileInfo, string>? pathMapper = null,
    IProgress<FileProgress>? progress = null)

// Add entire directory
void AddDirectory(
    string directoryPath,
    bool recursive = true,
    string searchPattern = "*",
    Func<FileInfo, bool>? filter = null,
    bool preserveDirectoryStructure = true,
    IProgress<FileProgress>? progress = null)
```

## Entry Names

The `archivePath` given to `AddEntry`, `AddFile` and `AddDirectoryEntry` (and the paths that
`AddFiles` and `AddDirectory` make) can hold any Unicode characters, on every platform and
whatever the locale of the process. Each format stores them as follows, the same way on every
platform; libarchive does not normalize them, so each name is stored in the form it is given (a
composed, NFC, name stays composed):

| Format | Name stored as |
|--------|----------------|
| `Zip` | UTF-8, with the UTF-8 flag (general purpose bit 11) set on a name that is not ASCII, in the local and central directory headers |
| `SevenZip` | UTF-16LE |
| `Tar`, `Pax` | UTF-8 in a pax extended header's `path` record for a name that is not ASCII (or too long for ustar), and in the ustar header as far as it fits |
| `Ustar` | UTF-8 in the name and prefix fields (100 and 155 bytes); ustar cannot declare a character set |
| `Cpio` | UTF-8; cpio cannot declare a character set |
| `Iso9660` | UTF-16BE in the Joliet directory, and UTF-8 in the Rock Ridge `NM` entries of the primary one. The primary directory's own names are ISO 9660 level 1 identifiers, which do not keep the name: an 8.3 name of upper-case letters, digits and `_` with a `;1` version, such as `GR____E.TXT;1` for `Größe.txt` |
| `Xar` | UTF-8 in the table of contents, base64-encoded (`enctype="base64"`) when the name has characters beyond Latin-1, as libarchive's XAR writer does |

The `Iso9660` and `Xar` writers hold a tree of directories, and add each parent directory that was
not added as an entry themselves, under the name it has in the entry's path.

`LibArchiveReader` reads each name back as it was written, except for its normalization: where
the format declares the name's character set, libarchive decodes the name and normalizes it to NFC
(composed), so a name written decomposed (NFD) reads back composed. The formats that declare it are
`Zip` (whose names that are not ASCII have the UTF-8 flag), `Tar` and `Pax` (pax `path` records),
`SevenZip` and `Xar`. A name whose format declares no character set reads back as the bytes stored,
decoded as UTF-8, with U+FFFD for any invalid byte sequence: `Ustar`, `Cpio`, and `Iso9660`,
whose Rock Ridge names libarchive reads in preference to its Joliet ones. See "Entry Names" in the
[README](../README.md#entry-names).

## Platform Support

- **Windows**: x86, x64, ARM64
- **Linux**: x64, ARM, ARM64 (glibc and musl)
- **macOS**: x64, ARM64

## .NET Targets

- .NET Standard 2.0, used on .NET Framework 4.6.1 and later, and on .NET versions that have no build of
  their own
- A build for each current .NET version: `scripts/generate-build-props.sh` derives the list from the
  .NET SDK that `global.json` selects, and writes it to `LibArchive.Net/Directory.Build.props`

## License

BSD-2-Clause

## See Also

- [LibArchive.Net README](../README.md)
- [Native Library Building](../native/BUILD.md)
- [Test Examples](../Test.LibArchive.Net/)
