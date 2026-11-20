# libarchive.net

.NET wrapper for the excellent [libarchive](https://www.libarchive.org/) project, providing read and write access to a wide variety of archive and compression formats.

[![NuGet](https://img.shields.io/nuget/v/LibArchive.Net.svg)](https://www.nuget.org/packages/LibArchive.Net/)
[![License](https://img.shields.io/badge/license-BSD--2--Clause-blue.svg)](LICENSE.md)

## Features

- **Read** archives: ZIP, RAR, 7-Zip, TAR, gzip, bzip2, xz, lzma, lzop (decoded by [lzokay](https://github.com/AxioDL/lzokay)), and more
- **Write** archives: ZIP, 7-Zip, TAR, Ustar, PAX, CPIO, ISO 9660, XAR (not on Linux, see below)
- **Compression**: Gzip, Bzip2, XZ, LZMA, LZ4, Zstd, Compress, LZIP
- **Encryption**: ZIP only. Writing supports AES-128, AES-256 and traditional PKWARE; reading also handles AES-192. 7-Zip encryption is not supported by libarchive
- **Platform support**: Windows (x86, x64, ARM64), Linux (x64, ARM64, ARM), macOS (x64, ARM64)
- **Native AOT** and trimming compatible (.NET 10+)
- **.NET Standard 2.0** support for .NET Framework 4.6.1+

XAR is not available on Linux, for writing or for reading. libarchive's XAR support needs an XML library, and the Linux native libraries are built without one (`native/build-linux*.sh` configure libarchive with `--without-xml2 --without-expat`). On Linux, creating a `LibArchiveWriter` for `ArchiveFormat.Xar` throws `ApplicationException` ("Xar not supported on this platform") before anything is written, and `LibArchiveReader` throws `ApplicationException` ("Unrecognized archive format") when given a XAR archive. The Windows and macOS native libraries are built with libxml2 and support XAR.

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
    Console.WriteLine($"{entry.Name} ({entry.LengthBytes} bytes)");

    // Read content
    byte[] content = entry.ReadAllBytes();
    // Or stream it
    using var stream = entry.Stream;
}

// Re-enumerate entries without reopening
reader.Reset();
foreach (var entry in reader.Entries()) { ... }
```

A single compressed file that is not an archive, such as a plain `.gz`, `.bz2`, `.xz`, `.lzma`, `.zst`, `.lz4` or `.lzo` file, is read as an archive holding one entry: the decompressed data. libarchive names that entry `data`, unless the file records its original name, as gzip does by default (`gzip -n` records none). Its `LengthBytes` is null, because the decompressed size is not known in advance. Non-empty data that is neither an archive nor compressed still fails to open, with an `ApplicationException` whose message is `Unrecognized archive format`; empty data opens as an archive with no entries, and so does a compressed file whose decompressed data is empty, such as a gzip of an empty file.

```csharp
// Password-protected ZIP archives
using var encryptedReader = new LibArchiveReader("encrypted.zip", password: "secret");

// Read from streams (e.g., HTTP response, memory)
using var streamReader = new LibArchiveReader(httpResponseStream);
```

### Writing Archives

```csharp
using LibArchive.Net;

// Create a gzip-compressed TAR archive
using var tarWriter = new LibArchiveWriter("archive.tar.gz",
    ArchiveFormat.Tar,
    CompressionType.Gzip);
tarWriter.AddFile("document.txt");
tarWriter.AddDirectory("folder/", recursive: true);
```

```csharp
using System.Text;

// Create encrypted ZIP
using var zipWriter = new LibArchiveWriter("secure.zip",
    ArchiveFormat.Zip,
    password: "secret",
    encryption: EncryptionType.AES256);
zipWriter.AddEntry("secret.txt", Encoding.UTF8.GetBytes("confidential"));
```

```csharp
// Write to memory
using var memWriter = LibArchiveWriter.CreateMemoryWriter(ArchiveFormat.Zip);
memWriter.AddEntry("data.json", jsonBytes);
memWriter.Dispose();
byte[] archiveBytes = memWriter.ToArray();
```

```csharp
// Write to stream with batch operations and progress
using var streamWriter = new LibArchiveWriter(outputStream, ArchiveFormat.SevenZip);
var files = new DirectoryInfo("source").GetFiles();
var progress = new Progress<FileProgress>(p =>
    Console.WriteLine($"{p.FilePath}: {p.BytesProcessed}/{p.TotalBytes}"));
streamWriter.AddFiles(files, progress: progress);
```

## Entry Names

Entry names can hold any Unicode characters, on every platform and whatever the locale of the process.

When writing, each format stores a name as below. libarchive does not normalize names: each is stored in the form it is given, so a name given composed (NFC) is stored composed.

| Format | Name stored as |
|--------|----------------|
| ZIP | UTF-8, with the UTF-8 flag (general purpose bit 11) set on a name that is not ASCII |
| 7-Zip | UTF-16LE |
| TAR, PAX | UTF-8 in a pax `path` record for a name that is not ASCII or does not fit the ustar header's name fields, and in the ustar header as far as it fits |
| Ustar, CPIO | UTF-8 (neither format can declare a character set) |
| ISO 9660 | UTF-16BE in Joliet names, and UTF-8 in Rock Ridge names |
| XAR | UTF-8, base64-encoded if the name has characters beyond Latin-1 |

When reading, `Entry.Name` is:

- the name decoded from the character set the archive declares, normalized to NFC (composed) by libarchive, for pax `path` records, ZIP entries with the UTF-8 flag or an Info-ZIP Unicode path field, and 7-Zip, XAR and Joliet names (Joliet names are read only from ISO 9660 images without Rock Ridge names). A name written decomposed (NFD) in one of these therefore reads back composed;
- otherwise (ustar, GNU tar and cpio headers, ZIP entries without the UTF-8 flag, Rock Ridge names, and so on) the stored bytes read as UTF-8, as tools on macOS and Linux write them, without normalizing them. A byte sequence that is not valid UTF-8, as in a name in a legacy code page (a ZIP from an older Windows, in its OEM code page, for example), becomes U+FFFD, and the entry is still returned.

An entry is always returned, with an empty name if libarchive cannot give one.

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

### LibArchiveReader.Entry

| Property/Method | Description |
|-----------------|-------------|
| `Name` | Entry filename/path (see [Entry Names](#entry-names)) |
| `LengthBytes` | Uncompressed size in bytes, or `null` if it is not known |
| `Type` | Entry type, a `LibArchiveReader.EntryType`: `RegularFile` or `Directory` (other kinds of entry, such as symbolic links, have no named value) |
| `Warning` | libarchive's warning about the entry, such as a pax attribute that it ignored or a name that it could not convert, or `null` if there was none. If libarchive gave the warning without a message, it is `libarchive returned ARCHIVE_WARN without a message` |
| `Stream` | Stream for reading entry content |
| `ReadAllBytes()` | Read entire content as byte array |
| `ReadAllText()` | Read entire content as string |

### LibArchiveWriter

| Method | Description |
|--------|-------------|
| `AddFile()` | Add a file from disk |
| `AddEntry()` | Add content from a byte array |
| `AddDirectory()` | Add directory (optionally recursive) |
| `AddFiles()` | Batch add files with progress reporting |
| `CreateMemoryWriter()` | Create writer that outputs to memory |
| `ToArray()` | Get archive bytes (memory writer only) |

## Requirements

- .NET 10.0+ (recommended)
- .NET Standard 2.0 / .NET Framework 4.6.1+ (supported)
- Native AOT compatible on .NET 10+

## License

BSD-2-Clause. See [LICENSE](LICENSE.md) for details.

## Contributing

Contributions welcome! Please open issues or submit PRs on [GitHub](https://github.com/jas88/libarchive.net).
