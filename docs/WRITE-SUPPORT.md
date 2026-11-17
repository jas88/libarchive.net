# LibArchive.Net Write Support

## Overview

LibArchive.Net now provides comprehensive write support for creating archives in multiple formats with compression and encryption capabilities.

## Features

### ✅ Supported Archive Formats

- **ZIP** - Widely compatible, supports AES encryption
- **7-Zip (.7z)** - Excellent compression (no encryption: libarchive cannot encrypt 7-Zip archives)
- **TAR** - Unix standard, combine with compression filters
- **Ustar** - TAR variant with extended metadata
- **PAX** - Modern TAR with Unicode support
- **CPIO** - Unix archive format
- **ISO 9660** - CD-ROM filesystem
- **XAR** - macOS package format (not on Linux, see below)

XAR is not available on Linux. libarchive's XAR support needs an XML library, and the Linux native
libraries are built without one (`native/build-linux*.sh` configure libarchive with
`--without-xml2 --without-expat`). On Linux, creating a writer for `ArchiveFormat.Xar` throws
`ApplicationException` ("Xar not supported on this platform") before anything is written, and XAR
archives cannot be read either. The Windows and macOS native libraries are built with libxml2 and
support XAR.

### ✅ Compression Algorithms

- **Gzip** - Fast, widely supported (.gz)
- **Bzip2** - Better compression than gzip (.bz2)
- **XZ** - Excellent compression, LZMA2 (.xz)
- **LZMA** - High compression ratio (.lzma)
- **LZ4** - Extremely fast (.lz4)
- **Zstd** - Modern, excellent balance (.zst)
- **Compress** - Legacy Unix (.Z)
- **LZIP** - LZMA with integrity checking (.lz)

The compression type is a filter applied to the whole archive, as in `.tar.gz`. ZIP and 7-Zip compress
their contents themselves, so they need no filter: leave `compression` at `CompressionType.None`.
`CompressionType.Deflate` is not a filter, and the writer throws `NotSupportedException` for it.

### ✅ Encryption Support

- **ZIP**: AES-128, AES-256, Traditional PKWARE (reading also handles AES-192)
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
- `EncryptionType.AES192` throws `NotSupportedException`: libarchive's ZIP writer supports AES-128 and
  AES-256 only.
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

## Performance Tips

### 1. Choose the Right Format

- **ZIP**: Best compatibility, moderate compression
- **7-Zip**: Best compression ratio, slower
- **TAR + Gzip**: Good for Unix systems, streaming-friendly
- **TAR + Zstd**: Modern, excellent speed/ratio balance

### 2. Compression Level Guidelines

The level applies to the compression filter only: with `CompressionType.None` it has no effect, so it does
not change how ZIP or 7-Zip compress their contents.

```csharp
// Fast compression (level 1-3)
compressionLevel: 1  // Fastest, larger files

// Balanced (level 4-6)
compressionLevel: 6  // Default, good balance

// Maximum compression (level 7-9)
compressionLevel: 9  // Slowest, smallest files
```

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
| ZIP | Writing: AES-128, AES-256, Traditional. Reading: AES-128, AES-192, AES-256, Traditional |
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
        _ => CompressionType.None  // ZIP and 7-Zip compress their contents themselves
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
    CompressionType compression = None,       // Compression algorithm
    int compressionLevel = 6,                 // Compression level (0-9)
    uint blockSize = 1048576,                 // Block size (1 MB default)
    string? password = null,                  // Optional password (ZIP only)
    EncryptionType encryption = Default)      // Encryption type
```

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
