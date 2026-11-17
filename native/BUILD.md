# LibArchive Native Build System

This directory contains the unified build system for building libarchive native libraries across all supported platforms.

## Overview

The build system consists of:
- **build-config.sh**: Central configuration file with all library versions and URLs
- **build-lzo-shim.sh**: Builds and verifies lzop support from lzokay (see "lzop support" below)
- **build-linux.sh**, **build-linux-arm.sh**, **build-linux-arm64.sh**: Linux x64, ARMv7 and ARM64 build scripts (using musl-libc)
- **build-linux-common.sh**: Steps shared by the Linux scripts: the mbedTLS build, the final link, and the checks and load tests of the linked library
- **build-macos.sh**: macOS universal binary build script (x86_64 + arm64)
- **build-windows.sh**: Windows build script (x86, x64, arm64 via MinGW)
- **build-all.sh**: Unified orchestrator for building all platforms

## Library Versions

All library versions are managed in `build-config.sh`, which pins the version,
download URL and SHA256 checksum of each library, so they are not repeated here.
The libraries are:

- libarchive
- lz4
- zstd
- lzokay (lzop support)
- libxml2
- zlib
- xz (liblzma)
- bzip2
- mbedTLS (Linux only)

mbedTLS is built and statically linked by the Linux scripts only, as a minimal
`libmbedcrypto.a` (see `mbedtls-config.h`) that gives libarchive its ZIP AES
encryption and digest support. Windows (CNG) and macOS (CommonCrypto) use the
operating system's crypto instead. It is pinned to the 3.6 LTS line because
libarchive cannot build against mbedTLS 4.

### lzop support

