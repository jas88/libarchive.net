v0.1.5 - Jan 29 2024

- Build Mac component on MacOS 12 not 11
- Bump libarchive from 3.6.2 to 3.7.2
- Bump libzstd from 1.5.2 to 1.5.5
- Fetch liblzo source using HTTPS not HTTP - all dependencies now HTTPS
- Bump libxml2 from 2.10.3 to 2.12.4
- Bump zlib from 1.3 to 1.3.1

v0.1.4 - Jun 28 2023

- Add support for multi-volume archives

v0.1.3 - Dec 23 2022

- Add optional argument to LibArchiveReader to configure blocksize, default 1MiB

v0.1.2 - Oct 27 2022

- Fix memory leak on Linux in musl-libc malloc

v0.1.1 - Sep 22 2022

- First full release, supporting Linux, Windows and MacOS x64 plus MacOS ARM64
