#!/bin/bash
# Build libarchive for macOS
# Supports multiple architectures via ARCH environment variable

set -e

# Detect architecture or use default (build both if not specified)
ARCH="${ARCH:-universal}"

# Architecture-specific configuration
case "$ARCH" in
    x86_64|x64)
        ARCH_NAME="x64"
        ARCH_FLAGS="-arch x86_64"
        BUILD_BOTH=false
        ;;
    arm64|aarch64)
        ARCH_NAME="arm64"
        ARCH_FLAGS="-arch arm64"
        BUILD_BOTH=false
        ;;
    universal|both)
        # Build both architectures
        BUILD_BOTH=true
        ;;
    *)
        echo "Error: Unsupported architecture: $ARCH"
        echo "Supported: x86_64/x64, arm64/aarch64, universal/both"
        exit 1
        ;;
esac

if [ "$BUILD_BOTH" = true ]; then
    echo "Building both x64 and arm64 architectures..."

    # Build x64
    ARCH=x64 "$0"

    # Build arm64
    ARCH=arm64 "$0"

    echo "Both architectures built successfully"
    echo "  x64:   ${HOME}/libarchive-native/libarchive-x64.dylib"
    echo "  arm64: ${HOME}/libarchive-native/libarchive-arm64.dylib"
    exit 0
fi

# Single architecture build
echo "Building for macOS ${ARCH_NAME}..."

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-macos-${ARCH_NAME}"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

# Ensure build tools are available
echo "Installing required build tools..."
brew install autoconf automake libtool 2>/dev/null || true

# macOS-specific build settings
export CPPFLAGS="-I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib -liconv"
export CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64 ${ARCH_FLAGS}"

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
./configure --prefix=$PREFIX
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
./autogen.sh --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX --with-lzma=$PREFIX
make -sj$NCPU install
cd ..

echo "Building zstd ${ZSTD_VERSION}..."
make -j$NCPU -sC zstd-${ZSTD_VERSION} install

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS="-L$PREFIX -lxml2"
./configure --prefix=$PREFIX --enable-silent-rules --disable-dependency-tracking --enable-static --disable-shared --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --disable-rpath --enable-posix-regex-lib=libc --enable-xattr --enable-acl --enable-largefile --with-pic --with-zlib --with-bz2lib --with-libb2 --with-iconv --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-cng
make -sj$NCPU install
cd ..

echo "Creating merged static library with all dependencies..."
mkdir -p local/lib/merge_tmp
cd local/lib/merge_tmp
ar x ../libarchive.a
ar x ../libbz2.a
ar x ../libz.a
ar x ../libxml2.a
ar x ../liblzma.a
ar x ../liblzo2.a
ar x ../libzstd.a
ar x ../liblz4.a
ar rcs ../libarchive.a *.o
cd ../../..
rm -rf local/lib/merge_tmp

echo "Creating ${ARCH_NAME} dylib..."
clang ${ARCH_FLAGS} -dynamiclib -shared -o libarchive-${ARCH_NAME}.dylib -Wl,-force_load local/lib/libarchive.a -liconv

echo "Testing library..."
file libarchive-${ARCH_NAME}.dylib
otool -L libarchive-${ARCH_NAME}.dylib

echo "Building native test..."
gcc ${ARCH_FLAGS} -o nativetest "${SCRIPT_DIR}/nativetest.c" local/lib/libarchive.a -Ilocal/include
./nativetest

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive-${ARCH_NAME}.dylib "${OUTPUT_DIR}/libarchive-${ARCH_NAME}.dylib"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "macOS ${ARCH_NAME} build complete: ${OUTPUT_DIR}/libarchive-${ARCH_NAME}.dylib"
