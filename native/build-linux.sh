#!/bin/bash
# Build libarchive for Linux x86-64 using musl-libc for static linking

set -e

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-linux-x64"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

# Load the helpers shared by the Linux builds (mbedTLS build, final link, checks, load tests)
# shellcheck source-path=SCRIPTDIR
. "${SCRIPT_DIR}/build-linux-common.sh"

echo "Setting up x86-64 musl cross-compiler toolchain from Bootlin..."
# Download toolchain to cache (does not unpack)
TOOLCHAIN_ARCHIVE=$(download_toolchain "$TOOLCHAIN_X64_URL" "x86-64-musl")

# Extract directory name from archive
TOOLCHAIN_DIR="${TOOLCHAIN_ARCHIVE##*/}"
TOOLCHAIN_DIR="${TOOLCHAIN_DIR%.tar.xz}"

# Unpack toolchain in build directory
echo "Unpacking toolchain in build directory..."
tar xJf "$TOOLCHAIN_ARCHIVE"

# Set up toolchain paths
TOOLCHAIN_PREFIX="$(pwd)/${TOOLCHAIN_DIR}"
export TOOLCHAIN_PREFIX
export TOOLCHAIN_SYSROOT="$TOOLCHAIN_PREFIX/x86_64-buildroot-linux-musl/sysroot"

# Verify toolchain was unpacked correctly
if [ ! -f "$TOOLCHAIN_PREFIX/bin/x86_64-linux-gcc" ]; then
    echo "ERROR: Toolchain compiler not found at $TOOLCHAIN_PREFIX/bin/x86_64-linux-gcc"
    echo "Directory contents:"
    ls -la "$TOOLCHAIN_PREFIX" 2>/dev/null || echo "  Directory does not exist"
    exit 1
fi

export CC="sccache $TOOLCHAIN_PREFIX/bin/x86_64-linux-gcc"
export CXX="sccache $TOOLCHAIN_PREFIX/bin/x86_64-linux-g++"
export AR=$TOOLCHAIN_PREFIX/bin/x86_64-linux-ar
export RANLIB=$TOOLCHAIN_PREFIX/bin/x86_64-linux-ranlib

export PATH="$TOOLCHAIN_PREFIX/bin:$PATH"

# Keep PREFIX for our built libraries (same as before)
export PREFIX="${PREFIX:-$(pwd)/local}"

# Set compiler flags for static linking
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS -static-libgcc"
export CXXFLAGS="-fPIC -O2 $CPPFLAGS -static-libstdc++ -static-libgcc"
export LDFLAGS="-L$PREFIX/lib -static"
# pkg-config and the manifest tool for configure (see build-config.sh)
export_cross_build_tools

echo "Toolchain installed:"
$CC --version | head -n1
echo ""

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Every narrow string UTF-8, whatever the locale (see "The libarchive patch" in BUILD.md)
patch_libarchive "${SCRIPT_DIR}" "libarchive-${LIBARCHIVE_VERSION}"

# Build compression libraries (static only to avoid conflicts with -static LDFLAGS)
echo "Building lz4 ${LZ4_VERSION}..."
cd "lz4-${LZ4_VERSION}/lib"
make -j"$NCPU" liblz4.a "CC=$CC" "AR=$AR"
mkdir -p "$PREFIX/lib" "$PREFIX/include"
cp liblz4.a "$PREFIX/lib/"
cp lz4.h lz4hc.h lz4frame.h "$PREFIX/include/"
cd ../..

echo "Building zstd ${ZSTD_VERSION}..."
cd "zstd-${ZSTD_VERSION}/lib"
make -j"$NCPU" libzstd.a "CC=$CC" "AR=$AR"
mkdir -p "$PREFIX/lib" "$PREFIX/include"
cp libzstd.a "$PREFIX/lib/"
cp zstd.h zstd_errors.h zdict.h "$PREFIX/include/"
cd ../..

echo "Building bzip2 ${BZIP2_VERSION}..."
cd "bzip2-${BZIP2_VERSION}"
make -j"$NCPU" libbz2.a "CC=$CC" "AR=$AR" "RANLIB=$RANLIB" CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64"
mkdir -p "$PREFIX/lib" "$PREFIX/include"
cp libbz2.a "$PREFIX/lib/"
cp bzlib.h "$PREFIX/include/"
cd ..

# lzop (LZO1X) support: lzokay behind the liblzo2 API, in place of the GPL-licensed liblzo2
# shellcheck source-path=SCRIPTDIR disable=SC1091
. "${SCRIPT_DIR}/build-lzo-shim.sh"
build_lzo_shim "${SCRIPT_DIR}/lzo-shim" "lzokay-${LZOKAY_COMMIT}" "$CXXFLAGS"

echo "Building zlib ${ZLIB_VERSION}..."
cd "zlib-${ZLIB_VERSION}"
./configure --static --prefix="$PREFIX"
make -sj"$NCPU" install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd "xz-${XZ_VERSION}"
./configure --cache-file="$(get_config_cache x86_64-linux)" --with-pic --disable-shared --prefix="$PREFIX"
make -sj"$NCPU" install
cd ..

# Download, build and install the static mbedTLS crypto library (see build-linux-common.sh)
build_mbedtls

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd "libarchive-${LIBARCHIVE_VERSION}"
# --with-mbedtls --without-openssl: use the static mbedTLS built above for ZIP AES.
# --without-xml2 --without-expat: keep XAR disabled, as it was while no MD5/SHA-1
# implementation was available. With digests present it would otherwise be enabled
# and need an XML library resolved in the final link. So no XML library is built
# for Linux: download_all_libraries unpacks libxml2, but only macOS and Windows use it.
./configure --cache-file="$(get_config_cache x86_64-linux)" --prefix="$PREFIX" --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --enable-posix-regex-lib=libc --with-pic --with-lzo2 --disable-shared --enable-static --with-mbedtls --without-openssl --without-xml2 --without-expat
make -sj"$NCPU" install
cd ..

echo "Creating final shared library..."
# libarchive.a, the libraries it uses, libgcc and musl's libc.a, failing on any
# unresolved symbol (see build-linux-common.sh)
link_libarchive libarchive.so

# Fail the build unless the mbedTLS crypto backend was linked in (see build-linux-common.sh)
verify_crypto_backend libarchive.so

# Fail the build unless the library needs nothing at run time and loads, both in a
# process that contains only musl and in one built with the host's gcc and C library
# (see build-linux-common.sh)
verify_self_contained libarchive.so
test_load_musl libarchive.so
test_load_host libarchive.so
file libarchive.so

# Fail the build unless lzop support was linked in and needs nothing the library lacks (see build-lzo-shim.sh)
verify_lzo_support libarchive.so
verify_lzo_shim_resolved libarchive.so

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-linux-x64.so"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux x64 build complete: ${OUTPUT_DIR}/libarchive-linux-x64.so"
