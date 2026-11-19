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

### The libarchive patch

LibArchive.Net passes UTF-8 to libarchive's narrow (`char *`) functions and decodes every narrow
string that libarchive returns as UTF-8, on every platform. libarchive itself takes narrow strings
to be in a "current" character set, which it gets from the process locale, and converts names
between that and the character set of an archive. A .NET process never calls `setlocale`, so that
locale is "C":

- On macOS and Linux libarchive asks `nl_langinfo(CODESET)`, which in the "C" locale is US-ASCII
  on macOS and ASCII in the copy of musl that the Linux libraries contain (in which nothing ever
  calls `setlocale`), and it converts narrow strings to and from wide ones with `mbrtowc` and
  `wcrtomb`. Unpatched, non-ASCII names then fail in several formats: the pax, 7-Zip, ISO 9660
  (Joliet) and XAR writers warn and store them wrongly or not at all, the 7-Zip, XAR, zip (with the
  UTF-8 flag) and Joliet readers return no name for them, and the zip writer leaves out the UTF-8
  flag.
- On Windows libarchive takes a code page from the C runtime's locale, and in the "C" locale makes
  a narrow string one byte per character, which can hold only U+0000-U+00FF. Unpatched, a Rock
  Ridge name in ISO 9660, which is taken from the narrow form, is stored as Latin-1 when every
  character fits, and the ISO 9660 and XAR writers misname a non-ASCII parent directory that they
  add themselves. In any other locale, names in formats that declare no character set are converted
  between the OEM code page and the locale's.

`libarchive-narrow-utf8.patch` makes the current character set, the one that narrow strings are in,
UTF-8 on every platform, whatever the locale and without changing any locale, process-wide or per
thread. The other side of each conversion is unchanged: an archive's names are still in the
character set its format declares, or the one an `hdrcharset` option names. On macOS and Linux
narrow and wide strings are converted with libarchive's own UTF-8 code; on Windows the current and
OEM code pages are UTF-8, so Windows converts them as UTF-8, and names in formats that declare no
character set are not converted, as on every other platform. Names read on macOS are NFC (composed)
as on every other platform instead of NFD, and the zip writer sets the UTF-8 flag on non-ASCII
names. The description at the top of the patch lists each change.

- `patch_libarchive` (in `build-config.sh`) applies it to the freshly unpacked sources in every
  build script: `build-macos.sh`, the three `build-linux*.sh` scripts and `build-windows.sh`. It
  fails the build unless every hunk applies without fuzz, and unless each file the patch changes
  has its `LIBARCHIVE_NET_NARROW_UTF8` marker.
- The macOS native test (`nativetest.c`) and the Linux load test (`write_load_test_source` in
  `build-linux-common.sh`) fail the build unless the linked library behaves that way in the "C"
  locale: a name converts between narrow and wide strings as UTF-8, and a zip entry keeps its UTF-8
  name, with the UTF-8 flag, and reads back unchanged. The Windows DLLs are cross-compiled, so the
  build cannot run them: the .NET tests check them on Windows (the `test-net462` job, which runs
  them on .NET Framework 4.6.2 and on modern .NET).
- `scripts/check-native-updates.sh` proposes a new libarchive release only if the patch still
  applies to it the same way (a dry run, without fuzz), and otherwise reports a failed check. To
  rework the patch for a new release, apply it to the new sources, resolve any rejected hunks
  keeping each change's marker comment, and write it again with `diff -u` for each changed file,
  relative to the unpacked release (`a/libarchive/...` and `b/libarchive/...`), below the
  description.

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

