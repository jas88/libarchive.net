# Package Documentation

## LibArchive.Net

**NuGet Package:** [LibArchive.Net](https://www.nuget.org/packages/LibArchive.Net/)

### Overview

LibArchive.Net is a .NET wrapper for the native libarchive library, providing read and write access to a wide variety of archive and compression formats, including zip, rar (read only), 7zip, tar, gzip, bzip2, xz, lzma, lzop, zstd and lz4.

### Key Features

- **Reading**: zip, rar, 7zip, tar, cpio, ISO 9660 and other archive formats, compressed with gzip, bzip2, xz, lzma, lzop, zstd, lz4 and more, including password-protected ZIP archives (traditional PKWARE and AES)
- **Writing**: ZIP, 7-Zip, TAR (also Ustar and PAX), CPIO, ISO 9660 and XAR archives, compressed with Gzip, Bzip2, XZ, LZMA, LZ4, Zstd, Compress or LZIP, and encrypted ZIP archives (AES-128, AES-192, AES-256 or traditional PKWARE)
- **Cross-Platform**: Native libraries included for:
  - Windows: x86, x64, ARM64 (`win-x86`, `win-x64`, `win-arm64`)
  - Linux: x64, ARM (32-bit), ARM64, on glibc and on musl (`linux-x64`, `linux-arm`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm`, `linux-musl-arm64`)
  - macOS: x64, ARM64 (Apple Silicon) (`osx-x64`, `osx-arm64`)
- **Native AOT and Trimming**: Compatible on .NET 10 and later
- **No Native Dependencies to Install**: The native libraries are in the package

### Target Frameworks

- **.NET Standard 2.0**: .NET Framework 4.6.1 and later, and Mono
- **.NET**: A build for each current .NET version (the list in `LibArchive.Net/Directory.Build.props`, which `scripts/generate-build-props.sh` writes for the SDK that builds the package). These builds are trimming and Native AOT compatible

An app on a .NET version that has no build of its own, such as .NET 8 or .NET 9, gets the .NET Standard 2.0 build, which is not marked as trimming or Native AOT compatible.

### Installation

```bash
dotnet add package LibArchive.Net
```

### Basic Usage

```csharp
using LibArchive.Net;

// Read every entry of an archive
using (var reader = new LibArchiveReader("archive.7z"))
{
    foreach (var entry in reader.Entries())
    {
        Console.WriteLine($"File: {entry.Name}, Size: {entry.LengthBytes}");
        byte[] content = entry.ReadAllBytes();
    }
}

// Write a gzip-compressed TAR archive
using (var writer = new LibArchiveWriter("archive.tar.gz", ArchiveFormat.Tar, CompressionType.Gzip))
{
    writer.AddFile("document.txt");
}
```

See [README.md](README.md) and [docs/WRITE-SUPPORT.md](docs/WRITE-SUPPORT.md) for more.

### Multi-Volume Archive Support

LibArchive.Net supports multi-volume RAR archives. Pass every volume, in order:

```csharp
var volumes = new[] { "archive.part00001.rar", "archive.part00002.rar", "archive.part00003.rar" };
using var reader = new LibArchiveReader(volumes);
```

### Platform-Specific Notes

#### Windows
- ARM64 support requires Windows 10 ARM64 or later
- Native libraries are built with MinGW-w64 (LLVM-MinGW for ARM64)
- ZIP encryption uses the operating system's cryptography (CNG)

#### Linux
- Each runtime has a native library of its own, linked dynamically against that runtime's C library, which it needs at run time:
  - `linux-x64` and `linux-arm64`: glibc 2.28 or later (RHEL 8 and later)
  - `linux-arm`: glibc 2.34 or later; an older glibc refuses to load it
  - `linux-musl-x64`, `linux-musl-arm64` and `linux-musl-arm`: musl 1.2.5 or later (Alpine 3.21 and later)
- The 32-bit ARM libraries are built for ARMv7-A with VFPv3-D16, without NEON
- ZIP encryption uses the Mbed TLS crypto library, which the native libraries contain

#### macOS
- Universal binary supporting both Intel (x64) and Apple Silicon (ARM64)
- ZIP encryption uses the operating system's cryptography (CommonCrypto)

### Native AOT and Trimming

On .NET 10 and later, LibArchive.Net is compatible with Native AOT and assembly trimming:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
</PropertyGroup>
```

The package is annotated with trim analysis attributes to ensure safe trimming behavior.

### Known Limitations

1. **Encryption**: ZIP only. libarchive can neither read nor write encrypted 7-Zip or RAR archives
2. **Multi-Volume Archives**: Only RAR multi-volume archives are tested

### Bundled Native Libraries

Each native library is libarchive with these libraries statically linked into it. Their versions are pinned in [native/build-config.sh](native/build-config.sh), and their licence notices are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which the package also contains.

- **All platforms**: zlib, bzip2, liblzma (XZ Utils), Zstandard, LZ4, libxml2, for the XAR format, and lzokay, which provides lzop support in place of the GPL-licensed liblzo2
- **Linux**: the Mbed TLS crypto library (for ZIP AES encryption and digests; Windows and macOS use the operating system's cryptography instead). The Linux libraries use the system's C library, glibc or musl, which is not in the package

They also contain some code from their build toolchains, which THIRD-PARTY-NOTICES.txt describes as well.

### License

BSD-2-Clause

### Support and Contributing

- **Repository**: https://github.com/jas88/libarchive.net
- **Issues**: https://github.com/jas88/libarchive.net/issues
- **Author**: James A Sutherland

Contributions welcome via pull requests or GitHub Sponsorship.

### Changelog

See [CHANGELOG.md](CHANGELOG.md) for version history and release notes.
