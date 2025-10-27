v0.3.0 - TBD

**Password-Protected Archive Support:**
- Add password support for encrypted ZIP archives (traditional PKWARE and AES encryption)
- New optional `password` parameter on `LibArchiveReader` constructors
- `HasEncryptedEntries()` method to detect encrypted archives
- Comprehensive test coverage for password functionality
- **Note:** Only ZIP archives are supported - RAR and 7z encrypted archives are not supported by libarchive

**Improved Error Handling:**
- Enhanced `Read()` method to properly throw exceptions on errors (wrong password, unsupported encryption, checksum failures)
- Better error messages for encrypted archive failures

**Code Quality:**
- Simplified `Read()` method implementation (removed unnecessary MemoryMarshal complexity)
- Fixed nullability warnings

v0.2.0 - October 27, 2025

**Platform Support Expansion:**
- Add .NET Standard 2.0 support for .NET Framework 4.6.1+ compatibility on Windows, Linux (Mono), and macOS (Mono)
- Add Linux ARM64 support (AWS Graviton, Raspberry Pi 4+, Azure ARM VMs)
- Add Linux ARM v7 support (Raspberry Pi 2/3, older ARM devices)
- Add Linux musl variants for Alpine Linux (x64, ARM64, ARM v7)
- Add Windows x86 (32-bit) and ARM64 support
- Add macOS ARM64 (Apple Silicon) support
- **Total: 11 platform RIDs** (was 3): win-x86, win-x64, win-arm64, linux-x64, linux-musl-x64, linux-arm, linux-musl-arm, linux-arm64, linux-musl-arm64, osx-x64, osx-arm64

**Native AOT and Trimming:**
- Add Native AOT compatibility for .NET 6+ (`IsAotCompatible=true`)
- Add trimming support (`IsTrimmable=true`)
- Enable build-time AOT and trimming analyzers
- Use `LibraryImport` attribute for .NET 7+ (source-generated, AOT-friendly P/Invoke)

**Build System Improvements:**
- Streamlined native library deployment with automatic MSBuild .targets integration
- Cross-platform build scripts using Bootlin musl toolchains for Linux ARM/ARM64
- Enhanced CI/CD with parallel builds for all architectures
- Static dependency-free libraries (same binary works for glibc and musl)

**Developer Experience:**
- Comprehensive diagnostics via `LIBARCHIVE_NET_DEBUG=1` environment variable
- Enhanced error messages showing all searched library locations
- Automatic native library resolution for .NET 6+ (no custom code needed)
- Manual library loading with platform detection for .NET Standard 2.0/Framework

v0.1.6 - Apr 25 2024

- Bump libarchive from 3.7.2 to 3.7.3
- Bump zstd from 1.5.5 to 1.5.6
- Bump libxml2 from 2.12.4 to 2.12.6

v0.1.5 - Jan 30 2024

- Build Mac component on MacOS 12 not 11
- Bump libarchive from 3.6.2 to 3.7.2
- Bump libzstd from 1.5.2 to 1.5.5
- Fetch liblzo source using HTTPS not HTTP - all dependencies now HTTPS
- Bump libxml2 from 2.10.3 to 2.12.4
- Bump zlib from 1.3 to 1.3.1
- Bump xz from 5.4.0 to 5.4.6, update download location to Github

v0.1.4 - Jun 28 2023

- Add support for multi-volume archives

v0.1.3 - Dec 23 2022

- Add optional argument to LibArchiveReader to configure blocksize, default 1MiB

v0.1.2 - Oct 27 2022

- Fix memory leak on Linux in musl-libc malloc

v0.1.1 - Sep 22 2022

- First full release, supporting Linux, Windows and MacOS x64 plus MacOS ARM64