`--linux` builds the Linux x64, ARMv7 and ARM64 libraries. On Linux, `--all` builds Linux and Windows, skipping macOS with a warning, and stops with an error if a MinGW cross-compiler is missing (see the [Windows requirements](#windows-x86-x64-arm64)). On macOS it builds macOS and skips Linux and Windows with a warning. Unless it has stopped, it then creates and tests the package. `--package` packs version `$PACKAGE_VERSION`, or `0.0.0-dev` if it is not set (for example `PACKAGE_VERSION=1.2.3 ./build-all.sh --package`), and runs the tests against that package as CI does, following [Testing a package locally](#testing-a-package-locally): on the test project's .NET target, and on Windows on .NET Framework 4.6.2 as well. A package that passes them is put in the repository root. The version must be a SemVer 2.0 version; build metadata (after a `+`) is not part of a package version, so `1.2.3+abc` is packed as `1.2.3`.

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
- tar, gzip, xz and bzip2, which unpack the downloads (`.tar.gz`, `.tar.xz` and `.tar.bz2` archives)
- file (each script runs it on the library it built)
- unzip, to list a package's contents (see [Testing Changes](#testing-changes))
- For the ARMv7 and ARM64 load tests: QEMU user-mode emulation (`qemu-arm`,
  `qemu-aarch64`; Debian/Ubuntu package `qemu-user`)

**Output:** `libarchive.so`, one per architecture, which needs no other library at
run time (it has no DT_NEEDED entry): the C library (musl) and every dependency are
linked into it, so the same file loads in glibc and musl processes.

**Notes:**
- Uses musl-libc cross-compiler for maximum portability
- All dependencies are statically linked
- Build time: ~20-30 minutes (the musl toolchain is a Bootlin prebuilt release, downloaded rather than built)

**Bootlin toolchains:** each script downloads its toolchain (`TOOLCHAIN_*_URL` in
`build-config.sh`, release `BOOTLIN_RELEASE`) with `download_toolchain`, which verifies
it against the `<release>.sha256` file that Bootlin publishes beside each
`<release>.tar.xz`:
- The tarball is downloaded to a temporary file in the download cache, checked, and
  only then renamed into place, so a download that fails or is cut short is never
  used. An interrupted download can leave a `*.tmp.XXXXXX` file behind; nothing reads
  it, and it can be deleted.
- The checksum file is kept beside the tarball, so a cached tarball, including one
  restored by CI's cache, is checked again on every use without a download. One that
  does not match is deleted and downloaded again.
- This catches a truncated or corrupted download, an error page served in place of the
  tarball, and a damaged cache. It does not protect against a compromise of
  toolchains.bootlin.com, which serves the checksum as well as the tarball.
- Pinning the hashes in `build-config.sh` would close that gap, but Renovate updates
  `BOOTLIN_RELEASE` (`.github/renovate.json`), and its datasource, Bootlin's listing
  page, gives it release names and no hashes. Every toolchain update would then need
  three hashes added by hand, and fail CI without them, so they are not pinned. The
  library sources, whose updates (`scripts/check-native-updates.sh`) compute their
  hashes, are pinned.

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

All the scripts keep two caches between builds, in a directory of their own, `$XDG_CACHE_HOME/libarchive-net/` (`~/.cache/libarchive-net/` when `XDG_CACHE_HOME` is unset): `downloads/` holds the downloaded source and toolchain tarballs, and `config-cache/` the configure results. Set `XDG_CACHE_HOME` to move them. CI caches the same two directories.

## Updating Library Versions

A weekly workflow runs `scripts/check-native-updates.sh`, which looks for new releases and opens a pull request that updates their version and checksum variables; see [scripts/README.md](../scripts/README.md). To update library versions by hand:

1. Edit `build-config.sh`
2. Update the version variables (e.g., `LIBARCHIVE_VERSION="3.8.0"`)
3. Update the matching `*_SHA256` variables to the SHA256 of each new tarball: the builds verify each library tarball against them and fail on a mismatch
4. Update the URLs if the download pattern changed
5. For a new libarchive release, check that `libarchive-narrow-utf8.patch` still applies, and rework it if not (see "The libarchive patch" above): every build fails otherwise
6. Test the build on each platform

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
- A cached toolchain is checked on every use, and downloaded again if it does not match Bootlin's checksum (see "Bootlin toolchains" above). If every attempt reports that the download does not match, the server is serving a tarball that differs from its own published checksum: try again later rather than working around the check

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

### Testing a package locally

CI packs the libraries of all eleven runtimes and runs the tests against that package rather than the project (the package and test-net462 jobs in `.github/workflows/build.yml`). To do the same locally, first put native libraries in `LibArchive.Net/runtimes/<rid>/native/`, laid out as in [Directory Structure](#directory-structure): `build-all.sh` puts there what it builds, and libraries built elsewhere, such as the artifacts of a CI run, can be copied in. Git ignores those folders, so none of them can be committed by accident. The tests load the library of the machine's own runtime, so the package needs at least that one. A pack that lacks the library of any runtime warns (`LIBARCHIVENET001`) and names those runtimes: the package installs on them, but fails there at run time.

Then, from the repository root:

```bash
VERSION=0.0.0-local
# MinVer sets the version from the git tags, overriding PackageVersion; MinVerVersionOverride
# fixes it, so that the tests can ask for that version
dotnet pack LibArchive.Net/LibArchive.Net.csproj -c Release -o artifacts/package "-p:MinVerVersionOverride=$VERSION"
# A NuGet config under which LibArchive.Net comes from artifacts/package only, never from a
# cache or nuget.org. Run it again after every pack: it empties the packages folder it sets up
./scripts/write-package-test-nuget-config.sh artifacts/package artifacts/package-test
# Restore, build and run the tests against the package (UseLocalPackage) instead of the project
dotnet restore Test.LibArchive.Net/Test.LibArchive.Net.csproj --configfile artifacts/package-test/nuget.config -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$VERSION"
# The test project's .NET target: the first netN.N in its TargetFrameworks (see below)
TFM=$(dotnet msbuild Test.LibArchive.Net/Test.LibArchive.Net.csproj -getProperty:TargetFrameworks | tr -d '\r' | tr ';' '\n' | grep -m1 -E '^net[0-9]+\.[0-9]+$')
dotnet test Test.LibArchive.Net/Test.LibArchive.Net.csproj --framework "$TFM" -c Release --no-restore -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$VERSION"
```

`TFM` is the test project's .NET target, the first one in `Test.LibArchive.Net/Directory.Build.props`, which `scripts/generate-build-props.sh` writes for the installed SDK. On Windows, `--framework net462` runs the tests on .NET Framework, as the test-net462 job does. `artifacts/` is ignored by git.

`./build-all.sh --package` (see [Quick Start](#quick-start)) runs these steps itself, with the version from `PACKAGE_VERSION`, the .NET target read from the test project as CI reads it, and net462 on Windows only. It packs into a temporary directory, which also holds the NuGet config and its packages folder and is deleted when the script ends, and copies the package to the repository root only if the tests pass.

### Cleaning Build Artifacts

```bash
# Clean downloaded sources and build artifacts
rm -rf local* {bzip2,libarchive,libxml2,lz4,lzokay,mbedtls,xz,zlib,zstd}-*
# Linux and macOS build directories, their output, and the configure cache. The cache
# directory is chosen as build-config.sh chooses it: XDG_CACHE_HOME if it is an absolute
# path, ~/.cache otherwise
case "${XDG_CACHE_HOME:-}" in /*) cache_home=$XDG_CACHE_HOME ;; *) cache_home=$HOME/.cache ;; esac
rm -rf ~/libarchive-linux-* ~/libarchive-macos ~/libarchive-native "$cache_home/libarchive-net/config-cache"
rm -f *.so *.dylib *.dll test nativetest
```

## Performance Notes

Build times on GitHub Actions runners (approximate):

- **macOS**: 15-20 minutes (M1 runner)
- **Linux**: 25-30 minutes (includes downloading the musl toolchain)
- **Windows**: 15-20 minutes (all 3 architectures)

Optimization strategies:
- Use ccache and sccache (already enabled in workflow)
- Cache the download and configure caches (`~/.cache/libarchive-net/`) between runs (already enabled)
- Pre-built Bootlin musl toolchains (already used)

## License

All scripts in this directory are part of LibArchive.Net and licensed under the BSD-2-Clause license.

The libraries being built have their own licenses:
- libarchive: BSD-2-Clause
- zlib: Zlib
- bzip2: BSD-like
- lz4: BSD-2-Clause
- zstd: BSD-3-Clause (upstream offers BSD OR GPLv2)
- lzokay: MIT
- xz (liblzma): 0BSD (BSD Zero Clause) since XZ Utils 5.6.0; earlier releases were public domain
- libxml2: MIT
- mbedTLS (Linux only): Apache-2.0 (upstream offers Apache-2.0 OR GPL-2.0-or-later)

[THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt), which the package also contains, has their full notices, and those of the code that the build toolchains link into the native libraries.
