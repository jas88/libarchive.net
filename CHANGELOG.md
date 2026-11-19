v0.3.2 - November 16, 2026

**Bug Fixes:**
- Fix `DllNotFoundException: Unable to load DLL 'archive'` on Windows under .NET Framework (the .NET Standard 2.0 build). The package deploys `archive.dll` to `runtimes\win-{arch}\native\`, which .NET Framework does not probe, and the library only loaded it explicitly on Linux and macOS. It now does the same on Windows.
- Fix the .NET Standard 2.0 build failing to initialise on Linux and macOS when running on .NET 10 or later. That happens when an app targeting a framework this package has no dedicated build for (such as `net6.0`) is rolled forward to a newer runtime. Since .NET 10, the assembly-wide `DllImportSearchPath.AssemblyDirectory` no longer falls back to the OS library search, so the `libdl` imports used to load the native library could not be resolved.
- Fix the Linux ARMv7 and ARM64 native libraries failing to load in processes that do not already provide libgcc's helper routines: in v0.3.1 they left integer division helpers (ARMv7) and 128-bit floating point helpers (ARM64) undefined. Every Linux library is now self-contained.
- Fix reading archives from seekable streams. The reader now registers a seek callback, so formats that must seek to read their headers, such as 7-Zip, can be read from a `Stream`. The stream's position when the reader is created is treated as the start of the archive, and `Reset()` returns to it. Contributed by @paoloambrosio in #293.
- Fix the .NET Standard 2.0 build on Linux and macOS keeping a stale copy of the native library in the application's base directory, where it copies the library for Mono: a copy left there by an earlier version of the package, or made in a process of the other C library, was never replaced. A copy that differs from the library loaded is now replaced, with a new copy renamed over it, so that a process which has the old one loaded is not disturbed; as before, a copy that cannot be made is no error.
- Fix every XAR archive ending with 20 bytes too many: the first 20 bytes of its compressed table of contents, as many as its SHA-1 checksum has. libarchive's XAR writer copied the heap out of its temporary file with the room for that checksum counted twice. Readers ignored the bytes. The native libraries now carry a patch to libarchive (`native/libarchive-xar-heap-length.patch`), so that an archive ends where its heap ends.
- Fix incorrect Unix permission defaults in `LibArchiveWriter` — bare `0644`/`0755`/`0444` literals were being interpreted as decimal in C# (no octal literal syntax), producing nonsensical permission bits. Files now correctly default to `rw-r--r--` (and `r--r--r--` for read-only attributes), directories to `rwxr-xr-x`. Reported and initial fix contributed by @JuanCalle1606 in #220.
- Fix creating AES-encrypted ZIP archives, the default encryption, on Linux. The Linux native libraries were built without a cryptography backend; they now statically link a minimal mbedTLS.
- Fix a password with the 7-Zip format silently producing an unencrypted archive, which read back in full without the password. libarchive can only encrypt ZIP, so 7-Zip now throws `NotSupportedException` before the output is created, as the other formats besides ZIP already did. With `EncryptionType.None` the password is ignored, for every format.
- `EncryptionType.AES192` now throws `NotSupportedException`, as libarchive's ZIP writer supports only AES-128 and AES-256, instead of an `ApplicationException` reading ``Undefined option: `encryption'``. `EncryptionType.None` with a password, which failed the same way for ZIP, now writes an unencrypted archive.
- `WithPassword` now throws `InvalidOperationException`, pointing to the constructor and `CreateMemoryWriter`, instead of failing and leaving the writer unusable. libarchive accepts encryption settings only before an archive is opened, and the writer opens it when it is created, so encryption cannot be enabled, changed or removed afterwards. With `EncryptionType.None` on a writer that is not encrypting, `WithPassword` does nothing.
- Fix non-ASCII entry names, in every format, on every platform, whatever the process locale. libarchive converted names through the C runtime's locale, which a .NET process leaves as "C".
  - On macOS and Linux, writing such a name failed in pax/tar and XAR, 7-Zip archives with one could not be read, and ZIP archives lacked the UTF-8 flag.
  - On Windows, names were stored double-encoded in pax, 7-Zip, XAR and ISO 9660 Joliet names, and the .NET Standard 2.0 build lost every character outside the ANSI code page.

  Names are now stored as UTF-8 (UTF-16 in 7-Zip and Joliet), with ZIP's UTF-8 flag, and read back as UTF-8. Archive files with non-ASCII paths now open on Windows.
