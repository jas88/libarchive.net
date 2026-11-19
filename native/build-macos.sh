#!/bin/bash
# Build libarchive for macOS as universal binary (x86_64 + arm64)

set -e

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-macos"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

# Ensure build tools are available (libxml2's autogen.sh runs autoreconf)
echo "Installing required build tools..."
brew install autoconf automake libtool 2>/dev/null || true

# No automake-1.17/aclocal-1.17 symlinks: nothing invokes a version-specific
# automake now that the tarballs' generated files are left in place. Such a
# symlink would also be actively harmful, letting an aclocal.m4 rebuild rule
# silently regenerate aclocal.m4 from the host libtool.m4 and reintroduce the
# "libtool: Version mismatch error" this stamping is meant to prevent.

# macOS-specific build settings
export CPPFLAGS="-I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib -liconv"
export CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64 -arch arm64 -arch x86_64"

# The answer to libtool's configure check "for -single_module linker flag", given as a
# configure cache variable so that the check does not run. It links a test library
# with the flag and copies the linker's warning about it into the configure output
# ("ld: warning: -single_module is obsolete", from the current Xcode linker), then
# answers "no". The answer only matters to libtool's shared library links, and every
# library built with libtool here is static.
export lt_cv_apple_cc_single_mod=no

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Every narrow string UTF-8, whatever the locale (see "The libarchive patch" in BUILD.md)
patch_libarchive "${SCRIPT_DIR}" "libarchive-${LIBARCHIVE_VERSION}"

# Build compression libraries: only the static library and headers of each, which is
# all that libarchive and the final link use, as on the other platforms. Their install
# targets would also build lz4's program and shared library and bzip2's programs.
mkdir -p "$PREFIX/lib" "$PREFIX/include"

# The objects are universal (CFLAGS has two -arch flags). Asked to index an archive of
# them itself ("ar rcs", which lz4's and zstd's Makefiles use), Apple's ar warns that
# it "will be fat and ar(1) will not be able to operate on it"; ranlib run on its own
# makes the same universal archive without a warning. So those Makefiles get
# ARFLAGS=rc, and ranlib indexes their archives afterwards.
echo "Building lz4 ${LZ4_VERSION}..."
make -j"$NCPU" -sC "lz4-${LZ4_VERSION}/lib" liblz4.a CFLAGS="$CFLAGS" ARFLAGS=rc
cp "lz4-${LZ4_VERSION}/lib/liblz4.a" "$PREFIX/lib/"
ranlib "$PREFIX/lib/liblz4.a"
cp "lz4-${LZ4_VERSION}/lib/lz4.h" "lz4-${LZ4_VERSION}/lib/lz4hc.h" "lz4-${LZ4_VERSION}/lib/lz4frame.h" "$PREFIX/include/"

echo "Building bzip2 ${BZIP2_VERSION}..."
make -j"$NCPU" -sC "bzip2-${BZIP2_VERSION}" libbz2.a CFLAGS="$CFLAGS"
cp "bzip2-${BZIP2_VERSION}/libbz2.a" "$PREFIX/lib/"
cp "bzip2-${BZIP2_VERSION}/bzlib.h" "$PREFIX/include/"

# lzop (LZO1X) support: lzokay behind the liblzo2 API, in place of the GPL-licensed liblzo2.
# This script sets no C++ compiler of its own (CI exports CXX="ccache clang++"), so fall
# back to the clang++ that belongs to the clang of the final link - for this one command
# only, so that the configure runs below see the same environment as before.
# CFLAGS is passed because it carries the two -arch flags that make the objects universal.
# shellcheck source-path=SCRIPTDIR disable=SC1091
. "${SCRIPT_DIR}/build-lzo-shim.sh"
CXX="${CXX:-clang++}" build_lzo_shim "${SCRIPT_DIR}/lzo-shim" "lzokay-${LZOKAY_COMMIT}" "$CFLAGS"

echo "Building zlib ${ZLIB_VERSION}..."
cd "zlib-${ZLIB_VERSION}"
./configure --static --prefix="$PREFIX"
make -sj"$NCPU" install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd "xz-${XZ_VERSION}"
# No autotools regeneration here: the tarball ships consistent generated files and
# download_all_libraries has already stamped them in dependency order.
./configure --cache-file="$(get_config_cache darwin-universal)" --with-pic --disable-shared --prefix="$PREFIX"
make -sj"$NCPU" install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd "libxml2-${LIBXML2_VERSION}"
# --with-zlib names where zlib was installed above: libxml2 adds -I<dir>/include and
# -L<dir>/lib, and records them for whatever links it. libxml2 has had no lzma
# support (and no --with-lzma option) since 2.15.0.
./autogen.sh --cache-file="$(get_config_cache darwin-universal)" --enable-silent-rules --disable-shared --enable-static --prefix="$PREFIX" --without-python --with-zlib="$PREFIX"
make -sj"$NCPU" install
cd ..

echo "Building zstd ${ZSTD_VERSION}..."
# Library only: the top-level install also builds the zstd CLI, whose lz4 support
# uses the unprefixed lz4frame enums removed in lz4 1.11.0. Matches the other platforms.
# ARFLAGS=rc and ranlib: see lz4 above.
make -j"$NCPU" -sC "zstd-${ZSTD_VERSION}/lib" install-static install-includes ARFLAGS=rc
ranlib "$PREFIX/lib/libzstd.a"

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd "libarchive-${LIBARCHIVE_VERSION}"
# No autotools regeneration here: running aclocal would rebuild aclocal.m4 from the
# host's libtool.m4 while leaving the tarball's older ltmain.sh in place, which fails
# with "libtool: Version mismatch error" once the runner's libtool outpaces it.
export LIBXML2_PC_CFLAGS="-I$PREFIX/include/libxml2"
export LIBXML2_PC_LIBS="-L$PREFIX/lib -lxml2"
./configure --cache-file="$(get_config_cache darwin-universal)" --prefix="$PREFIX" --enable-silent-rules --disable-dependency-tracking --enable-static --disable-shared --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --disable-rpath --enable-posix-regex-lib=libc --enable-xattr --enable-acl --enable-largefile --with-pic --with-zlib --with-bz2lib --with-libb2 --with-iconv --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-cng
make -sj"$NCPU" install
cd ..

echo "Creating universal binary..."
clang -arch arm64 -arch x86_64 -dynamiclib -shared -o libarchive.dylib -Wl,-force_load local/lib/libarchive.a local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a -liconv

echo "Testing library..."
file libarchive.dylib
otool -L libarchive.dylib

# Fail the build unless both halves of the universal library have lzop support (see build-lzo-shim.sh)
verify_lzo_support libarchive.dylib -arch arm64
verify_lzo_support libarchive.dylib -arch x86_64

echo "Building native test..."
# It also fails unless names are UTF-8 in the "C" locale, which it runs in, as a .NET
# process does: the libarchive patch is in the library (see BUILD.md). Its zip writer
# brings in libarchive's table of every writer, the XAR one (libxml2) among them.
gcc -o nativetest "${SCRIPT_DIR}/nativetest.c" local/lib/libarchive.a -Llocal/lib -Ilocal/include -lxml2 -llz4 -lzstd -llzma -lz -liconv -lbz2
./nativetest

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.dylib "${OUTPUT_DIR}/libarchive.dylib"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "macOS build complete: ${OUTPUT_DIR}/libarchive.dylib"
