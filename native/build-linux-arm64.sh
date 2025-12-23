#!/bin/bash
# Build libarchive for Linux ARM64 (aarch64) using musl-libc for static linking

set -e

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-linux-arm64"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

echo "Setting up aarch64 musl cross-compiler toolchain from Bootlin..."
# Download toolchain to cache (does not unpack)
TOOLCHAIN_ARCHIVE=$(download_toolchain "$TOOLCHAIN_ARM64_URL" "aarch64-musl")

# Extract directory name from archive
TOOLCHAIN_DIR="${TOOLCHAIN_ARCHIVE##*/}"
TOOLCHAIN_DIR="${TOOLCHAIN_DIR%.tar.xz}"

# Unpack toolchain in build directory
echo "Unpacking toolchain in build directory..."
tar xJf "$TOOLCHAIN_ARCHIVE"

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/aarch64-buildroot-linux-musl/sysroot"

# Verify toolchain was unpacked correctly
if [ ! -f "$TOOLCHAIN_PREFIX/bin/aarch64-linux-gcc" ]; then
    echo "ERROR: Toolchain compiler not found at $TOOLCHAIN_PREFIX/bin/aarch64-linux-gcc"
    echo "Directory contents:"
    ls -la "$TOOLCHAIN_PREFIX" 2>/dev/null || echo "  Directory does not exist"
    exit 1
fi

export CC=aarch64-linux-gcc
export CXX=aarch64-linux-g++
export AR=aarch64-linux-ar
export RANLIB=aarch64-linux-ranlib

# Generate sccache wrappers for compilers only (not ar/ranlib)
echo "Setting up sccache wrappers..."
mkdir -p .ccache-bin
for tool in gcc g++; do
    printf '#!/bin/sh\nexec sccache "%s/bin/aarch64-linux-%s" "$@"\n' "$TOOLCHAIN_PREFIX" "$tool" > .ccache-bin/aarch64-linux-$tool
    chmod +x .ccache-bin/aarch64-linux-$tool
done

# Add wrappers to PATH (before toolchain bin)
export PATH="$(pwd)/.ccache-bin:$TOOLCHAIN_PREFIX/bin:$PATH"

# Keep PREFIX for our built libraries (same as before)
export PREFIX="${PREFIX:-$(pwd)/local}"

# Set compiler flags for static linking
# Use hidden visibility and function sections to enable dead code elimination
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS -static-libgcc -fvisibility=hidden -ffunction-sections -fdata-sections"
export CXXFLAGS="-fPIC -O2 $CPPFLAGS -static-libstdc++ -static-libgcc -fvisibility=hidden -ffunction-sections -fdata-sections"
export LDFLAGS="-L$PREFIX/lib -static"

echo "Toolchain installed:"
$CC --version | head -n1
echo ""

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Initialize static library verification file
export STATIC_LIBS_FILE="$(pwd)/static-libs.txt"
echo "Static Library Verification Report" > "$STATIC_LIBS_FILE"
echo "Platform: Linux ARM64 (aarch64 musl)" >> "$STATIC_LIBS_FILE"
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
verify_static_lib "$PREFIX/lib/liblz4.a" "${AR/%ar/nm}"

echo "Building zstd ${ZSTD_VERSION}..."
cd zstd-${ZSTD_VERSION}/lib
make -j$NCPU libzstd.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp libzstd.a $PREFIX/lib/
cp zstd.h zstd_errors.h zdict.h $PREFIX/include/
cd ../..
verify_static_lib "$PREFIX/lib/libzstd.a" "${AR/%ar/nm}"

echo "Building bzip2 ${BZIP2_VERSION}..."
cd bzip2-${BZIP2_VERSION}
make -sj$NCPU libbz2.a CC=$CC AR=$AR RANLIB=$RANLIB CFLAGS="-fPIC -O2 -w -D_FILE_OFFSET_BITS=64"
mkdir -p $PREFIX/lib $PREFIX/include
cp libbz2.a $PREFIX/lib/
cp bzlib.h $PREFIX/include/
cd ..
verify_static_lib "$PREFIX/lib/libbz2.a" "${AR/%ar/nm}"

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --quiet --cache-file="$(get_config_cache aarch64-linux)" --build=x86_64-pc-linux-gnu --host=aarch64-linux --prefix=$PREFIX --disable-shared --enable-static
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzo2.a" "${AR/%ar/nm}"

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
CHOST=aarch64-linux ./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libz.a" "${AR/%ar/nm}"

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --quiet --cache-file="$(get_config_cache aarch64-linux)" --build=x86_64-pc-linux-gnu --host=aarch64-linux --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzma.a" "${AR/%ar/nm}"

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --cache-file="$(get_config_cache aarch64-linux)" --build=x86_64-pc-linux-gnu --host=aarch64-linux --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX --with-lzma=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libxml2.a" "${AR/%ar/nm}"

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --cache-file="$(get_config_cache aarch64-linux)" --build=x86_64-pc-linux-gnu --host=aarch64-linux --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2 --disable-shared --enable-static
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libarchive.a" "${AR/%ar/nm}"

echo "Creating final shared library..."
$CC -shared -o libarchive.so \
    -Wl,--version-script="${SCRIPT_DIR}/libarchive.map" \
    -Wl,--gc-sections \
    -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive \
    local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a \
    local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a \
    ${TOOLCHAIN_SYSROOT}/lib/libc.a -nostdlib

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
NM_CMD="${AR/%ar/nm}"

echo "=== Dependency Verification ===" > "$DEPS_FILE"
echo "Platform: Linux ARM64 (aarch64 musl)" >> "$DEPS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$DEPS_FILE"
echo "" >> "$DEPS_FILE"

echo "=== Library Dependencies ===" >> "$DEPS_FILE"
ldd libarchive.so >> "$DEPS_FILE" 2>&1 || echo "Statically linked (no dynamic dependencies)" >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Exported Symbols (API) ===" >> "$DEPS_FILE"
$NM_CMD -D --defined-only libarchive.so 2>/dev/null | grep " T " | awk '{print $3}' | sort >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Imported Symbols (from libc/system) ===" >> "$DEPS_FILE"
$NM_CMD -D --undefined-only libarchive.so 2>/dev/null | awk '{print $2}' | sort >> "$DEPS_FILE"

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
$NM_CMD libarchive.so | grep -c " T " | xargs echo "Defined symbols:"
$NM_CMD libarchive.so | grep -c " U " | xargs echo "Undefined symbols:"

echo "Skipping native test (cross-compilation - cannot run ARM64 binary on x86_64 host)"

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-linux-arm64.so"
cp "$DEPS_FILE" "${OUTPUT_DIR}/dependencies-linux-arm64.txt"
cp "$STATIC_LIBS_FILE" "${OUTPUT_DIR}/static-libs-linux-arm64.txt"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux ARM64 build complete: ${OUTPUT_DIR}/libarchive-linux-arm64.so"
