# LibArchive Native Build System

This directory contains the unified build system for building libarchive native libraries across all supported platforms.

## Overview

The build system consists of:
- **build-config.sh**: Central configuration file with all library versions and URLs
- **build-linux.sh**: Linux x86-64 build script (using musl-libc)
- **build-macos.sh**: macOS universal binary build script (x86_64 + arm64)
- **build-windows.sh**: Windows build script (x86, x64, arm64 via MinGW)
- **build-all.sh**: Unified orchestrator for building all platforms

## Library Versions

All library versions are managed in `build-config.sh`:

| Library | Version |
|---------|---------|
| libarchive | 3.7.3 |
| lz4 | 1.9.4 |
| zstd | 1.5.6 |
| lzo | 2.10 |
| libxml2 | 2.12.6 |
| zlib | 1.3.1 |
| xz (liblzma) | 5.4.6 |
| bzip2 | 1.0.8 |

## Quick Start

### Build for Current Platform

```bash
cd native
./build-all.sh
```

This will automatically detect your platform and build the appropriate libraries.

### Build for Specific Platforms

```bash
# Linux only (must run on Linux)
./build-all.sh --linux

# macOS only (must run on macOS)
./build-all.sh --macos

# Windows only (must run on Linux with MinGW)
./build-all.sh --windows

# All platforms and create NuGet package
./build-all.sh --all --package
```

## Platform-Specific Requirements

### Linux (x86-64)

**Requirements:**
- bash
- curl
- gcc
- make
- unzip

**Output:** `libarchive.so` (fully static, no external dependencies except glibc)

**Notes:**
- Uses musl-libc cross-compiler for maximum portability
- All dependencies are statically linked
- Build time: ~20-30 minutes (includes building musl toolchain)

### macOS (Universal Binary)

**Requirements:**
- macOS (tested on 11.0+)
- Xcode Command Line Tools
- Homebrew
- autoconf, automake, libtool (installed automatically)

**Output:** `libarchive.dylib` (universal binary: x86_64 + arm64)

**Notes:**
- Creates a single "fat" binary containing both architectures
- Only depends on system libraries (libc, libiconv)
- Build time: ~10-15 minutes

### Windows (x86, x64, arm64)

**Requirements:**
- Linux build machine
- MinGW-w64 cross-compiler toolchain
  - Ubuntu/Debian: `sudo apt-get install mingw-w64`
  - Fedora: `sudo dnf install mingw64-gcc mingw32-gcc`

**Output:**
- `archive-x86.dll` (32-bit x86)
- `archive-x64.dll` (64-bit x86_64)
- `archive-arm64.dll` (64-bit ARM64)

**Notes:**
- Cross-compiled from Linux
- All dependencies statically linked
- Build time: ~5-10 minutes per architecture

**Building Individual Architectures:**

```bash
# x64
ARCH=x86_64 ./build-windows.sh

# x86
ARCH=i686 ./build-windows.sh

# arm64
ARCH=aarch64 ./build-windows.sh
```

## Directory Structure

After building, libraries are placed in:

```
LibArchive.Net/runtimes/
├── linux-x64/
│   └── libarchive.so
├── osx-any64/
│   └── libarchive.dylib
├── win-x86/
│   └── archive.dll
├── win-x64/
│   └── archive.dll
└── win-arm64/
    └── archive.dll
```

## Updating Library Versions

To update library versions:

1. Edit `build-config.sh`
2. Update the version variables (e.g., `LIBARCHIVE_VERSION="3.8.0"`)
3. Update the URLs if the download pattern changed
4. Test the build on each platform
5. Update this README with the new versions

## CI/CD Integration

The GitHub Actions workflow (`.github/workflows/build.yml`) uses these scripts:

1. **macOS job**: Runs `build-macos.sh`, uploads `libarchive.dylib`
2. **Linux job**:
   - Downloads macOS artifact
   - Runs `build-linux.sh`
   - Places both libraries in runtimes/
   - Builds and tests .NET package
   - Creates NuGet package

Currently, Windows builds are pre-built and checked into the repository. To enable CI builds:

1. Add a Windows build job using Linux + MinGW
2. Build all three Windows architectures
3. Upload as artifacts
4. Include in final package

## Troubleshooting

### Linux Build Issues

**Problem:** "musl-cross-make build failed"
- Check internet connectivity (downloads many sources)
- Verify disk space (~2GB needed during build)
- Check build log: `cat musl-cross-make-master/musl.log`

### macOS Build Issues

**Problem:** "configure: error: C compiler cannot create executables"
- Install Xcode Command Line Tools: `xcode-select --install`
- Accept Xcode license: `sudo xcodebuild -license accept`

**Problem:** "No such file or directory: autoconf"
- Install build tools: `brew install autoconf automake libtool`

### Windows Build Issues

**Problem:** "x86_64-w64-mingw32-gcc: command not found"
- Install MinGW: `sudo apt-get install mingw-w64` (Ubuntu/Debian)
- Or: `sudo dnf install mingw64-gcc mingw32-gcc` (Fedora)

**Problem:** "Cannot find -lws2_32"
- Ensure MinGW runtime libraries are installed
- Check: `dpkg -L mingw-w64-x86-64-dev` (Ubuntu)

## Development Workflow

### Adding a New Dependency

1. Add version variable to `build-config.sh`
2. Add download URL to `build-config.sh`
3. Update `download_all_libraries()` function
4. Add build steps to each platform script
5. Update linker commands to include the new library
6. Test on all platforms

### Testing Changes

```bash
# Quick test (current platform only)
./build-all.sh

# Full test (requires appropriate OS or VMs)
./build-all.sh --all --package

# Verify NuGet package contents
unzip -l LibArchive.Net.*.nupkg | grep runtimes
```

### Cleaning Build Artifacts

```bash
# Clean downloaded sources and build artifacts
rm -rf local* {bzip2,libarchive,libxml2,lz4,lzo,musl-cross-make,xz,zlib,zstd}-*
rm -rf musl-cross-make-master configcache
rm -f *.so *.dylib *.dll test nativetest
```

## Performance Notes

Build times on GitHub Actions runners (approximate):

- **macOS**: 15-20 minutes (M1 runner)
- **Linux**: 25-30 minutes (includes musl toolchain)
- **Windows** (if added to CI): 15-20 minutes (all 3 architectures)

Optimization strategies:
- Use ccache (already enabled in workflow)
- Cache `configcache` between runs (already enabled)
- Pre-built musl toolchain (possible future optimization)

## License

All scripts in this directory are part of LibArchive.Net and licensed under the BSD-2-Clause license.

The libraries being built have their own licenses:
- libarchive: BSD-2-Clause
- zlib: Zlib
- bzip2: BSD-like
- lz4: BSD-2-Clause
- zstd: BSD + GPLv2
- lzo: GPLv2
- xz (liblzma): Public Domain
- libxml2: MIT
