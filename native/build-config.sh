#!/bin/sh
# Shared build configuration for libarchive native library builds
# Source this file in platform-specific build scripts

# Library versions
LIBARCHIVE_VERSION="3.7.3"
LZ4_VERSION="1.9.4"
ZSTD_VERSION="1.5.6"
LZO_VERSION="2.10"
LIBXML2_VERSION="2.12.6"
ZLIB_VERSION="1.3.1"
XZ_VERSION="5.4.6"
BZIP2_VERSION="1.0.8"

# musl toolchain versions (Linux only - for reference, actual versions from Bootlin prebuilt toolchain)
# Bootlin stable 2025.08-1: GCC 14.3.0, musl latest, binutils 2.43.1
MUSL_VERSION="1.2.5"
GCC_VERSION="9.4.0"
BINUTILS_VERSION="2.44"

# Download URLs
LIBARCHIVE_URL="https://github.com/libarchive/libarchive/releases/download/v${LIBARCHIVE_VERSION}/libarchive-${LIBARCHIVE_VERSION}.tar.xz"
LZ4_URL="https://github.com/lz4/lz4/archive/refs/tags/v${LZ4_VERSION}.tar.gz"
ZSTD_URL="https://github.com/facebook/zstd/releases/download/v${ZSTD_VERSION}/zstd-${ZSTD_VERSION}.tar.gz"
LZO_URL="https://www.oberhumer.com/opensource/lzo/download/lzo-${LZO_VERSION}.tar.gz"
LIBXML2_URL="https://download.gnome.org/sources/libxml2/2.12/libxml2-${LIBXML2_VERSION}.tar.xz"
BZIP2_URL="https://www.sourceware.org/pub/bzip2/bzip2-${BZIP2_VERSION}.tar.gz"
ZLIB_URL="https://zlib.net/zlib-${ZLIB_VERSION}.tar.xz"
XZ_URL="https://github.com/tukaani-project/xz/releases/download/v${XZ_VERSION}/xz-${XZ_VERSION}.tar.xz"

# Common build settings
export PREFIX="${PREFIX:-$(pwd)/local}"

# Download cache directory (persistent across builds)
export DOWNLOAD_CACHE="${HOME}/downloads"

# Function to download and extract a library
# Downloads to cache if not present, then unpacks fresh copy
download_library() {
    local url="$1"
    local name="$2"
    local dir_name="$3"

    # Extract archive filename from URL
    local archive_name="${url##*/}"
    local cache_file="${DOWNLOAD_CACHE}/${archive_name}"

    # Create cache directory if it doesn't exist
    mkdir -p "$DOWNLOAD_CACHE"

    # Download to cache if not already present
    if [ ! -f "$cache_file" ]; then
        echo "Downloading ${name} to cache..."
        curl -sL "$url" -o "$cache_file"
    else
        echo "Using cached ${name}..."
    fi

    # Delete any existing unpacked directory to ensure clean start
    rm -rf "$dir_name"

    # Unpack from cache
    echo "Unpacking ${name}..."
    if [ "${url##*.}" = "xz" ]; then
        tar xJf "$cache_file"
    else
        tar xzf "$cache_file"
    fi
}

# Function to download all libraries
# Always unpacks fresh copies from cache
download_all_libraries() {
    download_library "$LIBARCHIVE_URL" "libarchive" "libarchive-${LIBARCHIVE_VERSION}"
    download_library "$LZ4_URL" "lz4" "lz4-${LZ4_VERSION}"
    download_library "$ZSTD_URL" "zstd" "zstd-${ZSTD_VERSION}"
    download_library "$LZO_URL" "lzo" "lzo-${LZO_VERSION}"
    download_library "$LIBXML2_URL" "libxml2" "libxml2-${LIBXML2_VERSION}"
    download_library "$BZIP2_URL" "bzip2" "bzip2-${BZIP2_VERSION}"
    download_library "$ZLIB_URL" "zlib" "zlib-${ZLIB_VERSION}"
    download_library "$XZ_URL" "xz" "xz-${XZ_VERSION}"
}

# Detect number of CPU cores
if command -v nproc >/dev/null 2>&1; then
    export NCPU=$(nproc)
elif command -v sysctl >/dev/null 2>&1; then
    export NCPU=$(sysctl -n hw.ncpu)
else
    export NCPU=4
fi

echo "Build configuration loaded:"
echo "  libarchive: ${LIBARCHIVE_VERSION}"
echo "  lz4: ${LZ4_VERSION}"
echo "  zstd: ${ZSTD_VERSION}"
echo "  lzo: ${LZO_VERSION}"
echo "  libxml2: ${LIBXML2_VERSION}"
echo "  zlib: ${ZLIB_VERSION}"
echo "  xz: ${XZ_VERSION}"
echo "  bzip2: ${BZIP2_VERSION}"
echo "  musl: ${MUSL_VERSION:-N/A}"
echo "  gcc: ${GCC_VERSION:-N/A}"
echo "  binutils: ${BINUTILS_VERSION:-N/A}"
echo "  CPUs: ${NCPU}"
echo "  PREFIX: ${PREFIX}"
