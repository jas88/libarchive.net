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

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
make -j$NCPU -sC lz4-${LZ4_VERSION} install PREFIX=$PREFIX CFLAGS="$CFLAGS"

echo "Building bzip2 ${BZIP2_VERSION}..."
make -j$NCPU -sC bzip2-${BZIP2_VERSION} install PREFIX=$PREFIX CFLAGS="$CFLAGS"

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --cache-file=$(get_config_cache darwin-universal) --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
# No autotools regeneration here: the tarball ships consistent generated files and
# download_all_libraries has already stamped them in dependency order.
./configure --cache-file=$(get_config_cache darwin-universal) --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --cache-file=$(get_config_cache darwin-universal) --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-${ZLIB_VERSION} --with-lzma=$PREFIX/../xz-${XZ_VERSION}
make -sj$NCPU install
cd ..

echo "Building zstd ${ZSTD_VERSION}..."
make -j$NCPU -sC zstd-${ZSTD_VERSION} install

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
# No autotools regeneration here: running aclocal would rebuild aclocal.m4 from the
# host's libtool.m4 while leaving the tarball's older ltmain.sh in place, which fails
# with "libtool: Version mismatch error" once the runner's libtool outpaces it.
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS="-L$PREFIX -lxml2"
./configure --cache-file=$(get_config_cache darwin-universal) --prefix=$PREFIX --enable-silent-rules --disable-dependency-tracking --enable-static --disable-shared --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-rpath --enable-posix-regex-lib=libc --enable-xattr --enable-acl --enable-largefile --with-pic --with-zlib --with-bz2lib --with-libb2 --with-iconv --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-cng
make -sj$NCPU install
cd ..

echo "Creating universal binary..."
clang -arch arm64 -arch x86_64 -dynamiclib -shared -o libarchive.dylib -Wl,-force_load local/lib/libarchive.a local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a -liconv

echo "Testing library..."
file libarchive.dylib
otool -L libarchive.dylib

echo "Building native test..."
gcc -o nativetest "${SCRIPT_DIR}/nativetest.c" local/lib/libarchive.a -Llocal/lib -Ilocal/include -llz4 -lzstd -llzma -lz -liconv -lbz2
./nativetest

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.dylib "${OUTPUT_DIR}/libarchive.dylib"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "macOS build complete: ${OUTPUT_DIR}/libarchive.dylib"
