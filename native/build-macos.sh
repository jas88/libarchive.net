#!/bin/bash
# Build libarchive for macOS as universal binary (x86_64 + arm64)

set -e

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-macos"
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

# Create symlinks for automake-1.17 (xz 5.8+ was built with this version)
# Homebrew installs automake 1.18+ but xz's build system looks for version-specific commands
BREW_PREFIX="$(brew --prefix)"
ln -sf "${BREW_PREFIX}/bin/automake" "${BREW_PREFIX}/bin/automake-1.17"
ln -sf "${BREW_PREFIX}/bin/aclocal" "${BREW_PREFIX}/bin/aclocal-1.17"

# macOS-specific build settings
export CPPFLAGS="-I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib -liconv"
export CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64 -arch arm64 -arch x86_64"

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Initialize static library verification file
export STATIC_LIBS_FILE="$(pwd)/static-libs.txt"
echo "Static Library Verification Report" > "$STATIC_LIBS_FILE"
echo "Platform: macOS universal (x86_64 + arm64)" >> "$STATIC_LIBS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$STATIC_LIBS_FILE"
echo "" >> "$STATIC_LIBS_FILE"

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
make -j$NCPU -sC lz4-${LZ4_VERSION} install PREFIX=$PREFIX CFLAGS="$CFLAGS"
verify_static_lib "$PREFIX/lib/liblz4.a"

echo "Building bzip2 ${BZIP2_VERSION}..."
make -j$NCPU -sC bzip2-${BZIP2_VERSION} install PREFIX=$PREFIX CFLAGS="$CFLAGS"
verify_static_lib "$PREFIX/lib/libbz2.a"

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --cache-file=$(get_config_cache darwin-universal) --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzo2.a"

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libz.a"

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
# Regenerate autotools files for local automake version
aclocal && automake && autoconf
./configure --cache-file=$(get_config_cache darwin-universal) --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzma.a"

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
./autogen.sh --cache-file=$(get_config_cache darwin-universal) --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX --with-lzma=$PREFIX
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libxml2.a"

echo "Building zstd ${ZSTD_VERSION}..."
make -j$NCPU -sC zstd-${ZSTD_VERSION} install
verify_static_lib "$PREFIX/lib/libzstd.a"

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
# Regenerate autotools files for local automake version
aclocal && automake && autoconf
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS="-L$PREFIX -lxml2"
./configure --cache-file=$(get_config_cache darwin-universal) --prefix=$PREFIX --enable-silent-rules --disable-dependency-tracking --enable-static --disable-shared --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-rpath --enable-posix-regex-lib=libc --enable-xattr --enable-acl --enable-largefile --with-pic --with-zlib --with-bz2lib --with-libb2 --with-iconv --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-cng
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libarchive.a"

echo "Creating universal binary..."
clang -arch arm64 -arch x86_64 -dynamiclib -shared -o libarchive.dylib -Wl,-force_load local/lib/libarchive.a local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a -liconv

echo "Testing library..."
file libarchive.dylib
otool -L libarchive.dylib

# Generate dependency verification report
DEPS_FILE="$(pwd)/dependencies.txt"

echo "=== Dependency Verification ===" > "$DEPS_FILE"
echo "Platform: macOS universal (x86_64 + arm64)" >> "$DEPS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$DEPS_FILE"
echo "" >> "$DEPS_FILE"

echo "=== Library Dependencies ===" >> "$DEPS_FILE"
otool -L libarchive.dylib >> "$DEPS_FILE"
echo "" >> "$DEPS_FILE"
file libarchive.dylib >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Exported Symbols (API) ===" >> "$DEPS_FILE"
nm -gU libarchive.dylib | grep " T " | awk '{print $3}' | sort >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Imported Symbols (from system libs) ===" >> "$DEPS_FILE"
nm -gu libarchive.dylib | awk '{print $2}' | sort >> "$DEPS_FILE"

# Fail on unexpected dependencies (only system libs allowed)
echo "=== Checking for unexpected dependencies ==="
DEPS=$(otool -L libarchive.dylib | awk '{print $1}')
for dep in $DEPS; do
    case "$dep" in
        libarchive.dylib) ;;  # library's own install name (appears per architecture in universal binary)
        libarchive.dylib*) ;;  # architecture headers like "libarchive.dylib (architecture x86_64):"
        /usr/lib/libSystem.B.dylib) ;;
        /usr/lib/libiconv.*.dylib) ;;
        /usr/lib/libresolv.*.dylib) ;;
        *)
            echo "ERROR: Unexpected dependency: $dep"
            exit 1
            ;;
    esac
done
echo "Dependency check passed: only system libraries linked"

echo "Building native test..."
gcc -o nativetest "${SCRIPT_DIR}/nativetest.c" local/lib/libarchive.a -Llocal/lib -Ilocal/include -llz4 -lzstd -llzma -lz -liconv -lbz2
./nativetest

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.dylib "${OUTPUT_DIR}/libarchive.dylib"
cp "$DEPS_FILE" "${OUTPUT_DIR}/dependencies-macos.txt"
cp "$STATIC_LIBS_FILE" "${OUTPUT_DIR}/static-libs-macos.txt"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "macOS build complete: ${OUTPUT_DIR}/libarchive.dylib"
