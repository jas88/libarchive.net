#!/bin/sh
# Build libarchive for macOS as universal binary (x86_64 + arm64)

set -e

# Load shared configuration
. "$(dirname "$0")/build-config.sh"

# Ensure build tools are available
echo "Installing required build tools..."
brew install autoconf automake libtool 2>/dev/null || true

# macOS-specific build settings
export CPPFLAGS="-I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib -liconv"
export CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64 -arch arm64 -arch x86_64"

# Download all libraries if not already present
if [ ! -d "libarchive-${LIBARCHIVE_VERSION}" ]; then
    echo "Downloading library sources..."
    download_all_libraries
else
    echo "Using pre-downloaded library sources"
fi

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
make -j$NCPU -sC lz4-${LZ4_VERSION} install PREFIX=$PREFIX CFLAGS="$CFLAGS"

echo "Building bzip2 ${BZIP2_VERSION}..."
make -j$NCPU -sC bzip2-${BZIP2_VERSION} install PREFIX=$PREFIX CFLAGS="$CFLAGS"

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --cache-file=$CONFIGCACHE --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --cache-file=$CONFIGCACHE --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-${ZLIB_VERSION} --with-lzma=$PREFIX/../xz-${XZ_VERSION}
make -sj$NCPU install
cd ..

echo "Building zstd ${ZSTD_VERSION}..."
make -j$NCPU -sC zstd-${ZSTD_VERSION} install

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS="-L$PREFIX -lxml2"
./configure --cache-file=$CONFIGCACHE --prefix=$PREFIX --enable-silent-rules --disable-dependency-tracking --enable-static --disable-shared --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-rpath --enable-posix-regex-lib=libc --enable-xattr --enable-acl --enable-largefile --with-pic --with-zlib --with-bz2lib --with-libb2 --with-iconv --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-cng
make -sj$NCPU install
cd ..

echo "Creating universal binary..."
clang -arch arm64 -arch x86_64 -dynamiclib -shared -o libarchive.dylib -Wl,-force_load local/lib/libarchive.a local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a -liconv

echo "Testing library..."
file libarchive.dylib
otool -L libarchive.dylib

echo "Building native test..."
gcc -o nativetest native/nativetest.c local/lib/libarchive.a -Llocal/lib -Ilocal/include -llz4 -lzstd -liconv -lbz2
./nativetest

echo "macOS build complete: libarchive.dylib"
