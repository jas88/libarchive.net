# LibArchive.Net Write Support

## Overview

LibArchive.Net now provides comprehensive write support for creating archives in multiple formats with compression and encryption capabilities.

## Features

### ✅ Supported Archive Formats

- **ZIP** - Widely compatible, supports AES encryption
- **7-Zip (.7z)** - Excellent compression, AES-256 encryption
- **TAR** - Unix standard, combine with compression filters
- **Ustar** - TAR variant with extended metadata
- **PAX** - Modern TAR with Unicode support
- **CPIO** - Unix archive format
- **ISO 9660** - CD-ROM filesystem
- **XAR** - macOS package format

### ✅ Compression Algorithms

- **Gzip** - Fast, widely supported (.gz)
- **Bzip2** - Better compression than gzip (.bz2)
- **XZ** - Excellent compression, LZMA2 (.xz)
- **LZMA** - High compression ratio (.lzma)
- **LZ4** - Extremely fast (.lz4)
- **Zstd** - Modern, excellent balance (.zst)
- **Compress** - Legacy Unix (.Z)
- **LZIP** - LZMA with integrity checking (.lz)

### ✅ Encryption Support

- **ZIP**: AES-128, AES-192, AES-256, Traditional PKWARE
- **7-Zip**: AES-256
- **TAR**: Not supported (use external encryption)

### ✅ Zero-Copy I/O Optimization

Performance is optimized for each .NET version:

| File Size | .NET Standard 2.0 | .NET 6-8 | .NET 9 |
|-----------|------------------|----------|---------|
| < 85 KB | ArrayPool | ArrayPool | **stackalloc** ⚡ |
| 85 KB - 2 MB | ArrayPool | ArrayPool | **Span + RandomAccess** ⚡ |
| 2 MB - 64 MB | FileStream | Memory-mapped | **Chunked RandomAccess** |
| > 64 MB | FileStream | Memory-mapped ⚡ | Memory-mapped ⚡ |

## Basic Usage

### Creating a Simple Archive

```csharp
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

```csharp
// 7-Zip with AES-256 (always AES-256 for 7z)
using var writer = new LibArchiveWriter(
    "backup.7z",
    ArchiveFormat.SevenZip,
    compression: CompressionType.LZMA,
    compressionLevel: 9,
    password: "SecurePass456");

writer.AddDirectory(@"C:\Important", recursive: true);
```

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
    ArchiveFormat.SevenZip,
    compression: CompressionType.LZMA);

writer.AddDirectory(@"C:\Data");
```

### Memory Writer (Convenience API)

```csharp
// Create archive in memory
byte[] archiveBytes;

using (var writer = LibArchiveWriter.CreateMemoryWriter(
    ArchiveFormat.Zip,
    compression: CompressionType.Deflate,
    compressionLevel: 6))
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

```csharp
// Fast compression (level 1-3)
compressionLevel: 1  // Fastest, larger files

// Balanced (level 4-6)
compressionLevel: 6  // Default, good balance

// Maximum compression (level 7-9)
compressionLevel: 9  // Slowest, smallest files
```

### 3. Batch Operations

```csharp
// ✅ Good: Single call with IEnumerable
writer.AddFiles(fileInfos);

// ❌ Avoid: Multiple individual calls
foreach (var file in fileInfos)
    writer.AddFile(file.FullName);
```

### 4. .NET 9 Performance

On .NET 9, files < 85 KB use stackalloc for maximum performance:

```csharp
// Optimal for many small files on .NET 9
writer.AddFiles(smallConfigFiles);  // Each < 85 KB
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
| ZIP | AES-128, AES-192, AES-256, Traditional |
| 7-Zip | AES-256 only |
| TAR | None (use external encryption) |

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
    ArchiveFormat.SevenZip,
    compression: CompressionType.LZMA,
    compressionLevel: 9,
    password: Environment.GetEnvironmentVariable("BACKUP_PASSWORD"));

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
        ArchiveFormat.Zip => CompressionType.Deflate,
        ArchiveFormat.SevenZip => CompressionType.LZMA,
        ArchiveFormat.Tar => CompressionType.Gzip,
        _ => CompressionType.None
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
    string? password = null,                  // Optional password
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

- .NET Standard 2.0
- .NET 6.0
- .NET 8.0
- .NET 9.0

## License

BSD-2-Clause

## See Also

- [LibArchive.Net README](../README.md)
- [Native Library Building](../native/README.md)
- [Test Examples](../Test.LibArchive.Net/)
