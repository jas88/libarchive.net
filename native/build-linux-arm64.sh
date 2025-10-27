#!/bin/sh
# Build libarchive for Linux ARM64 (aarch64) using musl-libc for static linking

set -e

# Load shared configuration
. "$(dirname "$0")/build-config.sh"

echo "Downloading prebuilt musl cross-compiler toolchain from Bootlin..."
# Use Bootlin's stable aarch64 musl toolchain
TOOLCHAIN_URL="https://toolchains.bootlin.com/downloads/releases/toolchains/aarch64/tarballs/aarch64--musl--stable-2025.08-1.tar.xz"
TOOLCHAIN_DIR="aarch64--musl--stable-2025.08-1"

curl -sL "$TOOLCHAIN_URL" | tar xJf -

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/aarch64-buildroot-linux-musl/sysroot"
export PATH="$TOOLCHAIN_PREFIX/bin:$PATH"
export CC=aarch64-linux-gcc
export CXX=aarch64-linux-g++
export AR=aarch64-linux-ar
export RANLIB=aarch64-linux-ranlib

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

# Download all libraries if not already present
if [ ! -d "libarchive-${LIBARCHIVE_VERSION}" ]; then
    echo "Downloading library sources..."
    download_all_libraries
else
    echo "Using pre-downloaded library sources"
fi

# Build compression libraries (static only to avoid conflicts with -static LDFLAGS)
echo "Building lz4 ${LZ4_VERSION}..."
cd lz4-${LZ4_VERSION}/lib
make -j$NCPU liblz4.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp liblz4.a $PREFIX/lib/
cp lz4.h lz4hc.h lz4frame.h $PREFIX/include/
cd ../..

echo "Building zstd ${ZSTD_VERSION}..."
cd zstd-${ZSTD_VERSION}/lib
make -j$NCPU libzstd.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp libzstd.a $PREFIX/lib/
cp zstd.h zstd_errors.h zdict.h $PREFIX/include/
cd ../..

echo "Building bzip2 ${BZIP2_VERSION}..."
cd bzip2-${BZIP2_VERSION}
make -j$NCPU libbz2.a CC=$CC AR=$AR RANLIB=$RANLIB CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64"
mkdir -p $PREFIX/lib $PREFIX/include
cp libbz2.a $PREFIX/lib/
cp bzlib.h $PREFIX/include/
cd ..

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --prefix=$PREFIX --disable-shared --enable-static
make -sj$NCPU install
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-${ZLIB_VERSION} --with-lzma=$PREFIX/../xz-${XZ_VERSION}
make -sj$NCPU install
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2 --disable-shared --enable-static
make -sj$NCPU install
cd ..

echo "Creating final shared library..."
gcc -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a ${TOOLCHAIN_SYSROOT}/lib/libc.a -nostdlib

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
