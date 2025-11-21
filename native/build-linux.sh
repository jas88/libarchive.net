#!/bin/bash
# Build libarchive for Linux using musl-libc for static linking
# Supports multiple architectures via ARCH environment variable

set -e

# Detect architecture or use default
ARCH="${ARCH:-x86-64}"

# Architecture-specific configuration
case "$ARCH" in
    x86-64|x64|x86_64)
        ARCH_NAME="x64"
        TOOLCHAIN_URL="$TOOLCHAIN_X64_URL"
        TOOLCHAIN_NAME="x86-64-musl"
        COMPILER_PREFIX="x86_64-linux"
        SYSROOT_PREFIX="x86_64-buildroot-linux-musl"
        CONFIGURE_HOST=""
        ZLIB_CHOST=""
        NEED_LIBGCC=false
        ;;
    x86|i686)
        ARCH_NAME="x86"
        TOOLCHAIN_URL="$TOOLCHAIN_X86_URL"
        TOOLCHAIN_NAME="i686-musl"
        COMPILER_PREFIX="i686-linux"
        SYSROOT_PREFIX="i686-buildroot-linux-musl"
        CONFIGURE_HOST="--build=x86_64-pc-linux-gnu --host=i686-linux"
        ZLIB_CHOST="CHOST=i686-linux"
        NEED_LIBGCC=true
        ;;
    arm|armv7)
        ARCH_NAME="arm"
        TOOLCHAIN_URL="$TOOLCHAIN_ARM_URL"
        TOOLCHAIN_NAME="arm-musl"
        COMPILER_PREFIX="arm-linux"
        SYSROOT_PREFIX="arm-buildroot-linux-musleabihf"
        CONFIGURE_HOST="--build=x86_64-pc-linux-gnu --host=arm-linux"
        ZLIB_CHOST="CHOST=arm-linux"
        NEED_LIBGCC=false
        ;;
    arm64|aarch64)
        ARCH_NAME="arm64"
        TOOLCHAIN_URL="$TOOLCHAIN_ARM64_URL"
        TOOLCHAIN_NAME="aarch64-musl"
        COMPILER_PREFIX="aarch64-linux"
        SYSROOT_PREFIX="aarch64-buildroot-linux-musl"
        CONFIGURE_HOST="--build=x86_64-pc-linux-gnu --host=aarch64-linux"
        ZLIB_CHOST="CHOST=aarch64-linux"
        NEED_LIBGCC=false
        ;;
    *)
        echo "Error: Unsupported architecture: $ARCH"
        echo "Supported: x86-64, x86/i686, arm/armv7, arm64/aarch64"
        exit 1
        ;;
esac

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-linux-${ARCH_NAME}"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

echo "Building for Linux ${ARCH_NAME} (${COMPILER_PREFIX})..."
echo "Setting up ${TOOLCHAIN_NAME} cross-compiler toolchain from Bootlin..."

# Download toolchain to cache (does not unpack)
TOOLCHAIN_ARCHIVE=$(download_toolchain "$TOOLCHAIN_URL" "$TOOLCHAIN_NAME")

# Extract directory name from archive
TOOLCHAIN_DIR="${TOOLCHAIN_ARCHIVE##*/}"
TOOLCHAIN_DIR="${TOOLCHAIN_DIR%.tar.xz}"

# Unpack toolchain in build directory
echo "Unpacking toolchain in build directory..."
tar xJf "$TOOLCHAIN_ARCHIVE"

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/${SYSROOT_PREFIX}/sysroot"

# Verify toolchain was unpacked correctly
if [ ! -f "$TOOLCHAIN_PREFIX/bin/${COMPILER_PREFIX}-gcc" ]; then
    echo "ERROR: Toolchain compiler not found at $TOOLCHAIN_PREFIX/bin/${COMPILER_PREFIX}-gcc"
    echo "Directory contents:"
    ls -la "$TOOLCHAIN_PREFIX" 2>/dev/null || echo "  Directory does not exist"
    exit 1
fi

export CC=${COMPILER_PREFIX}-gcc
export CXX=${COMPILER_PREFIX}-g++
export AR=${COMPILER_PREFIX}-ar
export RANLIB=${COMPILER_PREFIX}-ranlib

# Generate sccache wrappers for compilers only (not ar/ranlib)
echo "Setting up sccache wrappers..."
mkdir -p .ccache-bin
for tool in gcc g++; do
    printf '#!/bin/sh\nexec sccache "%s/bin/%s-%s" "$@"\n' "$TOOLCHAIN_PREFIX" "$COMPILER_PREFIX" "$tool" > .ccache-bin/${COMPILER_PREFIX}-$tool
    chmod +x .ccache-bin/${COMPILER_PREFIX}-$tool
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
./configure $CONFIGURE_HOST --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared --with-pic
make -sj$NCPU install
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
$ZLIB_CHOST ./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
# Touch autotools-generated files to prevent rebuild attempts
touch aclocal.m4 configure Makefile.in */Makefile.in */*/Makefile.in 2>/dev/null || true
./configure $CONFIGURE_HOST --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --disable-shared --with-pic \
    --disable-xz --disable-xzdec --disable-lzmadec --disable-lzmainfo \
    --disable-lzma-links --disable-scripts --disable-doc \
    --disable-nls --disable-rpath
make -sj$NCPU install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh $CONFIGURE_HOST --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared \
    --with-zlib=$PREFIX --with-lzma=$PREFIX \
    --without-python --without-catalog --without-debug \
    --without-http --without-ftp --without-threads \
    --without-icu --without-history
make -sj$NCPU install
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure $CONFIGURE_HOST --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared \
    --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip \
    --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2 \
    --without-expat
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
if [ "$NEED_LIBGCC" = true ]; then
    # For 32-bit builds, we need libgcc for 64-bit division intrinsics (__udivdi3, etc.)
    LIBGCC_PATH=$($CC -print-libgcc-file-name)
    $CC -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive "$LIBGCC_PATH" ${TOOLCHAIN_SYSROOT}/lib/libc.a -nostdlib
else
    $CC -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive ${TOOLCHAIN_SYSROOT}/lib/libc.a -nostdlib
fi

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

echo "Building native test..."
gcc -o nativetest "${SCRIPT_DIR}/nativetest.c" local/lib/libarchive.a -Ilocal/include
./nativetest

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-linux-${ARCH_NAME}.so"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux ${ARCH_NAME} build complete: ${OUTPUT_DIR}/libarchive-linux-${ARCH_NAME}.so"
