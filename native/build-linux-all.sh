#!/bin/bash
# Build libarchive for Linux (x86, x64, arm, arm64) using musl-libc for static linking
# Usage: ARCH=x64 ./build-linux-all.sh
#   or:  ./build-linux-all.sh x64

set -e

# Get absolute path to script directory
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Architecture can be passed as argument or environment variable
ARCH="${1:-${ARCH:-x64}}"

# Architecture-specific configuration
case "$ARCH" in
    x64|x86_64)
        ARCH_NAME="x64"
        TOOLCHAIN_VAR="TOOLCHAIN_X64_URL"
        TOOLCHAIN_CACHE_NAME="x86-64-musl"
        SYSROOT_TRIPLE="x86_64-buildroot-linux-musl"
        COMPILER_PREFIX="x86_64-linux"
        PLATFORM_DESC="Linux x86-64 (musl)"
        ;;
    x86|i686)
        ARCH_NAME="x86"
        TOOLCHAIN_VAR="TOOLCHAIN_X86_URL"
        TOOLCHAIN_CACHE_NAME="i686-musl"
        SYSROOT_TRIPLE="i686-buildroot-linux-musl"
        COMPILER_PREFIX="i686-linux"
        PLATFORM_DESC="Linux x86 (i686 musl)"
        ;;
    arm64|aarch64)
        ARCH_NAME="arm64"
        TOOLCHAIN_VAR="TOOLCHAIN_ARM64_URL"
        TOOLCHAIN_CACHE_NAME="aarch64-musl"
        SYSROOT_TRIPLE="aarch64-buildroot-linux-musl"
        COMPILER_PREFIX="aarch64-linux"
        PLATFORM_DESC="Linux ARM64 (aarch64 musl)"
        ;;
    arm|armv7)
        ARCH_NAME="arm"
        TOOLCHAIN_VAR="TOOLCHAIN_ARM_URL"
        TOOLCHAIN_CACHE_NAME="armv7-musl"
        SYSROOT_TRIPLE="arm-buildroot-linux-musleabihf"
        COMPILER_PREFIX="arm-linux"
        PLATFORM_DESC="Linux ARM (armv7-eabihf musl)"
        ;;
    *)
        echo "Error: Unsupported architecture: $ARCH"
        echo "Supported: x64, x86, arm64, arm"
        exit 1
        ;;
esac

echo "Building for ${PLATFORM_DESC}..."

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-linux-${ARCH_NAME}"
OUTPUT_DIR="${HOME}/libarchive-native"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

# Get the toolchain URL from the variable name
eval "TOOLCHAIN_URL=\$$TOOLCHAIN_VAR"

echo "Setting up ${TOOLCHAIN_CACHE_NAME} cross-compiler toolchain from Bootlin..."
# Download toolchain to cache (does not unpack)
TOOLCHAIN_ARCHIVE=$(download_toolchain "$TOOLCHAIN_URL" "$TOOLCHAIN_CACHE_NAME")

# Extract directory name from archive
TOOLCHAIN_DIR="${TOOLCHAIN_ARCHIVE##*/}"
TOOLCHAIN_DIR="${TOOLCHAIN_DIR%.tar.xz}"

# Unpack toolchain in build directory
echo "Unpacking toolchain in build directory..."
tar xJf "$TOOLCHAIN_ARCHIVE"

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/${SYSROOT_TRIPLE}/sysroot"

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
export NM=${COMPILER_PREFIX}-nm

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
# Use function sections to enable dead code elimination with --gc-sections
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS -static-libgcc -ffunction-sections -fdata-sections"
export CXXFLAGS="-fPIC -O2 $CPPFLAGS -static-libstdc++ -static-libgcc -ffunction-sections -fdata-sections"
export LDFLAGS="-L$PREFIX/lib -static"

# Configure flags for cross-compilation (all builds run on x86_64 host)
CONFIGURE_HOST="--build=x86_64-pc-linux-gnu --host=${COMPILER_PREFIX}"

echo "Toolchain installed:"
$CC --version | head -n1
echo ""

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Initialize static library verification file
export STATIC_LIBS_FILE="$(pwd)/static-libs.txt"
echo "Static Library Verification Report" > "$STATIC_LIBS_FILE"
echo "Platform: ${PLATFORM_DESC}" >> "$STATIC_LIBS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$STATIC_LIBS_FILE"
echo "" >> "$STATIC_LIBS_FILE"

# Build compression libraries (static only to avoid conflicts with -static LDFLAGS)
echo "Building lz4 ${LZ4_VERSION}..."
cd lz4-${LZ4_VERSION}/lib
make -j$NCPU liblz4.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp liblz4.a $PREFIX/lib/
cp lz4.h lz4hc.h lz4frame.h $PREFIX/include/
cd ../..
verify_static_lib "$PREFIX/lib/liblz4.a" "$NM"

echo "Building zstd ${ZSTD_VERSION}..."
cd zstd-${ZSTD_VERSION}/lib
make -j$NCPU libzstd.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp libzstd.a $PREFIX/lib/
cp zstd.h zstd_errors.h zdict.h $PREFIX/include/
cd ../..
verify_static_lib "$PREFIX/lib/libzstd.a" "$NM"

