#!/bin/sh
# Build libarchive for Linux x86-64 using musl-libc for static linking

set -e

# Load shared configuration
. "$(dirname "$0")/build-config.sh"

echo "Downloading prebuilt musl cross-compiler toolchain from Bootlin..."
# Use Bootlin's stable x86-64 musl toolchain (GCC 14.3.0, tested and verified)
TOOLCHAIN_URL="https://toolchains.bootlin.com/downloads/releases/toolchains/x86-64/tarballs/x86-64--musl--stable-2025.08-1.tar.xz"
TOOLCHAIN_DIR="x86-64--musl--stable-2025.08-1"

curl -sL "$TOOLCHAIN_URL" | tar xJf -

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/x86_64-buildroot-linux-musl/sysroot"
export PATH="$TOOLCHAIN_PREFIX/bin:$PATH"
export CC=x86_64-linux-gcc
export CXX=x86_64-linux-g++

# Keep PREFIX for our built libraries (same as before)
export PREFIX="${PREFIX:-$(pwd)/local}"

# Set compiler flags for static linking
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS -static-libgcc"
export CXXFLAGS="-fPIC -O2 $CPPFLAGS -static-libstdc++ -static-libgcc"
export LDFLAGS="-L$PREFIX/lib -static"

echo "Toolchain installed:"
$CC --version | head -n1
echo ""

# Download all libraries
download_all_libraries

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
make -j$NCPU -sC lz4-${LZ4_VERSION} install

echo "Building zstd ${ZSTD_VERSION}..."
make -j$NCPU -sC zstd-${ZSTD_VERSION} install

echo "Building bzip2 ${BZIP2_VERSION}..."
make -j$NCPU -sC bzip2-${BZIP2_VERSION} install PREFIX=$PREFIX CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64" CC=$CC

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
./autogen.sh --cache-file=$CONFIGCACHE --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-${ZLIB_VERSION} --with-lzma=$PREFIX/../xz-${XZ_VERSION}
make -sj$NCPU install
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --cache-file=$CONFIGCACHE --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2
make -sj$NCPU install
cd ..

echo "Creating final shared library..."
gcc -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a ${TOOLCHAIN_SYSROOT}/usr/lib/libc.a -nostdlib

echo "Testing library..."
cat > test.c <<EOT
#include <stdio.h>
#include <dlfcn.h>
int main() {
   printf("libarchive.so=%p\n",dlopen("./libarchive.so",RTLD_NOW));
   return 0;
}
EOT
gcc -o test test.c
./test
file libarchive.so
ldd libarchive.so || true

echo "Building native test..."
gcc -o nativetest native/nativetest.c local/lib/libarchive.a -Llocal/lib -Ilocal/include -llz4 -lzstd -lbz2
./nativetest

echo "Linux build complete: libarchive.so"
