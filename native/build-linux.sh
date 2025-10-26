#!/bin/sh
# Build libarchive for Linux x86-64 using musl-libc for static linking

set -e

# Load shared configuration
. "$(dirname "$0")/build-config.sh"

# Linux-specific build settings
GCC_MAJOR=$(echo $GCC_VERSION | cut -d. -f1)
GCC_MINOR=$(echo $GCC_VERSION | cut -d. -f2)
export CPPFLAGS="-I$PREFIX/include -I$PREFIX/x86_64-linux-musl/include -I$PREFIX/lib/gcc/x86_64-linux-musl/${GCC_MAJOR}.${GCC_MINOR}.0/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS -static-libgcc"
export CXXFLAGS="-fPIC -O2 -I$PREFIX/x86_64-linux-musl/include/c++/${GCC_MAJOR}.${GCC_MINOR}.0 -I$PREFIX/x86_64-linux-musl/include/c++/${GCC_MAJOR}.${GCC_MINOR}.0/x86_64-linux-musl $CPPFLAGS -static-libstdc++ -static-libgcc -include sys/time.h"
export LDFLAGS="-L$PREFIX/lib -static"
export PATH="$PREFIX/bin:$PREFIX/x86_64-linux-musl/bin:$PATH"

echo "Building musl cross-compiler toolchain..."
curl -sL https://github.com/richfelker/musl-cross-make/archive/refs/heads/master.zip > musl-git.zip
unzip -q musl-git.zip
rm musl-git.zip

# Download all libraries
download_all_libraries

# Build musl cross-compiler
cd musl-cross-make-master
cat > config.mak <<EOC
GNU_SITE = https://mirrors.ocf.berkeley.edu/gnu/
TARGET=x86_64-linux-musl
MUSL_VER = ${MUSL_VERSION}
GCC_VER = ${GCC_VERSION}
BINUTILS_VER = ${BINUTILS_VERSION}
COMMON_CONFIG += --disable-nls
GCC_CONFIG += --disable-libitm
GCC_CONFIG += --enable-default-pie
DL_CMD = wget -c --no-check-certificate -O
EOC
echo "Building musl cross-compiler (this may take a while)..."
make -sj$NCPU install OUTPUT=$PREFIX 2>&1 >musl.log || cat musl.log

# Use ccache if available
if command -v ccache >/dev/null 2>&1; then
    export CC="ccache x86_64-linux-musl-gcc"
    export CXX="ccache x86_64-linux-musl-g++"
else
    export CC=x86_64-linux-musl-gcc
    export CXX=x86_64-linux-musl-g++
fi
cd ..

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
gcc -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a local/x86_64-linux-musl/lib/libc.a -nostdlib

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