libarchive's lzop filters are written against liblzo2, which is GPL-licensed and is therefore not
linked in. `build-lzo-shim.sh` builds a stand-in `liblzo2.a` instead, from
[lzokay](https://github.com/AxioDL/lzokay) (MIT) and the sources in `lzo-shim/`, which implement the
part of the liblzo2 API that libarchive uses. libarchive's own `--with-lzo2` configure checks find
it, so libarchive itself is not patched.

- lzokay has no releases, so `build-config.sh` pins a commit (`LZOKAY_COMMIT`) and the checksum of
  GitHub's archive of it. `lzo-shim/lzokay-bounds-checks.patch` is applied to it, because its
  decompressor does not check its input fully; re-check the patch whenever the pin moves.
- The shim is C++, so every build needs a C++ compiler (`CXX`) and the `patch` program. It is
  compiled so that it needs no C++ runtime, because the Linux libraries are linked without one.
- Every build script fails if lzop support did not reach the library it linked
  (`verify_lzo_support`). The Linux scripts also fail, naming the symbols, if the library does
  not define everything the shim needs or leaves a C++ runtime symbol unresolved
  (`verify_lzo_shim_resolved`). The link itself and `verify_self_contained` reject any
  strong (non-weak) unresolved symbol as well (see "How the library is linked and
  tested" below).

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

`--linux` builds the Linux x64, ARMv7 and ARM64 libraries. On Linux, `--all` builds Linux and Windows, skipping macOS with a warning, and stops with an error if a MinGW cross-compiler is missing (see the [Windows requirements](#windows-x86-x64-arm64)). On macOS it builds macOS and skips Linux and Windows with a warning. Unless it has stopped, it then creates the package. `--package` packs version `$PACKAGE_VERSION`, or `0.0.0-dev` if it is not set (for example `PACKAGE_VERSION=1.2.3 ./build-all.sh --package`), into the repository root.

## Platform-Specific Requirements

### Linux (x64, ARMv7, ARM64)

**Requirements:**
- An x86-64 Linux build machine (the Bootlin toolchains are x86-64 Linux programs)
- bash
- curl
- gcc
- make
- sccache (the scripts compile through it)
- autoconf, automake, libtool and pkg-config (libxml2 is built with its `autogen.sh`)
- unzip
- For the ARMv7 and ARM64 load tests: QEMU user-mode emulation (`qemu-arm`,
  `qemu-aarch64`; Debian/Ubuntu package `qemu-user`)

**Output:** `libarchive.so`, one per architecture, which needs no other library at
run time (it has no DT_NEEDED entry): the C library (musl) and every dependency are
linked into it, so the same file loads in glibc and musl processes.

**Notes:**
- Uses musl-libc cross-compiler for maximum portability
- All dependencies are statically linked
- Build time: ~20-30 minutes (the musl toolchain is a Bootlin prebuilt release, downloaded rather than built)

**How the library is linked and tested** (all three Linux scripts use the functions
in `build-linux-common.sh`):
- `link_libarchive` links all of `libarchive.a` with the other static libraries,
  the toolchain's own `libgcc.a` (`$CC -print-libgcc-file-name`) and musl's static
  `libc.a`, with `-nostdlib`. No shared library is linked, not even `libgcc_s`.
  libgcc supplies the helper routines that compiled code calls (integer division on
  ARMv7, 128-bit floating point on ARM64). musl's `libc.a` calls those
  helpers and libgcc calls back into `libc.a`, so the archives are linked as one
  `--start-group`/`--end-group`. `-Wl,-z,defs` makes the link fail on any strong
  (non-weak) unresolved symbol: the toolchain links with `-z now` (BIND_NOW), so such
  a symbol left for the loader to find would make the library fail to load in any
  process that does not happen to define it.
- `verify_self_contained` then fails the build if the library has a DT_NEEDED
  entry or a strong undefined dynamic symbol. Weak undefined symbols, such as
  zstd's optional `ZSTD_trace_*` hooks, are allowed: they resolve to null.
- `test_load_musl` builds a small dynamically linked musl program with the target
  toolchain and runs it with the musl dynamic loader from the toolchain's sysroot,
  so that the process contains nothing but musl and the library: natively for x64,
  and under QEMU user-mode emulation for ARMv7 and ARM64. The program
  loads `./libarchive.so` with `RTLD_NOW`, calls a few of its functions (including
  reading an empty archive) and fails the build on any error. If QEMU is missing,
  the ARMv7 and ARM64 tests fail the build under GitHub Actions and are skipped
  with a warning elsewhere.
- The x64 build also loads the library in the same program built with the host's
  `gcc` and C library (glibc on the CI runners).

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
  - Fedora: `sudo dnf install mingw64-gcc mingw32-gcc mingw64-gcc-c++ mingw32-gcc-c++`

**Output:**
- `archive-x86.dll` (32-bit x86)
- `archive-x64.dll` (64-bit x86_64)
- `archive-arm64.dll` (64-bit ARM64)

**Notes:**
- Cross-compiled from Linux
- ARM64 needs `aarch64-w64-mingw32-gcc`, which the MinGW packages above do not provide; CI uses LLVM-MinGW for it
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

After building, `build-all.sh` places the libraries in:

```
LibArchive.Net/runtimes/
├── linux-x64/native/
│   └── libarchive.so
├── linux-musl-x64/native/
│   └── libarchive.so
├── linux-arm/native/
│   └── libarchive.so
├── linux-musl-arm/native/
│   └── libarchive.so
├── linux-arm64/native/
│   └── libarchive.so
├── linux-musl-arm64/native/
│   └── libarchive.so
├── osx-x64/native/
│   └── libarchive.dylib
├── osx-arm64/native/
│   └── libarchive.dylib
├── win-x86/native/
│   └── archive.dll
├── win-x64/native/
│   └── archive.dll
└── win-arm64/native/
    └── archive.dll
```

Run on their own, the Linux and macOS scripts leave their library in `~/libarchive-native/`, and `build-windows.sh` leaves its DLL in the current directory.

## Updating Library Versions

A weekly workflow runs `scripts/check-native-updates.sh`, which looks for new releases and opens a pull request that updates their version and checksum variables; see [scripts/README.md](../scripts/README.md). To update library versions by hand:

1. Edit `build-config.sh`
2. Update the version variables (e.g., `LIBARCHIVE_VERSION="3.8.0"`)
3. Update the matching `*_SHA256` variables to the SHA256 of each new tarball: the builds verify each library tarball against them and fail on a mismatch
4. Update the URLs if the download pattern changed
5. Test the build on each platform

## CI/CD Integration

The GitHub Actions workflow (`.github/workflows/build.yml`) uses these scripts:

1. **macOS job**: Runs `build-macos.sh`, uploads `libarchive.dylib`
2. **native job** (one job per target, on Linux):
   - Runs `build-linux.sh`, `build-linux-arm.sh` and `build-linux-arm64.sh`
   - Runs `build-windows.sh` for x86, x64 and arm64
   - Uploads each library
3. **package job**:
   - Downloads all the native libraries
   - Places them in runtimes/
   - Creates the NuGet package and tests it

The built libraries are cached between runs, keyed on the contents of `native/**`; tag builds always rebuild them from source.

## Troubleshooting

### Linux Build Issues

**Problem:** "Failed to download ... toolchain" or "Toolchain compiler not found"
- Check internet connectivity to toolchains.bootlin.com (the scripts download a prebuilt Bootlin musl toolchain)
- Check that `BOOTLIN_RELEASE` in `build-config.sh` names a published Bootlin release
- Delete the toolchain tarball from the download cache (`~/downloads`) if an earlier download was cut short: a cached tarball is reused without being checked

### macOS Build Issues

**Problem:** "configure: error: C compiler cannot create executables"
- Install Xcode Command Line Tools: `xcode-select --install`
- Accept Xcode license: `sudo xcodebuild -license accept`

**Problem:** "No such file or directory: autoconf"
- Install build tools: `brew install autoconf automake libtool`

### Windows Build Issues

**Problem:** "x86_64-w64-mingw32-gcc: command not found"
- Install MinGW: `sudo apt-get install mingw-w64` (Ubuntu/Debian)
- Or: `sudo dnf install mingw64-gcc mingw32-gcc mingw64-gcc-c++ mingw32-gcc-c++` (Fedora)

**Problem:** "Cannot find -lws2_32"
- Ensure MinGW runtime libraries are installed
- Check: `dpkg -L mingw-w64-x86-64-dev` (Ubuntu)

## Development Workflow

### Adding a New Dependency

1. Add version and `*_SHA256` checksum variables to `build-config.sh`
2. Add download URL to `build-config.sh`
3. Update `download_all_libraries()` function
4. Add build steps to each platform script
5. Update linker commands to include the new library (for Linux, the archive list in `link_libarchive` in `build-linux-common.sh`)
6. Test on all platforms

Also add it to the update checker: see "Adding a library" in [scripts/README.md](../scripts/README.md).

### Testing Changes

```bash
# Quick test (current platform only)
./build-all.sh

# Full test (requires appropriate OS or VMs)
./build-all.sh --all --package

# Verify NuGet package contents (build-all.sh writes it to the repository root)
unzip -l ../LibArchive.Net.*.nupkg | grep runtimes
```

### Cleaning Build Artifacts

```bash
# Clean downloaded sources and build artifacts
rm -rf local* {bzip2,libarchive,libxml2,lz4,lzokay,mbedtls,xz,zlib,zstd}-*
# Linux and macOS build directories, their output, and the configure cache
rm -rf ~/libarchive-linux-* ~/libarchive-macos ~/libarchive-native ~/config-cache
rm -f *.so *.dylib *.dll test nativetest
```

## Performance Notes

Build times on GitHub Actions runners (approximate):

- **macOS**: 15-20 minutes (M1 runner)
- **Linux**: 25-30 minutes (includes downloading the musl toolchain)
- **Windows**: 15-20 minutes (all 3 architectures)

Optimization strategies:
- Use ccache and sccache (already enabled in workflow)
- Cache `~/config-cache` between runs (already enabled)
- Pre-built Bootlin musl toolchains (already used)

## License

All scripts in this directory are part of LibArchive.Net and licensed under the BSD-2-Clause license.

The libraries being built have their own licenses:
- libarchive: BSD-2-Clause
- zlib: Zlib
- bzip2: BSD-like
- lz4: BSD-2-Clause
- zstd: BSD + GPLv2
- lzokay: MIT
- xz (liblzma): Public Domain
- libxml2: MIT
- mbedTLS (Linux only): Apache-2.0 (upstream offers Apache-2.0 OR GPL-2.0-or-later)
