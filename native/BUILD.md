# LibArchive Native Build System

This directory contains the unified build system for building libarchive native libraries across all supported platforms.

## Overview

The build system consists of:
- **build-config.sh**: Central configuration file with all library versions and URLs, and the pinned zig release for the Linux builds
- **build-lzo-shim.sh**: Builds and verifies lzop support from lzokay (see "lzop support" below)
- **build-linux.sh** `<rid>`: builds the Linux library of one runtime with zig, linked against that runtime's own C library: glibc for `linux-x64`, `linux-arm64` and `linux-arm`, musl for `linux-musl-x64`, `linux-musl-arm64` and `linux-musl-arm` (see [Linux](#linux-x64-armv7-arm64-glibc-and-musl))
- **build-linux-common.sh**: Steps of the Linux build: its tool directory, the libxml2 and mbedTLS builds, the final link, and the checks and load test of the linked library
- **zig-cc.sh**: The C and C++ compiler of the Linux builds, which runs `zig cc` and `zig c++` for the target
- **verify-exports.sh**: Checks that a Linux library exports libarchive's API alone, with every function that LibArchive.Net imports
- **verify-linux-runtimes.sh**: Checks that each Linux runtime in a `runtimes` folder has a library of its own, built for its architecture and linked against its C library
- **linux-loadtest.c**, **linux-loadtest-files.c**, **linux-loadtest-lfs32.c**, **linux-loadtest-guard.c** (with **linux-loadtest.h**): the Linux load test program
- **build-macos.sh**: macOS universal binary build script (x86_64 + arm64)
- **build-windows.sh**: Windows build script (x86, x64, arm64 via MinGW)
- **build-all.sh**: Unified orchestrator for building all platforms

## Library Versions

