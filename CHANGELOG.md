v0.2.0 - TBD

- Add .NET Standard 2.0 support for broader compatibility
- Add Windows ARM64 native library support
- Fix SYSLIB1050: Mark LibArchiveReader as partial for .NET 8.0 source generators
- Statically link C runtime to eliminate UCRT/msvcrt dependency on Windows
- Fix Windows build: Use LLVM-MinGW for ARM64 cross-compilation
- Fix Windows build: Only build static libraries for lz4/zstd to avoid LLVM lld linker issues
- Fix Windows build: Only install bzip2 library, not command-line programs

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