- Fix reading a plain compressed file that is not an archive (`.gz`, `.bz2`, `.xz`, `.lzma`, `.zst`, `.lz4` or `.lzo`), which threw "Unrecognized archive format": the README's own example failed. Such a file now reads as one entry holding the decompressed data, from a path, from volumes or from a stream. So does compressed text that libarchive took for an mtree spec. A compressed file holding no data, like empty data, opens as an archive with no entries.
- Fix the file writer ignoring `blockSize`: its import of `archive_write_open_filename` had a third argument that the C function does not have. The file, stream and memory writers now set the block size, without padding the last block, so archives are byte-identical whatever the block size. A block size above `int.MaxValue` throws `ArgumentOutOfRangeException`.
- Fix the P/Invoke declarations to match libarchive 3.8.11:
  - every import and callback is explicitly cdecl, where Windows x86 defaulted to stdcall;
  - `size_t` parameters are `nuint`;
  - `mode_t` has its per-platform width;
  - `time_t`'s companion `long` is a C `long`.

  The entry stream's `Read` now validates its arguments.
- Fix a crash when disposing a stream reader: the native reader was freed after its stream callbacks were released, and freeing it still calls them.
- An entry whose name libarchive cannot convert is now returned with an empty name instead of being skipped silently.
- Names read from a format that declares its character set (ZIP with the UTF-8 flag, pax, 7-Zip, Joliet, XAR) are returned composed (NFC), as libarchive normalizes them. Other formats return names as stored, decoded as UTF-8.
- The .NET Standard 2.0 build marshals strings as UTF-8 instead of the Windows ANSI code page. A non-ASCII password on that build is therefore UTF-8, as other ZIP tools expect.

**API Improvements:**
- `LibArchiveWriter.GetUnixPermissions` now returns `System.IO.UnixFileMode` and uses `FileInfo.UnixFileMode` directly on .NET 7+, falling back to defaults when the platform reports `0` (Windows) or `-1` (file missing). On .NET Standard 2.0 an internal `UnixFileMode` polyfill mirrors the BCL flags enum so the same code paths and constants are used everywhere.
- Add a `CreateMemoryWriter(uint blockSize, ArchiveFormat format, ...)` overload. The existing signature is unchanged, so compiled callers keep working.

**Target Frameworks:**
- Add a `net11.0` build. The package is now built with the .NET 11 SDK.
- Drop the `net8.0` and `net9.0` builds, as .NET 8 and .NET 9 have reached end of support. The package now ships `netstandard2.0`, `net10.0` and `net11.0` builds, so apps targeting .NET 8 or .NET 9 get the .NET Standard 2.0 build. That build is not marked as trimming or Native AOT compatible: target .NET 10 or later for those.

**Native Library Updates:**
- Bump libarchive from 3.8.6 to 3.8.11
- Bump xz from 5.8.3 to 5.8.4
- Bump zlib from 1.3.2 to 1.4
- Bump libxml2 from 2.15.2 to 2.15.4
- Bump lz4 from 1.10.0 to 1.11.1
- Bump the Bootlin musl toolchains used for the Linux builds from 2025.08-1 to 2026.08-1
- Replace the GPL-licensed liblzo2 2.10 with lzokay (MIT), pinned to commit `db2df1f`, behind a small shim that provides the part of the liblzo2 API libarchive uses, so lzop-compressed data is still read in-process
- Add mbedTLS 3.6.8, statically linked into the Linux libraries only, for AES-encrypted ZIP archives
- Add `THIRD-PARTY-NOTICES.txt` at the root of the package, with the licence notices for the third-party code linked into the native libraries
- Stop building and shipping the Linux x86 library, which the loader never supported and for which .NET has no official runtime; a .NET 10+ app on an unofficial Linux x86 runtime, which could previously load the packaged library, now gets a `DllNotFoundException`
- Patch libarchive at build time (`native/libarchive-narrow-utf8.patch`) so that its narrow strings are UTF-8 on every platform, whatever the process locale, without changing the locale. Every native build fails if the patch does not apply exactly, and the update checker does not propose a libarchive release that the patch does not apply to
- Ship a native library of its own for each Linux runtime, linked dynamically against that runtime's C library, in place of one library per architecture with musl linked into it, which the glibc and musl runtimes shared. The libraries need:
  - glibc 2.28 or later for `linux-x64` and `linux-arm64` (RHEL 8 and later);
  - glibc 2.34 or later for `linux-arm`, which is built with a 64-bit `time_t` and `off_t`. On an older glibc the dynamic loader refuses to load it, where the previous library crashed or misbehaved;
  - musl 1.2.5 or later (Alpine 3.21 and later) for `linux-musl-x64`, `linux-musl-arm64` and `linux-musl-arm`.