All library versions are managed in `build-config.sh`, which pins the version,
download URL and SHA256 checksum of each library, so they are not repeated here.
It also pins zig, which compiles and links the Linux libraries (see
[zig](#zig)). The libraries are:

- libarchive
- lz4
- zstd
- lzokay (lzop support)
- libxml2
- zlib
- xz (liblzma)
- bzip2
- mbedTLS (Linux only)

mbedTLS is built and statically linked by the Linux build only, as a minimal
`libmbedcrypto.a` (see `mbedtls-config.h`) that gives libarchive its ZIP AES
encryption and digest support. Windows (CNG) and macOS (CommonCrypto) use the
operating system's crypto instead. It is pinned to the 3.6 LTS line because
libarchive cannot build against mbedTLS 4.

libxml2 gives libarchive its XAR support, on every platform. libarchive's XAR reader
and writer also need zlib, MD5 and SHA-1. If any of these or the XML library is
missing, libarchive builds stubs in their place, which fail with "Xar not supported on
this platform". On Linux:
- mbedTLS supplies MD5 and SHA-1, as neither glibc nor musl has them, and libarchive is
  configured `--with-xml2 --without-expat` (expat can read XAR but not write it).
- `build_libxml2` (in `build-linux-common.sh`) builds libxml2 with only what libarchive
  uses: its xmlReader and xmlWriter interfaces, and the parser and output code under
  them. It leaves out libxml2's other modules, its programs and its Python bindings.
- libxml2 is compiled with `-fvisibility=hidden`, as mbedTLS is. `libarchive.so`
  therefore does not export libxml2's functions, and another libxml2 in the process can
  neither clash with them nor take their place.
- The builds fail unless libarchive's configure enabled XAR and the linked library has
  it (see [The checks](#the-checks) below).

The macOS native test and the Linux load test each write a XAR archive in memory and
read it back.

### lzop support

libarchive's lzop filters are written against liblzo2, which is GPL-licensed and is therefore not
linked in. `build-lzo-shim.sh` builds a stand-in `liblzo2.a` instead, from
[lzokay](https://github.com/AxioDL/lzokay) (MIT) and the sources in `lzo-shim/`, which implement the
part of the liblzo2 API that libarchive uses. libarchive's own `--with-lzo2` configure checks find
it, so libarchive itself is not patched.

- lzokay has no releases, so `build-config.sh` pins a commit (`LZOKAY_COMMIT`) and the checksum of
  GitHub's archive of it. `lzo-shim/lzokay-bounds-checks.patch` is applied to it, because its
  decompressor does not check its input fully; re-check the patch whenever the pin moves.
- The shim is C++, so every build needs a C++ compiler (`CXX`; on Linux `zig c++`) and the
  `patch` program. It is compiled so that it needs no C++ runtime, because the Linux libraries
  are linked without one.
- Every build script fails if lzop support did not reach the library it linked
  (`verify_lzo_support`). The Linux build also fails, naming the symbols, if the library neither
  defines nor imports from its C library everything the shim needs, or leaves a C++ runtime
  symbol unresolved (`verify_lzo_shim_resolved`). The link's `-z defs` rejects any strong
  (non-weak) unresolved symbol as well (see [How the library is built and
  linked](#how-the-library-is-built-and-linked) below).

### The libarchive patches

Every build patches libarchive's sources with this project's patches, which are in this directory,
one for each change. `LIBARCHIVE_PATCHES` in `build-config.sh` lists them, in the order in which
they are applied, each with its marker, which every file that it changes carries in a comment:

- `libarchive-narrow-utf8.patch` (`LIBARCHIVE_NET_NARROW_UTF8`): every narrow string is UTF-8.
- `libarchive-lz4-init-stream-hc.patch` (`LIBARCHIVE_NET_LZ4_INIT_STREAM_HC`): no call to lz4's
  deprecated `LZ4_resetStreamHC()`.
- `libarchive-zip-aes192.patch` (`LIBARCHIVE_NET_ZIP_AES192`): the zip writer can encrypt with WinZip
  AES-192.
- `libarchive-zip-zstd-end-of-entry.patch` (`LIBARCHIVE_NET_ZIP_ZSTD_END_OF_ENTRY`): the zip reader
  ends a Zstandard entry where its data ends, even if that fills the reader's buffer.
- `libarchive-xar-heap-length.patch` (`LIBARCHIVE_NET_XAR_HEAP_LENGTH`): the XAR writer ends an
  archive where its heap ends, without the first bytes of the compressed table of contents.

The description at the top of each patch lists its changes.

LibArchive.Net passes UTF-8 to libarchive's narrow (`char *`) functions and decodes every narrow
string that libarchive returns as UTF-8, on every platform. libarchive itself takes narrow strings
to be in a "current" character set, which it gets from the process locale, and converts names
between that and the character set of an archive. A .NET process never calls `setlocale`, so that
locale is "C":

- On macOS and Linux libarchive asks `nl_langinfo(CODESET)`, which in the "C" locale is US-ASCII
  on macOS, and on Linux ANSI_X3.4-1968 (ASCII) in glibc and ASCII in musl (the Linux libraries
  use the C library of the process, glibc or musl, and its locale), and it converts narrow
  strings to and from wide ones with `mbrtowc` and `wcrtomb`. Unpatched, non-ASCII names then
  fail in several formats: the pax, 7-Zip, ISO 9660 (Joliet) and XAR writers warn and store them
  wrongly or not at all, the 7-Zip, XAR, zip (with the UTF-8 flag) and Joliet readers return no
  name for them, and the zip writer leaves out the UTF-8 flag.
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
names.

`libarchive-lz4-init-stream-hc.patch` has nothing to do with character sets. libarchive's lz4
writer calls `LZ4_resetStreamHC()`, which lz4 1.11.0 deprecates, so every build warned. The patch
makes, in its place, the two calls that `lz4hc.h` names for it: `LZ4_initStreamHC()` on
`sizeof(LZ4_streamHC_t)` bytes, then `LZ4_resetStreamHC_fast()` with the compression level. lz4's
own `LZ4_resetStreamHC()` is that same initialisation followed by setting the level (`lz4hc.c`), so
the stream is left exactly as before and compresses to the same bytes. Defining lz4's
`LZ4_DISABLE_DEPRECATE_WARNINGS` instead would also hide any other deprecated call, such as one
that a later libarchive release makes.

`libarchive-zip-aes192.patch` adds WinZip AES-192 to libarchive's zip writer, which encrypted with
AES-128 and AES-256 only, although its reader decrypts all three. The writer handles AES-192
wherever it handles the other two, with the sizes that the reader reads it with: strength 2 in the
AES extra field (0x9901), a 12-byte salt, and a 24-byte AES key and a 24-byte HMAC-SHA1 key. The
crypto backends of the builds (CommonCrypto on macOS, CNG on Windows and mbedTLS on Linux) take a
24-byte AES key as they take the others. The patch changes `archive_write_set_format_zip.c`, as
`libarchive-narrow-utf8.patch` does, so its hunks are against that file as that patch leaves it.
LibArchive.Net's `EncryptionType.AES192` sets the option that the patch adds, `encryption=aes192`.
`Test.LibArchive.Net/ZipAesTests.cs` checks the headers and the encryption of what the writer
writes, and reads an archive that 7-Zip encrypted with AES-192.

`libarchive-zip-zstd-end-of-entry.patch` fixes a bug in libarchive's zip reader, which libarchive
3.8.11 and libarchive's master branch both have. The reader decompresses a Zstandard entry
(method 93) into a buffer of `ZSTD_DStreamOutSize()` bytes, 128 KiB. It ended an entry whose sizes
it knows only if the entry's data ended with part of that buffer unused, so an entry of 128 KiB, or
of a multiple of 128 KiB, failed with "Truncated zstd file body". The reader knows the sizes of
every entry that it reads from a file or a seekable stream, and of an entry whose local header
holds them, as Python's zipfile writes them. The patch ends such an entry once
`ZSTD_decompressStream()` reports the end of a frame and none of the entry's compressed bytes are
left; any that are left hold another frame, so an entry of several frames still reads in full. An
entry whose sizes follow its data (`ZIP_LENGTH_AT_END`), read from a non-seekable stream, still
ends with its first frame, as before. `Test.LibArchive.Net/ZipZstdTests.cs` reads such entries, as
Python's zipfile and LibArchive.Net's own writer write them, from a path, a seekable stream and a
non-seekable stream.

`libarchive-xar-heap-length.patch` fixes a bug in libarchive's XAR writer, which libarchive 3.8.11
and libarchive's master branch both have. The writer keeps the heap, the extended attributes and
data of the files, in a temporary file, after room for the checksum of the table of contents, which
starts the heap, and adds the compressed table of contents to that file after the heap. When it
closes the archive, it copied the heap out from the end of that room, but took as many bytes as the
room and the heap together, so every archive ended with the first bytes of its compressed table of
contents, as many as the checksum has: 20 with SHA-1, the default. Readers ignore them, as they read
the heap by the offsets and lengths that the table of contents gives. The patch copies the heap
alone. With `toc-checksum=none` there is no room, and the copy was already exact.
`Test.LibArchive.Net/XarTests.cs` (`TestArchiveEndsWithItsHeap`) checks that an archive that each
kind of writer writes ends where the last item of its heap ends.

- `patch_libarchive` (in `build-config.sh`) applies them to the freshly unpacked sources in every
  build script: `build-macos.sh`, `build-linux.sh` and `build-windows.sh`, each of which unpacks
  the sources afresh on every run. It applies one patch at a time, in order, to the sources as
  the patches before it leave them, so a patch may change a file that an earlier one changed. It
  fails the build, naming the patch, unless every hunk of the patch applies without fuzz, and
  unless every file that the patch changes, as its `+++ b/` lines name them, then has the patch's
  marker. Given sources that an earlier build left patched, in which a file already has the
  marker of a patch that changes it, it fails before changing anything and says so.
- The macOS native test (`nativetest.c`) and the Linux load test (`linux-loadtest.c`; see
  [The load tests](#the-load-tests)) fail unless the linked library's narrow strings are UTF-8 in
  the "C" locale: a name converts between narrow and wide strings as UTF-8, and a zip entry keeps
  its UTF-8 name, with the UTF-8 flag, and reads back unchanged. They also fail unless a zip
  entry of 128 KiB that they compress with Zstandard into memory reads back in full from there,
  as from any seekable source (`libarchive-zip-zstd-end-of-entry.patch`). The Windows DLLs are
  cross-compiled, so the build cannot run them: the .NET tests check them on Windows (the
  `test-net462` job, which runs them on .NET Framework 4.6.2 and on modern .NET).
- `scripts/check-native-updates.sh` proposes a new libarchive release only if the patches still
  apply to it: it applies them to a copy of the new sources with `patch_libarchive`, as the builds
  do, and otherwise reports a failed check that names the patch at fault.
- To rework a patch for a new release, apply the patches before it to the new sources, then apply
  it and resolve any rejected hunks, keeping each change's marker comment. Write it again with
  `diff -u` for each changed file, relative to the sources as the patches before it leave them
  (`a/libarchive/...` and `b/libarchive/...`), below the description. A later patch that changes
  the same file may then need reworking too.
- A new change gets a patch of its own, with a description and a marker that no other patch uses,
  added to `LIBARCHIVE_PATCHES` after the others. Where it changes a file that an earlier patch
  changes, write it against that file as the earlier patch leaves it.

## Quick Start

### Build for Current Platform

```bash
cd native
./build-all.sh
```

This will automatically detect your platform and build the appropriate libraries.

### Build for Specific Platforms

```bash
# Linux only (runs on Linux or macOS)
./build-all.sh --linux

# One Linux runtime
./build-linux.sh linux-musl-arm64

# macOS only (must run on macOS)
./build-all.sh --macos

# Windows only (must run on Linux with MinGW)
./build-all.sh --windows

# All platforms and create NuGet package
./build-all.sh --all --package
```

`--linux` builds the six Linux libraries, x64, ARMv7 and ARM64 for glibc and for musl, one after the other, with `build-linux.sh`. On Linux, `--all` builds Linux and Windows, skipping macOS with a warning, and stops with an error if a MinGW cross-compiler is missing (see the [Windows requirements](#windows-x86-x64-arm64)). On macOS it builds macOS and Linux and skips Windows with a warning. Unless it has stopped, it then creates and tests the package. `--package` packs version `$PACKAGE_VERSION`, or `0.0.0-dev` if it is not set (for example `PACKAGE_VERSION=1.2.3 ./build-all.sh --package`), and runs the tests against that package as CI does, following [Testing a package locally](#testing-a-package-locally): on the test project's .NET target, and on Windows on .NET Framework 4.6.2 as well. A package that passes them is put in the repository root. The version must be a SemVer 2.0 version; build metadata (after a `+`) is not part of a package version, so `1.2.3+abc` is packed as `1.2.3`.

## Platform-Specific Requirements

### Linux (x64, ARMv7, ARM64; glibc and musl)

`build-linux.sh <rid>` builds the library of one Linux runtime with [zig](#zig) (`zig cc` and
`zig c++`), linked dynamically against that runtime's own C library: glibc for the `linux-*`
runtimes and musl for the `linux-musl-*` ones. No C library is linked into any of them whole:
`linux-x64` and `linux-arm64` take a few functions (`stat` and the like) from glibc's static
`libc_nonshared.a`, which glibc links into every program, and the two 32-bit ARM libraries hold
zig's compiler-rt, which also supplies `memcpy` and a few other string functions. Each
library needs its C library at run time and nothing else, and exports libarchive's API
(`archive_*`) alone. Every configure is a cross configure, so the script runs on x86_64 and
aarch64 Linux and on macOS alike.

| Runtime | zig target | C library | Notes |
|---|---|---|---|
| `linux-x64` | `x86_64-linux-gnu.2.28` | glibc 2.28 or later | |
| `linux-arm64` | `aarch64-linux-gnu.2.28` | glibc 2.28 or later | |
| `linux-arm` | `arm-linux-gnueabihf.2.34` | glibc 2.34 or later | ARMv7-A; 64-bit `time_t` and `off_t` |
| `linux-musl-x64` | `x86_64-linux-musl` | musl 1.2.5 or later | |
| `linux-musl-arm64` | `aarch64-linux-musl` | musl 1.2.5 or later | |
| `linux-musl-arm` | `arm-linux-musleabihf` | musl 1.2.5 or later | ARMv7-A |

- A glibc target ends in the oldest glibc version whose symbols the code may use. 2.28 is the glibc
  of RHEL 8, the oldest of any distribution that .NET supports. 2.34 is the first glibc with a
  64-bit `time_t` on 32-bit ARM. LibArchive.Net passes a 64-bit `time_t` to
  `archive_entry_set_mtime`, so `linux-arm` is compiled with `-D_TIME_BITS=64
  -D_FILE_OFFSET_BITS=64`. A glibc older than a library's baseline makes the dynamic loader
  refuse the library, with "version `GLIBC_2.xx' not found".
- The musl libraries are built against zig's musl, 1.2.5, the musl of Alpine 3.21, the oldest
  Alpine that .NET supports. musl's `time_t` and `off_t` are 64-bit on every architecture.
- The ARM targets are ARMv7-A with VFPv3-D16 and without NEON (`ARM_CPU` in `build-linux.sh`),
  the baseline of Debian's armhf and of Alpine's armv7. zig's default for `arm` has NEON and 32
  double registers.

**Requirements:**
- An x86_64 or aarch64 Linux machine, or a Mac (Intel or Apple silicon): the machines that zig is
  pinned for (see [zig](#zig))
- bash, curl, make, patch, sed, awk and od
- tar, gzip, xz and bzip2, which unpack the downloads (`.tar.gz`, `.tar.xz` and `.tar.bz2` archives)
- `shasum` or `sha256sum`, which check the downloads
- pkg-config (libarchive's configure finds libxml2 with it)
- file (the script runs it on the library it built)
- GNU binutils for the target: `ar`, `ranlib`, `nm`, `readelf`, `objdump` and `strip`. They come
  from the directory that `BINUTILS_DIR` names, under their unprefixed names, if it is set;
  otherwise from `PATH` on Linux (Debian/Ubuntu: `apt-get install binutils`) and from Homebrew's
  binutils on macOS (`brew install binutils`, found in `/opt/homebrew/opt/binutils/bin` or
  `/usr/local/opt/binutils/bin`). zig has no `nm`, `readelf` or `objdump` of its own. `readelf`
  reads the ELF files of every target, but the other tools read and write only those of the
  targets that their binutils were built for: Homebrew's are built for every target, and a Linux
  distribution's own binutils for its own architecture. A build on Linux for another architecture
  takes that architecture's binutils from `BINUTILS_DIR`. CI, whose Ubuntu runners have x86
  binutils, installs Ubuntu's `binutils-aarch64-linux-gnu` for `linux-arm64` and
  `linux-musl-arm64`, and `binutils-arm-linux-gnueabihf` for `linux-arm` and `linux-musl-arm`,
  and sets `BINUTILS_DIR` to `/usr/aarch64-linux-gnu/bin` or `/usr/arm-linux-gnueabihf/bin`,
  where those packages put the unprefixed tools.
- For the glibc load tests (see [The load tests](#the-load-tests)): an x86_64 Linux machine, and
  for `linux-arm64` and `linux-arm` QEMU user-mode emulation (`qemu-aarch64`, `qemu-arm`;
  Debian/Ubuntu package `qemu-user`) and Ubuntu's cross runtime of glibc for the architecture
  (`libc6-arm64-cross`, `libc6-armhf-cross`)
- unzip, to list a package's contents (see [Testing Changes](#testing-changes))

No other compiler is needed: zig compiles the C and the C++ code. No autotools are needed either,
as the release tarballs' own configure scripts are used, and the Linux builds use no sccache,
which cannot sit in front of zig.

**Output:** `~/libarchive-native/libarchive-<rid>.so`, and `~/libarchive-native/loadtest-<rid>`,
the load test program built for it. A build that succeeds removes its build directory,
`~/libarchive-<rid>`.

#### zig

`build-config.sh` pins zig: `ZIG_VERSION`, and the SHA-256 of the release tarball for each of the
four build machines (`ZIG_SHA256_X86_64_LINUX`, `ZIG_SHA256_AARCH64_LINUX`,
`ZIG_SHA256_X86_64_MACOS` and `ZIG_SHA256_AARCH64_MACOS`).

- `download_zig` (in `build-config.sh`) fetches the tarball for the build machine,
  `https://ziglang.org/download/<version>/zig-<machine>-<version>.tar.xz`, into the download
  cache. It downloads to a temporary file beside the cached one, which is renamed into place only
  once it matches the pinned SHA-256 (`download_verified`), so a download that fails, is cut short
  or brings wrong content, such as an error page, is never used. An interrupted download can
  leave a `*.tmp.XXXXXX` file behind; nothing reads it, and it can be deleted.
- A cached tarball, including one restored by CI's download cache, is checked against the pinned
  SHA-256 on every use. One that does not match is deleted and downloaded again.
- `build-linux.sh` unpacks the tarball into its build directory and fails unless `zig version`
  prints `ZIG_VERSION`.
- zig keeps what it builds for each target (the C library stubs, glibc's `libc_nonshared.a` and
  its compiler-rt) in its own cache, `~/.cache/zig`, which CI caches for each target.

To update zig: `scripts/check-native-updates.sh`, which the weekly workflow runs, reports a newer
release with the five lines that `build-config.sh` needs, `ZIG_VERSION` and the four SHA-256s, as
zig's index of its releases (https://ziglang.org/download/index.json) gives them. It never puts
zig in its pull request (see "Limitations" in [scripts/README.md](../scripts/README.md)), so copy
the lines into `build-config.sh` by hand. Then build all six libraries and check that they build
without warnings and pass every check and load test. A zig release brings its own clang, linker,
compiler-rt, glibc stubs and musl, so repeat the checks that
[THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) describes for the Linux libraries as well.

#### The compiler and the tool directory

`setup_linux_tools` (in `build-linux-common.sh`) makes the build's tool directory,
`~/libarchive-<rid>/bin`, which `build-linux.sh` puts first on `PATH`. `<host>` below is the
triplet that configure is given as `--host`, such as `arm-linux-gnueabihf`.

- `<host>-gcc` and `<host>-g++` are links to `zig-cc.sh`, and are `CC` and `CXX`. `zig-cc.sh` runs
  `zig c++` when it is called by a name that ends in `++`, and `zig cc` otherwise, for the target
  `ZIG_TARGET`, with the target's own flags (`ZIG_TARGET_FLAGS`) and `-fstack-protector-strong`,
  which zig leaves off, and with `-g0` after all the other arguments, as zig compiles with
  debugging information unless told not to.
  - It drops `-static-libgcc` and `-static-libstdc++`, which mean something to GCC only: zig links
    its own compiler-rt.
  - A query that only prints something (any `-print-*` option, `-dumpmachine`, `-dumpversion`,
    `--version`, or `-v` on its own) gets the target without its glibc version, which zig rejects
    there ("version '.2.28' in target triple ... is invalid"). libtool's configure would otherwise
    print that error for `$CC -print-search-dirs`.
- `<host>-ar`, `<host>-ranlib`, `<host>-nm`, `<host>-readelf`, `<host>-objdump` and
  `<host>-strip` are links to GNU binutils (see
  [Requirements](#linux-x64-armv7-arm64-glibc-and-musl)), so that configure finds every tool that
  it looks for under the host triplet. The checks below run binutils through these links alone, so
  that every tool comes from the same binutils. `verify_xar_support` reads the library's text with
  `tr` rather than `strings`, which Ubuntu's cross binutils have only under its prefixed name, as
  `/usr/bin/<host>-strings`.

`CFLAGS` and `CXXFLAGS` are `-fPIC -O2`, with the build's `$PREFIX/include`. pkg-config is confined
to the `.pc` files under `$PREFIX` (`export_cross_build_tools` in `build-config.sh`). The
autoconf configure scripts, of xz, libxml2 and libarchive, are given `--host` and `--build`, the
build machine's triplet from libarchive's `config.guess`, which runs without `CC` set, as it would
otherwise compile a test program for the target; zlib's own configure is given `CHOST`. The three
autoconf configure scripts share one configure cache for the runtime, `config.cache.<rid>`.

#### How the library is built and linked

- lz4, zstd and bzip2: their static libraries only, with their own Makefiles. bzip2 is compiled
  with `-D_FILE_OFFSET_BITS=64`.
- The lzop shim (`build_lzo_shim`; see [lzop support](#lzop-support)).
- zlib, with its own configure, whose `CHOST` names the target. That configure turns on zlib's
  large file mode (`-D_LARGEFILE64_SOURCE=1`) wherever `off64_t` exists. In that mode `gzguts.h`
  undefines `_FILE_OFFSET_BITS` and `_TIME_BITS`, so that on 32-bit ARM the `gz*` functions, which
  libxml2 uses, would call glibc's `open` and `fcntl` with a 32-bit `off_t` and `time_t`, which
  `verify_glibc_imports` refuses. Every target's `off_t` is 64-bit already, so `build-linux.sh`
  takes the flag out of zlib's Makefile, and fails if any of it is left.
- xz, then libxml2 (`build_libxml2`) and mbedTLS (`build_mbedtls`), as [Library
  Versions](#library-versions) describes.
- libarchive, configured with `--with-mbedtls --without-openssl --with-xml2 --without-expat
  --with-lzo2 --enable-posix-regex-lib=libc` and without its programs.

`link_libarchive` (in `build-linux-common.sh`) then links `libarchive.so` with `$CC -shared`: all
of `libarchive.a` (`--whole-archive`), and the other static libraries in one
`--start-group`/`--end-group`, so that their order does not matter (`libxml2.a`, for one, uses
`libz.a`). zig links it dynamically against the target's C library, and adds its compiler-rt and,
for glibc, glibc's `libc_nonshared.a`, from which the linker takes only what the code needs: on
32-bit ARM compiler-rt's helper routines, such as the `__aeabi_*` division routines, and for
glibc 2.28 `stat` and the like, which glibc had in `libc_nonshared.a` before 2.33.
[THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) says what of them each library contains.
zig builds both with debugging information, which `-g0` does not remove, so the libraries that
contain them hold that too until it is stripped (see below). The link has:
- `-Wl,-z,defs`, which fails the link on any strong (non-weak) unresolved symbol, such as a
  function that the C library lacks in the oldest version that the target names. Weak references,
  such as zstd's optional `ZSTD_trace_*` hooks, are still allowed: they resolve to null when
  nothing defines them.
- `-Wl,-z,relro` and `-Wl,-z,now`, full RELRO: the dynamic loader binds every symbol when it loads
  the library, and then makes the data that it relocated read-only.
- `-Wl,--version-script=linux-exports.map`, which exports `archive_*`, libarchive's API, as any
  `libarchive.so` does, and nothing else. The code of zlib, xz, zstd, lz4, bzip2, libxml2, mbedTLS,
  the lzop shim and compiler-rt stays local to the library, so that no other code in the process
  can bind to it, or take its place by defining a symbol of the same name.
- On 32-bit ARM, `linux-arm-stack-guard.c`, compiled for the target and linked in ahead of the
  archives. There the stack protector checks each protected frame against the global
  `__stack_chk_guard`, and calls `__stack_chk_fail` when a frame fails the check. zig's
  compiler-rt defines both, weakly, and the guard as a constant, and in a link they take the
  place of the C library's; glibc and musl seed their guard from the kernel's random bytes.
  `linux-arm-stack-guard.c` defines a strong, hidden `__stack_chk_guard` instead, which a
  first-priority constructor seeds from the process's `AT_RANDOM` bytes, with its lowest byte
  cleared, as glibc clears it. compiler-rt's `__stack_chk_fail` stops the process at an undefined
  instruction (SIGILL), with no message, as musl's own does, so `linux-musl-arm` keeps it. glibc's
  writes `*** stack smashing detected ***: terminated` to stderr and aborts (SIGABRT), and for
  `linux-arm`, `linux-arm-stack-guard.c` defines a strong, hidden `__stack_chk_fail` that does the
  same, with glibc's `syscall` and `abort` alone, so that it needs neither the heap nor the frames
  that failed the check. It makes the `write` system call itself, as glibc's handler does: `write`
  is a cancellation point, where a thread with a cancellation request pending would unwind those
  frames.

Right after the link, before every check, `strip_debug_info` (in `build-linux-common.sh`) strips
that debugging information from the library with GNU `strip --strip-debug --keep-file-symbols`,
the `strip` of the tool directory. It is about 776 KB in each 32-bit ARM library, which takes
compiler-rt whole, and names the directory that zig built compiler-rt in, and about 10 KB in
`linux-x64` and `linux-arm64`, from `libc_nonshared.a`; `linux-musl-x64` and `linux-musl-arm64`
hold none. `--strip-debug` removes the `.debug_*` sections and keeps the symbol table (`.symtab`),
which the checks and the stack protector checks of the 32-bit ARM load test read, and
`--keep-file-symbols` keeps in it the symbols that name the objects linked in (`STT_FILE`), which
`--strip-debug` would remove too, and which `verify_stack_chk_fail` reads. zig's own
`-Wl,--strip-debug` is not used, as zig makes it a full strip, which removes `.symtab`.

#### The checks

`build-linux.sh` runs these on the library that it links, in this order, and a check that fails
fails the build:
- `verify_xar_configured`, before libarchive is compiled: its configure must have found libxml2
  (with pkg-config, in the `.pc` files under `$PREFIX`) with the headers of its reader and writer,
  zlib, and mbedTLS's MD5 and SHA-1, which its `config.h` records. configure does not fail without
  them, even given `--with-xml2`: libarchive then builds its XAR stubs.
- `verify_no_debug_info`, after `strip_debug_info`: no section of the library (`readelf -S`) is a
  `.debug_*` one.
- `verify_crypto_backend`: the library defines `mbedtls_pkcs5_pbkdf2_hmac`, so ZIP AES encryption
  works, and leaves no `mbedtls_` or `psa_` symbol undefined.
- `verify_xar_support`: the library defines libxml2's `xmlReaderForIO` and
  `xmlNewTextWriterMemory`, leaves no libxml2 symbol undefined, weak or strong, and does not
  contain the stubs' message.
- For a glibc library, `verify_glibc_imports <library> <baseline>`, with the version that ends the
  zig target:
  - every symbol version that the library needs is `GLIBC_x.y`, no later than the baseline
    (`GLIBC_PRIVATE`, glibc's internal interface, fails);
  - every symbol that it imports has a version, apart from weak references;
  - it needs (DT_NEEDED) no library but glibc's own: `libc.so.6`, the libraries split from it
    before glibc 2.34 (`libpthread.so.0`, `libm.so.6`, `libdl.so.2` and `librt.so.1`), and its
    dynamic loader, `ld-linux-*.so.*`, which defines a few of glibc's symbols itself, such as
    `__stack_chk_guard` on arm64;
  - on 32-bit ARM, it imports none of glibc's functions with a 32-bit `time_t` or `off_t`, which
    `glibc-32bit-names.txt` lists, each beside the function with a 64-bit one that is called in its
    place. The check fails unless the list holds the 157 names that it was made with, so that a
    list that lost its names cannot pass every import. The head of that file says how it was made;
    make it again the same way when glibc adds such functions, and change that number in
    `verify_glibc_imports`.
- For a musl library, `verify_musl_imports <library>`: it needs musl's `libc.so` alone
  (DT_NEEDED), and no symbol versions, which musl does not have.
- `verify-exports.sh <library>` (a script of its own): every symbol that the library defines for
  other code to bind to is an `archive_*` symbol, and it exports every function that LibArchive.Net
  imports from "archive": the entry points of the `[LibraryImport]` and `[DllImport]` attributes in
  `LibArchive.Net/*.cs`, apart from `archive_read_open_filenames_w`, which libarchive declares and
  exports on Windows only. `READELF` names the readelf that it uses. The package job of the
  workflow runs it on the six libraries that it packs as well.
- For `linux-arm` and `linux-musl-arm`, `verify_stack_guard`: the library's `__stack_chk_guard` is
  the uninitialised one of `linux-arm-stack-guard.c` (`nm` type `b` or `B`), not compiler-rt's
  initialised constant, and the constructor that seeds it, `libarchive_net_seed_stack_guard`, is
  in `.init_array`, where the dynamic loader finds what to run when it loads the library.
- For `linux-arm` and `linux-musl-arm`, `verify_stack_chk_fail <library> <glibc|musl>`: the
  library's `__stack_chk_fail` is hidden, and defined by `linux-arm-stack-guard.c` for glibc and
  by compiler-rt (`compiler_rt`) for musl. The symbol table names the object that defines each
  local symbol, hidden ones included, in the `STT_FILE` symbol before it, which `strip_debug_info`
  keeps.
- For the other four libraries, `verify_libc_stack_guard <library>`: neither `.symtab` nor
  `.dynsym` defines `__stack_chk_guard` or `__stack_chk_fail`, which the C library gives them
  (`linux-x64` and `linux-musl-x64` keep the guard in thread-local storage, and `linux-arm64` and
  `linux-musl-arm64` import it), and no symbol names zig's compiler-rt (`compiler_rt` or
  `compiler_rt.*`). A change of zig or of the flags that pulled compiler-rt into one of them, for
  outline atomics or 128-bit floats, say, would bring its constant guard, which would take the
  place of the C library's, as it did on 32-bit ARM. Both tables must define
  `archive_version_string`, and `.symtab` must have `STT_FILE` symbols, so that a missing or
  stripped table cannot pass.
- The load test (see [The load tests](#the-load-tests)).
- `verify_lzo_support` and `verify_lzo_shim_resolved` (in `build-lzo-shim.sh`; see [lzop
  support](#lzop-support)).

The package job of the workflow (see [CI/CD Integration](#cicd-integration)) also runs
`verify-linux-runtimes.sh <runtimes folder>` on the runtimes that it assembles. It fails unless each
of the six Linux runtimes has `runtimes/<rid>/native/libarchive.so`, of the ELF class and machine of
its architecture, which needs (DT_NEEDED) musl's `libc.so` alone for a `linux-musl-*` runtime, and
glibc's `libc.so.6` but not `libc.so` for the other `linux-*` runtimes, and unless no other
`linux-*` folder has a library. `READELF` names the readelf that it uses. Without it the tests
would not find the glibc and musl libraries swapped: musl's dynamic linker loads the glibc
`linux-x64` library as well, since it takes `libc.so.6` and `libpthread.so.0` to be itself and
provides every symbol that the library imports, so the tests in Alpine x64 pass with either library.
The tests check which library is loaded where `LIBARCHIVE_NET_EXPECT_RID` names a runtime:
`LoadedLibraryTests` (`Test.LibArchive.Net/LoadedLibraryTests.cs`) fails unless the library whose
code LibArchive.Net's P/Invokes run is `runtimes/<rid>/native/libarchive.so` of that runtime, and
is the only file named `libarchive.so` that the process maps. The package job sets it to
`linux-x64`, and the test-alpine job to `linux-musl-x64`; without it the test is skipped.

#### The load tests

The load test program is built from `linux-loadtest.c` (`main`, and the checks that need no
files), `linux-loadtest-files.c` (archives in files), `linux-loadtest-lfs32.c` (glibc's 32-bit file
functions) and `linux-loadtest-guard.c` (the stack protector on 32-bit ARM), with
`linux-loadtest.h`. Run as `loadtest-<rid> <path to libarchive.so>`, it exits non-zero unless:
- the library loads with every symbol bound at once (`RTLD_NOW`) and a little of its API works;
- names are UTF-8 in the "C" locale, a XAR archive written in memory reads back, and a Zstandard
  zip entry of 128 KiB reads back in full (see "The libarchive patches" above);
- zip, 7-Zip, XAR, ISO 9660 and ustar archives that the library writes to files, in a directory
  that it makes in `$TMPDIR` or `/tmp`, read back whole: from their path, again skipping all but
  the last entry's data, as three volumes, and from a `FILE`; and the library writes a file with
  data in its middle only through `archive_write_disk`, and lists the directory with
  `archive_read_disk`;
- in a 32-bit glibc process, the library calls none of glibc's file functions with a 32-bit
  `off_t` (or that open files without `O_LARGEFILE`): the program defines each of them itself,
  counts the library's calls of it and passes them on to glibc's;
- on 32-bit ARM, the library's `__stack_chk_guard` holds the first bytes of the process's
  `AT_RANDOM`, with the lowest byte cleared, and its `__stack_chk_fail`, called in a child
  process, ends it as the one that the C library exports ends another: by the same signal, after
  the same output on stderr, which the program reads through a pipe. With glibc that is SIGABRT
  after glibc's message, and with musl SIGILL with no output; the program has no message of its
  own to compare them with. glibc writes the message to stderr even in a process with a terminal
  (2.31, 2.35, 2.36 and 2.41 were seen to), where older glibc wrote it to `/dev/tty` unless
  `LIBC_FATAL_STDERR_` was set.

`build_load_test` builds `loadtest-<rid>` for the library's C library, as a dynamically linked
program: for glibc with `-rdynamic`, which exports its stand-ins for glibc's 32-bit file functions,
and with `-ldl`, as `dlopen` is in libdl before glibc 2.34; for musl with `-dynamic`, without which
zig links it statically, and it must then need musl's `libc.so` alone. The `linux-arm` program is
built for glibc 2.28, with the library's 64-bit `off_t` but not its 64-bit `time_t`, which needs
glibc 2.34, so that where glibc is older than 2.34 the dynamic loader refuses the library, not the
program before it.

`test_load_glibc` runs the program with a glibc library on an x86_64 Linux machine: natively for
`linux-x64`, and for `linux-arm64` and `linux-arm` under `qemu-aarch64` and `qemu-arm`, with the
dynamic loader and libraries of Ubuntu's cross runtime of glibc
(`/usr/aarch64-linux-gnu/lib/ld-linux-aarch64.so.1`,
`/usr/arm-linux-gnueabihf/lib/ld-linux-armhf.so.3`). On another machine, or without the emulator
or the cross runtime, it skips the test with a warning, except under GitHub Actions on Linux,
where that fails the build. The build does not run a musl library.

The program is kept beside the library, and CI runs each library with it in containers (the
`linux-load-test` job; see [CI/CD Integration](#cicd-integration)). So can anyone with Docker,
from the directory that holds the two files; for another architecture, Docker needs QEMU's
user-mode emulators registered with binfmt_misc, as Docker Desktop has them:

```bash
docker run --rm --platform linux/arm/v7 -v "$PWD:/work" -w /work alpine:3.21 \
    ./loadtest-linux-musl-arm ./libarchive-linux-musl-arm.so
```

#### Building on macOS

`build-linux.sh` runs on macOS as on Linux, with Homebrew's binutils and pkg-config (`brew install
binutils pkg-config`), and `build-all.sh --linux` builds all six libraries there. The glibc load
tests need an x86_64 Linux machine and are skipped, so run the load test programs in containers,
as above, or leave them to CI.

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

## Build warnings

The builds are kept free of compiler, linker and configure warnings. Of lz4, zstd and
bzip2 every script builds just the static library (bzip2's `bzip2recover` warned), no
script builds libarchive's programs, and the Linux build makes just libxml2's library,
not its programs. Warnings that came from the dependencies' own build machinery are
avoided as follows:

- **lz4's deprecated `LZ4_resetStreamHC()`**, which libarchive's lz4 writer called:
  `libarchive-lz4-init-stream-hc.patch` replaces the call (see "The libarchive patches" above).
- **libtool's `-single_module` check (macOS).** It links a test library with that
  linker flag and copies the linker's warning about it ("-single_module is obsolete")
  into the configure output. `build-macos.sh` gives the check's answer, `no`, as the
  configure cache variable `lt_cv_apple_cc_single_mod`, so it does not run. Only
  libtool's shared library links would use the flag, and every library that libtool
  builds here is static.
- **Universal archives (macOS).** The objects are built for both architectures at once.
  Apple's `ar`, asked to index an archive of them itself (`ar rcs`), warns that it "will
  be fat and ar(1) will not be able to operate on it"; `ranlib` run on its own makes the
  same archive without a warning. lz4's and zstd's Makefiles are given `ARFLAGS=rc` and
  their archives indexed with `ranlib`, and `build_lzo_shim` archives the same way.
- **Tools that configure looks for under the host triplet (Linux and Windows).** For a
  cross build, configure looks for `<host>-pkg-config` and libtool's `<host>-mt`, and
  takes the build machine's unprefixed ones when they are missing, with the warning
  "using cross tools not prefixed with host triplet". `export_cross_build_tools` (in
  `build-config.sh`) names them instead: the build machine's `pkg-config`, confined by
  `PKG_CONFIG_LIBDIR` to the `.pc` files the build itself installs, as a cross
  pkg-config would be, and `MANIFEST_TOOL=:`, libtool's own value for no manifest tool
  (neither the MinGW toolchains nor zig has one, and the build machine's `mt` is the tape
  drive program). The Linux builds also link GNU binutils into their tool directory under
  the host triplet (see [The compiler and the tool
  directory](#the-compiler-and-the-tool-directory)).
- **zig's versioned glibc targets (Linux).** zig rejects a target with a glibc version
  in a query that only prints something ("version '.2.28' in target triple ... is
  invalid"), and libtool's configure prints that for `$CC -print-search-dirs`.
  `zig-cc.sh` gives such queries the target without its version.

Do not silence a new warning with `-w`, a broad `-Wno-*` or the like. Stop building what
the libraries do not ship, or fix the flags or code at fault; for a warning in a
dependency's own sources, make a small, provably equivalent change (for libarchive, in a
patch of its own: see "The libarchive patches" above) or use a switch the dependency
provides for that one warning, scoped to its build, and record it here.

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

Run on their own, the Linux and macOS scripts leave their library in `~/libarchive-native/` (`build-linux.sh <rid>` as `libarchive-<rid>.so`, with its load test program, `loadtest-<rid>`), and `build-windows.sh` leaves its DLL in the current directory.

All the scripts keep two caches between builds, in a directory of their own, `$XDG_CACHE_HOME/libarchive-net/` (`~/.cache/libarchive-net/` when `XDG_CACHE_HOME` is unset): `downloads/` holds the downloaded source tarballs and the zig tarball, and `config-cache/` the configure results. Set `XDG_CACHE_HOME` to move them. CI caches the same two directories, and for the Linux builds zig's own cache, `~/.cache/zig`, as well.

## Updating Library Versions

A weekly workflow runs `scripts/check-native-updates.sh`, which looks for new releases and opens a pull request that updates their version and checksum variables; see [scripts/README.md](../scripts/README.md). To update library versions by hand:

1. Edit `build-config.sh`
2. Update the version variables (e.g., `LIBARCHIVE_VERSION="3.8.0"`)
3. Update the matching `*_SHA256` variables to the SHA256 of each new tarball: the builds verify each library tarball against them and fail on a mismatch
4. Update the URLs if the download pattern changed
5. For a new libarchive release, check that the patches in `LIBARCHIVE_PATCHES` still apply, in order, and rework any that does not (see "The libarchive patches" above): every build fails otherwise
6. Test the build on each platform

zig is updated by hand, from the lines that the update checker reports for a new release (see [zig](#zig)).

## CI/CD Integration

The GitHub Actions workflow (`.github/workflows/build.yml`) uses these scripts:

1. **macOS job**: Runs `build-macos.sh`, uploads `libarchive.dylib`
2. **native job** (one job per target, on Ubuntu 24.04 x64):
   - Runs `build-linux.sh <rid>` for each of the six Linux runtimes, and uploads the library with its load test program, `loadtest-<rid>` (artifact `native-<rid>`). The `linux-arm64` and `linux-arm` rows install QEMU user-mode emulation and Ubuntu's cross runtime of glibc (`libc6-arm64-cross`, `libc6-armhf-cross`) for the build's own load test (`test_load_glibc`), and every Linux row caches zig's cache, `~/.cache/zig`
   - Runs `build-windows.sh` for x86, x64 and arm64
   - Uploads each library
3. **linux-load-test job** (one job per library and distribution): runs each Linux library with its load test program in a container, with `docker run --platform`, under QEMU user-mode emulation for arm64 and arm (`docker/setup-qemu-action`). Each library runs in the oldest distribution of its C library that can be tested, and in a current one: `linux-x64` in AlmaLinux 8 (glibc 2.28) and Debian 13; `linux-arm64` in AlmaLinux 8 and Ubuntu 24.04; `linux-arm` in Ubuntu 22.04 (glibc 2.35, the oldest armhf glibc that can be tested) and Debian 13; each musl library in Alpine 3.21 (musl 1.2.5) and Alpine 3.24. In Debian 11 (glibc 2.31) the `linux-arm` library must be refused: that row fails unless the program fails with the dynamic loader's error that a `GLIBC_` version that the library needs is not found. Every image is pinned by the digest of its index.
4. **package job**:
   - Downloads all the native libraries
   - Places them in runtimes/, each Linux library in its own runtime's folder
   - Runs `verify-exports.sh` on the six Linux libraries, and `verify-linux-runtimes.sh` on the runtimes folder (see [The checks](#the-checks))
   - Creates the NuGet package and tests it on Ubuntu x64, where .NET loads the `linux-x64` library, as `LoadedLibraryTests` checks (`LIBARCHIVE_NET_EXPECT_RID=linux-x64`): on the test project's .NET target, and on .NET 8, which `-p:LibArchiveNetTestNet8=true` adds to the test project's targets. LibArchive.Net has no build for .NET 8, so there the tests run its .NET Standard 2.0 build, which finds and loads the native library itself. setup-dotnet installs .NET 8 beside the SDK that `global.json` names
5. **test-net462 job**: tests the package on Windows, on .NET Framework 4.6.2 and on modern .NET
6. **test-alpine job**: tests the package on .NET in Alpine x64, where .NET loads the `linux-musl-x64` library, as `LoadedLibraryTests` checks (`LIBARCHIVE_NET_EXPECT_RID=linux-musl-x64`), on the test project's .NET target and on .NET 8, as the package job does. It installs the .NET SDK that `global.json` names, built for musl, and the .NET 8 runtime for musl, with dotnet-install on the runner, then runs the package job's restore, build and test commands with a plain `docker run`, as GitHub's JavaScript actions do not run in an Alpine container job, in the .NET SDK's Alpine image, pinned by digest, with that SDK mounted read-only over the image's own in `/usr/share/dotnet`. The image provides Alpine's system libraries alone: its own SDK, pinned with it, would fall behind `global.json` once Dependabot moved that on. The tests stop unless `dotnet --version` in the container gives the version that `global.json` names
7. **build-and-test job** (the required status check): fails unless all the jobs above succeeded; the release jobs need the package, test-net462, linux-load-test and test-alpine jobs

The built libraries are cached between runs, keyed on the contents of `native/**`; tag builds always rebuild them from source.

## Troubleshooting

### Linux Build Issues

**Problem:** "zig ... is pinned for x86_64 and aarch64 Linux and macOS build machines only"
- Build on one of those four machines: `build-config.sh` pins zig's tarball for them alone (see [zig](#zig))

**Problem:** "Failed to download and verify zig"
- Check internet connectivity to ziglang.org
- A cached zig tarball is checked on every use, and downloaded again if it does not match its pinned SHA-256 (see [zig](#zig)). If every attempt reports a mismatch, the server is serving a tarball that differs from the pinned one: try again later rather than working around the check, and if it persists, compare the SHA-256s in zig's index of its releases with those in `build-config.sh`

**Problem:** "GNU ar was not found" (or ranlib, nm, readelf, objdump or strip), or "... is not the readelf of GNU binutils"
- Install GNU binutils (Debian/Ubuntu: `apt-get install binutils`; macOS: `brew install binutils`), or set `BINUTILS_DIR` to the directory that holds them under their unprefixed names, such as `/usr/aarch64-linux-gnu/bin` for Ubuntu's `binutils-aarch64-linux-gnu` (see [Requirements](#linux-x64-armv7-arm64-glibc-and-musl))

**Problem:** `verify_glibc_imports` fails, naming symbol versions that glibc 2.28 (or 2.34) does not have, or `-z defs` fails the link on an undefined symbol
- The code needs a glibc function that is newer than the library's baseline, typically after a dependency update. Keep the dependency's use of it out of the build, for example with a configure option or cache variable, or raise the baseline (the version that ends the zig target in `build-linux.sh`), which drops the distributions with an older glibc

**Problem:** loading a Linux library fails with "version `GLIBC_2.xx' not found"
- The system's glibc is older than the library's baseline (see the table in [Linux](#linux-x64-armv7-arm64-glibc-and-musl)): the library cannot be used there

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
6. On Linux, the link (`-z defs`) and `verify_glibc_imports` fail the build if the new library needs a C library function that is newer than a library's baseline, or, on 32-bit ARM, one of glibc's functions with a 32-bit `time_t` or `off_t` (see [The checks](#the-checks)); and add the new library to THIRD-PARTY-NOTICES.txt
7. Test on all platforms

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
- **Windows**: 15-20 minutes (all 3 architectures)

Each Linux runtime is built in a job of its own.

Optimization strategies:
- ccache for the macOS build and sccache for the Windows builds (already enabled in workflow). The Linux builds use neither: neither can sit in front of zig
- Cache the download and configure caches (`~/.cache/libarchive-net/`) between runs, and zig's own cache (`~/.cache/zig`) for each Linux target (already enabled)

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
