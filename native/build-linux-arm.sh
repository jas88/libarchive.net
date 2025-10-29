#!/bin/bash
# Build libarchive for Linux ARM v7 (armv7-eabihf) using musl-libc for static linking

set -e

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-linux-arm"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

echo "Setting up armv7-eabihf musl cross-compiler toolchain from Bootlin..."
# Download toolchain to cache (does not unpack)
TOOLCHAIN_ARCHIVE=$(download_toolchain "$TOOLCHAIN_ARM_URL" "armv7-musl")

# Extract directory name from archive
TOOLCHAIN_DIR="${TOOLCHAIN_ARCHIVE##*/}"
TOOLCHAIN_DIR="${TOOLCHAIN_DIR%.tar.xz}"

# Unpack toolchain in build directory
echo "Unpacking toolchain in build directory..."
tar xJf "$TOOLCHAIN_ARCHIVE"

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/arm-buildroot-linux-musleabihf/sysroot"

# Verify toolchain was unpacked correctly
if [ ! -f "$TOOLCHAIN_PREFIX/bin/arm-linux-gcc" ]; then
    echo "ERROR: Toolchain compiler not found at $TOOLCHAIN_PREFIX/bin/arm-linux-gcc"
    echo "Directory contents:"
    ls -la "$TOOLCHAIN_PREFIX" 2>/dev/null || echo "  Directory does not exist"
    exit 1
fi

export CC=arm-linux-gcc
export CXX=arm-linux-g++
export AR=arm-linux-ar
export RANLIB=arm-linux-ranlib

# Generate sccache wrappers for compilers only (not ar/ranlib)
echo "Setting up sccache wrappers..."
mkdir -p .ccache-bin
for tool in gcc g++; do
    printf '#!/bin/sh\nexec sccache "%s/bin/arm-linux-%s" "$@"\n' "$TOOLCHAIN_PREFIX" "$tool" > .ccache-bin/arm-linux-$tool
    chmod +x .ccache-bin/arm-linux-$tool
done

# Add wrappers to PATH (before toolchain bin)
export PATH="$(pwd)/.ccache-bin:$TOOLCHAIN_PREFIX/bin:$PATH"

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

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

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
./configure --build=x86_64-pc-linux-gnu --host=arm-linux --prefix=$PREFIX --disable-shared --enable-static
make -sj$NCPU install
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
CHOST=arm-linux ./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --build=x86_64-pc-linux-gnu --host=arm-linux --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --build=x86_64-pc-linux-gnu --host=arm-linux --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX --with-lzma=$PREFIX
make -sj$NCPU install
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --build=x86_64-pc-linux-gnu --host=arm-linux --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2 --disable-shared --enable-static
make -sj$NCPU install
cd ..

echo "Creating merged static library with all dependencies..."
mkdir -p local/lib/merge_tmp
cd local/lib/merge_tmp
$AR x ../libarchive.a
$AR x ../libbz2.a
$AR x ../libz.a
$AR x ../libxml2.a
$AR x ../liblzma.a
$AR x ../liblzo2.a
$AR x ../libzstd.a
$AR x ../liblz4.a
$AR rcs ../libarchive.a *.o
cd ../../..
rm -rf local/lib/merge_tmp

echo "Creating final shared library..."
$CC -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive ${TOOLCHAIN_SYSROOT}/lib/libc.a -nostdlib

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

echo "=== Checking dynamic library dependencies ==="
ldd libarchive.so || echo "Statically linked (no dynamic dependencies)"

echo "=== Inspecting symbols ==="
${AR/ar/nm} -D libarchive.so | head -20 || true
${AR/ar/nm} libarchive.so | grep -c " T " | xargs echo "Defined symbols:"
${AR/ar/nm} libarchive.so | grep -c " U " | xargs echo "Undefined symbols:"

echo "Skipping native test (cross-compilation - cannot run ARMv7 binary on x86_64 host)"

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-linux-arm.so"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux ARM build complete: ${OUTPUT_DIR}/libarchive-linux-arm.so"