- The Linux libraries export libarchive's API (`archive_*`) alone, and no longer the functions of the libraries linked into them, such as zlib's and zstd's, which other code in the process could bind to or take the place of.
- The .NET Standard 2.0 build loads the `linux-musl-<arch>` library in a musl process, which it recognises from an executable mapping of musl's dynamic linker in `/proc/self/maps`, or, when that cannot be read, from `dlsym` not finding glibc's `gnu_get_libc_version`. .NET 6 and later choose between the two libraries from the runtime identifier.

**Build & CI Improvements:**
- Build the native libraries in parallel, one job per target, and reuse the previous binaries when nothing under `native/` has changed
- Run the .NET Framework tests on Windows instead of under Mono on Linux, and test only the package that was just built
- Publish to nuget.org with Trusted Publishing instead of a stored API key
- Retry native source downloads that fail their checksum, fetch zlib from its GitHub release, and fall back to ftp.gnu.org when a GNU mirror serves a bad file
- Fix the macOS build failing on a libtool version mismatch
- Pin all GitHub Actions to exact versions
- Fail CI with instructions when `Directory.Build.props` is out of date for the SDK, instead of trying to push the fix
- Restrict Renovate to the Bootlin musl toolchains, which no other updater covers
- Fail the Linux builds if a library is not self-contained or does not load in a process that contains only musl, using QEMU for the ARMv7 and ARM64 load tests, and also load the x64 library from a glibc program
- Select the win32 MinGW compilers by name in the Windows build, which failed on the x86 and x64 targets when the apt package cache was restored
- Harden the native dependency update checker: apply an update only once its new tarball's SHA-256 is recorded and checked, replace `native/build-config.sh` atomically, accept only plain dotted version numbers from upstream, and send the GitHub token only to `api.github.com`, without following redirects; its workflow gives the default token no permissions
- Fix `native/build-all.sh` for local builds: copy the libraries from `~/libarchive-native`, where the platform scripts leave them. `--package` now takes its version from `PACKAGE_VERSION`, which MinVer previously overrode, and tests the packed package as CI does: the modern .NET target everywhere, and net462 on Windows only
- Remove the unused Perl update checker, `scripts/check-native-updates.pl`
- Verify each Bootlin toolchain download against the `.sha256` file that Bootlin publishes beside it, before every use, so a truncated download or an error page is never reused. Verify the LLVM-MinGW download against a pinned SHA-256
- Cache native source downloads and configure results under `${XDG_CACHE_HOME:-~/.cache}/libarchive-net/`. They were in `~/downloads`, which is `~/Downloads` on macOS's case-insensitive file system, and `~/config-cache`. Each native CI job now has its own download cache
- Stop tracking a prebuilt 2022 `archive.dll` (libarchive 3.6.1), which a local `dotnet pack` shipped as the only native library, and ignore `runtimes/*/native/`
- Warn (`LIBARCHIVENET001`) when packing without a native library for every runtime, naming the runtimes that have none
- Make every pre-commit hook pass on all files, and exclude patch files from the trailing-whitespace hook, which would change them
- Run the Windows package tests on the modern .NET target as well as .NET Framework 4.6.2
- Unpack fresh sources on every Windows native build, so a second local build no longer fails on the libarchive tree that the first one patched
- Keep the plain-text test fixtures byte-exact on Windows checkouts
- `scripts/generate-build-props.sh`: the .NET 13 SDK targets net12.0 and net13.0, and the .NET 14 SDK follows the same rule instead of targeting net14.0 alone
- Build the native libraries without compiler, linker, libtool or configure warnings:
  - build only the dependency libraries that are linked: no `bzip2recover`, no lz4 programs, and no libxml2 on Linux, where nothing links it;
  - pass libxml2 only the options it has;
  - stop libarchive's lz4 writer calling lz4's deprecated `LZ4_resetStreamHC`, through a hunk in the libarchive patch;
  - name the cross tools for cross-compiling configure runs.
