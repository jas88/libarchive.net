# Package Documentation

## LibArchive.Net

**NuGet Package:** [LibArchive.Net](https://www.nuget.org/packages/LibArchive.Net/)

### Overview

LibArchive.Net is a .NET wrapper for the native libarchive compression library, providing read-only access to a wide variety of archive and compression formats including: zip, rar, 7zip, tar, gzip, bzip2, lzo, and lzma.

### Key Features

- **Multi-Format Support**: Read archives in zip, rar, 7zip, tar, gzip, bzip2, lzo, lzma formats
- **Cross-Platform**: Native libraries included for:
  - Windows: x86, x64, ARM64
  - Linux: x64, musl-x64
  - macOS: x64, ARM64 (Apple Silicon)
- **Native AOT Compatible**: Full support for Native AOT compilation (.NET 8+)
- **Trimming Ready**: Optimized for trimmed deployments (.NET 6+)
- **Multi-Targeting**: Supports .NET Standard 2.0, .NET 6, .NET 8, and .NET 9
- **Zero Dependencies**: All native libraries are bundled in the package

### Target Frameworks

- **.NET Standard 2.0**: Broad compatibility with .NET Framework 4.6.1+, Unity, Xamarin
- **.NET 6.0**: LTS support (EOL November 2024)
- **.NET 8.0**: Current LTS with Native AOT support
- **.NET 9.0**: Latest features and performance improvements

### Installation

```bash
dotnet add package LibArchive.Net
```

### Basic Usage

```csharp
using LibArchive.Net;

// Open an archive
using var reader = LibArchiveReader.Open("archive.7z");

// Get current entry information
var entry = reader.CurrentEntry();
Console.WriteLine($"File: {entry.Name}, Size: {entry.Size}");

// Extract current entry
reader.ExtractCurrentEntry("output/path");
```

### Multi-Volume Archive Support

LibArchive.Net supports multi-volume RAR archives. All volume files must be in the same directory:

```csharp
// Open the first volume of a multi-volume RAR archive
using var reader = LibArchiveReader.Open("archive.part00001.rar");
// The library automatically finds and reads subsequent volumes
```

### Platform-Specific Notes

#### Windows
- ARM64 support requires Windows 10 ARM64 or later
- Native libraries are built with MinGW (LLVM for ARM64)

#### Linux
- Native library built with musl-libc for maximum portability
- Compatible with both glibc and musl-based distributions

#### macOS
- Universal binary supporting both Intel (x64) and Apple Silicon (ARM64)
- Minimum supported version: macOS 10.15 (Catalina)

### Native AOT and Trimming

LibArchive.Net is fully compatible with Native AOT and assembly trimming:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
</PropertyGroup>
```

The package is annotated with trim analysis attributes to ensure safe trimming behavior.

### Known Limitations

1. **Read-Only**: Currently only supports reading archives (no write/create support)
2. **Multi-Volume Archives**: Only RAR multi-volume archives are tested; all volumes must be in the same directory

### Build Information

- **Native libarchive version**: 3.7.3
- **Compression libraries included**:
  - lz4 1.9.4
  - zstd 1.5.6
  - lzo 2.10
  - libxml2 2.12.6
  - zlib 1.3.1
  - xz 5.4.6
  - bzip2 1.0.8

### License

BSD-2-Clause

### Support and Contributing

- **Repository**: https://github.com/jas88/libarchive.net
- **Issues**: https://github.com/jas88/libarchive.net/issues
- **Author**: James A Sutherland

Contributions welcome via pull requests or GitHub Sponsorship.

### Changelog

See [CHANGELOG.md](CHANGELOG.md) for version history and release notes.
