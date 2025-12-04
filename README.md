# libarchive.net

.NET wrapper for the excellent [libarchive](https://www.libarchive.org/) project, providing read and write access to a wide variety of archive and compression formats.

[![NuGet](https://img.shields.io/nuget/v/LibArchive.Net.svg)](https://www.nuget.org/packages/LibArchive.Net/)
[![License](https://img.shields.io/badge/license-BSD--2--Clause-blue.svg)](LICENSE)

## Features

- **Read** archives: ZIP, RAR, 7-Zip, TAR, gzip, bzip2, xz, lzma, lzo, and more
- **Write** archives: ZIP, 7-Zip, TAR, Ustar, PAX, CPIO, ISO 9660, XAR
- **Compression**: Gzip, Bzip2, XZ, LZMA, LZ4, Zstd, Compress, LZIP
- **Encryption**: ZIP (AES-128/192/256, Traditional PKWARE), 7-Zip (AES-256)
- **Platform support**: Windows, Linux, macOS (x64, ARM64, ARM, x86)
- **Native AOT** and trimming compatible (.NET 7+)
- **.NET Standard 2.0** support for .NET Framework 4.6.1+

## Quick Start

### Reading Archives

```csharp
using LibArchive.Net;

// Extract single-file archive (zip, gz, 7z, tar) to memory - one liner!
byte[] data = new LibArchiveReader("file.gz").FirstEntry()!.ReadAllBytes();

// Or as text
string text = new LibArchiveReader("file.tar.gz").FirstEntry()!.ReadAllText();

// Enumerate all entries
using var reader = new LibArchiveReader("archive.zip");
foreach (var entry in reader.Entries())
{
    Console.WriteLine($"{entry.Name} ({entry.Size} bytes)");

    // Read content
    byte[] content = entry.ReadAllBytes();
    // Or stream it
    using var stream = entry.Stream;
}

// Password-protected ZIP archives
using var reader = new LibArchiveReader("encrypted.zip", password: "secret");

// Read from streams (e.g., HTTP response, memory)
using var reader = new LibArchiveReader(httpResponseStream);

// Re-enumerate entries without reopening
reader.Reset();
foreach (var entry in reader.Entries()) { ... }
```

### Writing Archives

```csharp
using LibArchive.Net;

// Create a gzip-compressed TAR archive
using var writer = new LibArchiveWriter("archive.tar.gz",
    ArchiveFormat.Tar,
    CompressionType.Gzip);
writer.AddFile("document.txt");
writer.AddDirectory("folder/", recursive: true);

// Create encrypted ZIP
using var writer = new LibArchiveWriter("secure.zip",
    ArchiveFormat.Zip,
    password: "secret",
    encryption: EncryptionType.AES256);
writer.AddEntry("secret.txt", Encoding.UTF8.GetBytes("confidential"));

// Write to memory
using var writer = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip);
writer.AddEntry("data.json", jsonBytes);
writer.Dispose();
byte[] archiveBytes = writer.ToArray();

// Write to stream
using var writer = new LibArchiveWriter(outputStream, ArchiveFormat.SevenZip);

// Batch operations with progress
var files = new DirectoryInfo("source").GetFiles();
var progress = new Progress<FileProgress>(p =>
    Console.WriteLine($"{p.CurrentFile}: {p.BytesWritten}/{p.TotalBytes}"));
writer.AddFiles(files, progress);
```

## Installation

```bash
dotnet add package LibArchive.Net
```

Native libraries for all supported platforms are included in the NuGet package.

## Supported Platforms

| Platform | Architectures |
|----------|---------------|
| Windows | x86, x64, ARM64 |
| Linux (glibc) | x64, ARM64, ARM |
| Linux (musl/Alpine) | x64, ARM64, ARM |
| macOS | x64, ARM64 (Apple Silicon) |

## API Reference

### LibArchiveReader

| Method | Description |
|--------|-------------|
| `Entries()` | Enumerate all entries in the archive |
| `FirstEntry()` | Get the first entry (ideal for single-file archives) |
| `Reset()` | Reset to beginning for re-enumeration |
| `HasEncryptedEntries()` | Check if archive contains encrypted entries |

### Entry

| Property/Method | Description |
|-----------------|-------------|
| `Name` | Entry filename/path |
| `Size` | Uncompressed size in bytes |
| `Type` | Entry type (File, Directory, Symlink, etc.) |
| `Stream` | Stream for reading entry content |
| `ReadAllBytes()` | Read entire content as byte array |
| `ReadAllText()` | Read entire content as string |

### LibArchiveWriter

| Method | Description |
|--------|-------------|
| `AddFile()` | Add a file from disk |
| `AddEntry()` | Add content from byte array or stream |
| `AddDirectory()` | Add directory (optionally recursive) |
| `AddFiles()` | Batch add files with progress reporting |
| `CreateMemoryWriter()` | Create writer that outputs to memory |
| `ToArray()` | Get archive bytes (memory writer only) |

## Requirements

- .NET 6.0+ (recommended)
- .NET Standard 2.0 / .NET Framework 4.6.1+ (supported)
- Native AOT compatible on .NET 7+

## License

BSD-2-Clause. See [LICENSE](LICENSE) for details.

## Contributing

Contributions welcome! Please open issues or submit PRs on [GitHub](https://github.com/jas88/libarchive.net).