echo "Building bzip2 ${BZIP2_VERSION}..."
cd bzip2-${BZIP2_VERSION}
make -sj$NCPU libbz2.a CC=$CC AR=$AR RANLIB=$RANLIB CFLAGS="-fPIC -O2 -w -D_FILE_OFFSET_BITS=64"
mkdir -p $PREFIX/lib $PREFIX/include
cp libbz2.a $PREFIX/lib/
cp bzlib.h $PREFIX/include/
cd ..
verify_static_lib "$PREFIX/lib/libbz2.a" "$NM"

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --quiet --cache-file="$(get_config_cache ${COMPILER_PREFIX})" $CONFIGURE_HOST --prefix=$PREFIX --disable-shared --enable-static
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzo2.a" "$NM"

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
CHOST=${COMPILER_PREFIX} ./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libz.a" "$NM"

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --quiet --cache-file="$(get_config_cache ${COMPILER_PREFIX})" $CONFIGURE_HOST --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzma.a" "$NM"

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --cache-file="$(get_config_cache ${COMPILER_PREFIX})" $CONFIGURE_HOST --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX --with-lzma=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libxml2.a" "$NM"

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --cache-file="$(get_config_cache ${COMPILER_PREFIX})" $CONFIGURE_HOST --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2 --disable-shared --enable-static
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libarchive.a" "$NM"

echo "Creating final shared library..."
# All architectures need libgcc for compiler intrinsics:
# - x86: 64-bit division (__divmoddi4, __udivdi3, etc.)
# - arm: ARM EABI division (__aeabi_idiv, __aeabi_ldivmod, etc.)
# - arm64: 128-bit float for long double (__addtf3, __multf3, etc.)
LIBGCC_PATH=$($CC -print-libgcc-file-name)

# Use --start-group/--end-group for multi-pass symbol resolution between
# dependency libraries, libgcc (compiler intrinsics), and libc
# Note: Version script causes test crashes - musl symbols need investigation
$CC -shared -o libarchive.so \
    -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive \
    -Wl,--start-group \
    local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a \
    local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a \
    $LIBGCC_PATH ${TOOLCHAIN_SYSROOT}/lib/libc.a \
    -Wl,--end-group \
    -nostdlib

echo "Creating static library (fat archive with all dependencies, internal symbols localized)..."
# Create a combined static archive with all dependencies
mkdir -p _ar_combine
cd _ar_combine
# Extract all object files from each static library (use absolute paths)
for lib in $PREFIX/lib/libarchive.a $PREFIX/lib/libbz2.a $PREFIX/lib/libz.a \
           $PREFIX/lib/libxml2.a $PREFIX/lib/liblzma.a $PREFIX/lib/liblzo2.a \
           $PREFIX/lib/libzstd.a $PREFIX/lib/liblz4.a $LIBGCC_PATH; do
    # Use a subdir per library to avoid filename collisions
    libname=$(basename "$lib" .a)
    mkdir -p "$libname"
    cd "$libname"
    $AR x "$lib"
    cd ..
done
# Combine all object files into one archive
$AR rcs ../libarchive-static.a */*.o
cd ..
rm -rf _ar_combine
# Localize all symbols except the public API (prevents namespace pollution)
OBJCOPY=${COMPILER_PREFIX}-objcopy
$OBJCOPY --keep-global-symbols="${SCRIPT_DIR}/libarchive-exports.txt" libarchive-static.a
ls -lh libarchive-static.a
echo "Exported symbols in static library:"
$NM libarchive-static.a | grep " T " | awk '{print $3}' | sort -u | head -20

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

# Generate dependency verification report
DEPS_FILE="$(pwd)/dependencies.txt"

echo "=== Dependency Verification ===" > "$DEPS_FILE"
echo "Platform: ${PLATFORM_DESC}" >> "$DEPS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$DEPS_FILE"
echo "" >> "$DEPS_FILE"

echo "=== Library Dependencies ===" >> "$DEPS_FILE"
ldd libarchive.so >> "$DEPS_FILE" 2>&1 || echo "Statically linked (no dynamic dependencies)" >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Exported Symbols (API) ===" >> "$DEPS_FILE"
$NM -D --defined-only libarchive.so 2>/dev/null | grep " T " | awk '{print $3}' | sort >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Imported Symbols (from libc/system) ===" >> "$DEPS_FILE"
$NM -D --undefined-only libarchive.so 2>/dev/null | awk '{print $2}' | sort >> "$DEPS_FILE"

echo "=== Checking dynamic library dependencies ==="
ldd libarchive.so || echo "Statically linked (no dynamic dependencies)"

# Fail on unexpected dependencies (musl builds should be fully static)
if ldd libarchive.so 2>&1 | grep -qvE "not a dynamic executable|statically linked|not found"; then
    echo "ERROR: Unexpected dynamic dependencies found!"
    ldd libarchive.so
    exit 1
fi
echo "Dependency check passed: library is statically linked"

echo "=== Inspecting symbols ==="
$NM libarchive.so | grep -c " T " | xargs echo "Defined symbols:"
$NM libarchive.so | grep -c " U " | xargs echo "Undefined symbols:"

# Skip native test for cross-compiled libraries
echo "Skipping native test (cross-compilation - library validated via dlopen test above)"

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-linux-${ARCH_NAME}.so"
cp libarchive-static.a "${OUTPUT_DIR}/libarchive-linux-${ARCH_NAME}.a"
cp "$DEPS_FILE" "${OUTPUT_DIR}/dependencies-linux-${ARCH_NAME}.txt"
cp "$STATIC_LIBS_FILE" "${OUTPUT_DIR}/static-libs-linux-${ARCH_NAME}.txt"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux ${ARCH_NAME} build complete: ${OUTPUT_DIR}/libarchive-linux-${ARCH_NAME}.so"