- Make the build scripts pass shellcheck
- The Linux libraries are built with zig. CI load-tests each of them in containers of old and current distributions, and runs the package's tests on .NET in Alpine x64 as well as on Ubuntu.

**Documentation:**
- The package description and `PACKAGES.md` now describe what the package ships: reading and writing, the supported platforms and runtimes, and the libraries bundled on each platform. `PACKAGES.md`'s code samples now compile
- Correct API names, target frameworks and the license link in `README.md` and `docs/WRITE-SUPPORT.md`, and note that XAR is not available on Linux
- `native/BUILD.md`: liblzma is 0BSD (since XZ Utils 5.6.0), not public domain. It also gains the cache locations and a section on testing a package locally
- `README.md` and `docs/WRITE-SUPPORT.md` describe how each format stores entry names and how they are read back
- Correct the doc comments on the ZIP encryption variants (AES-128, AES-256, Traditional), on 7-Zip having no encryption, on XAR being unavailable on Linux, and on `CompressionType.Deflate` throwing `NotSupportedException`. Also correct the error-handling sample in `docs/WRITE-SUPPORT.md`

**Tooling Updates:**
- Bump .NET SDK from 10.0.201 to 11.0.100
- Bump MinVer from 7.0.0 to 8.0.0
- Bump NUnit from 4.5.1 to 5.0.0
- Bump NUnit3TestAdapter from 6.2.0 to 6.3.1
- Bump NUnit.Analyzers from 4.12.0 to 4.15.0
- Bump Microsoft.NET.Test.Sdk from 18.4.0 to 18.12.0
- Bump coverlet.collector from 8.0.1 to 10.1.1

v0.3.1 - April 7, 2026

**Native Library Updates:**
- Bump libarchive from 3.8.4 to 3.8.6
- Bump xz from 5.8.2 to 5.8.3
- Bump zlib from 1.3.1 to 1.3.2
- Bump libxml2 from 2.15.1 to 2.15.2

**Build & CI Improvements:**
- Add SHA256 checksum verification for native dependency downloads
- Reduce MSBuild message verbosity for native library copy
- Direct sccache invocation for faster builds
- Fix CI triggering for auto-created dependency PRs

**Tooling Updates:**
- Bump MinVer from 6.0.0 to 7.0.0
- Bump NUnit from 4.4.0 to 4.5.1
- Bump NUnit3TestAdapter from 6.0.1 to 6.2.0
- Bump NUnit.Analyzers from 4.11.2 to 4.12.0
- Bump Microsoft.NET.Test.Sdk from 18.0.1 to 18.4.0
- Bump coverlet.collector from 6.0.4 to 8.0.1

v0.3.0 - December 22, 2025

**Archive Write Support:**
- New `LibArchiveWriter` class for creating archives in multiple formats
- Supported formats: ZIP, 7-Zip, TAR, Ustar, PAX, CPIO, ISO 9660, XAR
- Compression algorithms: Gzip, Bzip2, XZ, LZMA, LZ4, Zstd, Compress, LZIP
- Encryption support: ZIP (AES-128/192/256, Traditional PKWARE), 7-Zip (AES-256)
- Zero-copy I/O with platform-specific optimizations (stackalloc on .NET 9, memory-mapped files for large files)
- Stream-based writing support (MemoryStream, FileStream)
- Batch file operations with `IProgress<FileProgress>` reporting
- Directory addition with recursive option and path mapping

**Reader API Improvements:**
- New `FirstEntry()` method for quick access to single-file archives
- New `Reset()` method to re-enumerate entries without reopening
- New `Entry.ReadAllBytes()` method to read entry content directly to byte array
- New `Entry.ReadAllText()` method to read entry content as string (with optional encoding)
- Stream-based reading via `LibArchiveReader(Stream)` constructor
- Simple one-liner extraction: `new LibArchiveReader(file).FirstEntry()!.ReadAllBytes()`

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
- Fixed `archive_write_data()` return value checks for proper error handling
- Fixed potential GC bug with delegate instances in stream-based writing

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
