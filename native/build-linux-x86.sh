#!/bin/bash
# Build libarchive for Linux x86 (32-bit i686) using musl-libc for static linking

set -e

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-linux-x86"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

echo "Setting up i686 musl cross-compiler toolchain from Bootlin..."
# Download toolchain to cache (does not unpack)
TOOLCHAIN_ARCHIVE=$(download_toolchain "$TOOLCHAIN_X86_URL" "i686-musl")

# Extract directory name from archive
TOOLCHAIN_DIR="${TOOLCHAIN_ARCHIVE##*/}"
TOOLCHAIN_DIR="${TOOLCHAIN_DIR%.tar.xz}"

# Unpack toolchain in build directory
echo "Unpacking toolchain in build directory..."
tar xJf "$TOOLCHAIN_ARCHIVE"

# Set up toolchain paths
export TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/i686-buildroot-linux-musl/sysroot"

# Verify toolchain was unpacked correctly
if [ ! -f "$TOOLCHAIN_PREFIX/bin/i686-linux-gcc" ]; then
    echo "ERROR: Toolchain compiler not found at $TOOLCHAIN_PREFIX/bin/i686-linux-gcc"
    echo "Directory contents:"
    ls -la "$TOOLCHAIN_PREFIX" 2>/dev/null || echo "  Directory does not exist"
    exit 1
fi

export CC=i686-linux-gcc
export CXX=i686-linux-g++
export AR=i686-linux-ar
export RANLIB=i686-linux-ranlib

# Generate sccache wrappers for this toolchain in build directory
echo "Setting up sccache wrappers..."
mkdir -p .ccache-bin
for tool in gcc g++ ar ranlib; do
    cat > .ccache-bin/i686-linux-$tool <<EOF
#!/bin/sh
exec sccache "$TOOLCHAIN_PREFIX/bin/i686-linux-\$tool" "\$@"
EOF
    chmod +x .ccache-bin/i686-linux-$tool
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
./configure --build=x86_64-pc-linux-gnu --host=i686-linux --prefix=$PREFIX --disable-shared --enable-static
make -sj$NCPU install
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
CHOST=i686-linux ./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --build=x86_64-pc-linux-gnu --host=i686-linux --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --build=x86_64-pc-linux-gnu --host=i686-linux --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-${ZLIB_VERSION} --with-lzma=$PREFIX/../xz-${XZ_VERSION}
make -sj$NCPU install
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --build=x86_64-pc-linux-gnu --host=i686-linux --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2 --disable-shared --enable-static
make -sj$NCPU install
cd ..

echo "Creating final shared library..."
# For 32-bit builds, we need libgcc for 64-bit division intrinsics (__udivdi3, etc.)
LIBGCC_PATH=$($CC -print-libgcc-file-name)
$CC -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a "$LIBGCC_PATH" ${TOOLCHAIN_SYSROOT}/lib/libc.a -nostdlib

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
${AR/ar/nm} -D libarchive.so | grep -E "(__udivdi3|__umoddi3|__divdi3|__moddi3)" || echo "No 64-bit division intrinsics found in exports"
${AR/ar/nm} libarchive.so | grep -c " T " | xargs echo "Defined symbols:"
${AR/ar/nm} libarchive.so | grep -c " U " | xargs echo "Undefined symbols:"

echo "Skipping native test (cross-compilation - cannot run 32-bit i386 binary on x86_64 host)"

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-linux-x86.so"

echo "Cleaning up build directory (including toolchain, wrappers, and build artifacts)..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux x86 build complete: ${OUTPUT_DIR}/libarchive-linux-x86.so"
