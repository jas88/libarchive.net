#!/bin/bash
# Shared build configuration for libarchive native library builds
# Source this file in platform-specific build scripts

# Library versions
LIBARCHIVE_VERSION="3.8.4"
LZ4_VERSION="1.10.0"
ZSTD_VERSION="1.5.7"
LZO_VERSION="2.10"
LIBXML2_VERSION="2.15.1"
ZLIB_VERSION="1.3.1"
XZ_VERSION="5.8.2"
BZIP2_VERSION="1.0.8"

# Bootlin toolchain versions (Linux only)
# Bootlin stable 2025.08-1: GCC 14.3.0, musl latest, binutils 2.43.1
BOOTLIN_RELEASE="stable-2025.08-1"
MUSL_VERSION="1.2.5"
GCC_VERSION="9.4.0"
BINUTILS_VERSION="2.44"

# Bootlin toolchain URLs (exported for use in build scripts)
TOOLCHAIN_BASE_URL="https://toolchains.bootlin.com/downloads/releases/toolchains"
export TOOLCHAIN_X86_URL="${TOOLCHAIN_BASE_URL}/x86-i686/tarballs/x86-i686--musl--${BOOTLIN_RELEASE}.tar.xz"
export TOOLCHAIN_X64_URL="${TOOLCHAIN_BASE_URL}/x86-64/tarballs/x86-64--musl--${BOOTLIN_RELEASE}.tar.xz"
export TOOLCHAIN_ARM_URL="${TOOLCHAIN_BASE_URL}/armv7-eabihf/tarballs/armv7-eabihf--musl--${BOOTLIN_RELEASE}.tar.xz"
export TOOLCHAIN_ARM64_URL="${TOOLCHAIN_BASE_URL}/aarch64/tarballs/aarch64--musl--${BOOTLIN_RELEASE}.tar.xz"

# Library download URLs
LIBARCHIVE_URL="https://github.com/libarchive/libarchive/releases/download/v${LIBARCHIVE_VERSION}/libarchive-${LIBARCHIVE_VERSION}.tar.xz"
LZ4_URL="https://github.com/lz4/lz4/archive/refs/tags/v${LZ4_VERSION}.tar.gz"
ZSTD_URL="https://github.com/facebook/zstd/releases/download/v${ZSTD_VERSION}/zstd-${ZSTD_VERSION}.tar.gz"
LZO_URL="https://www.oberhumer.com/opensource/lzo/download/lzo-${LZO_VERSION}.tar.gz"
# libxml2 URL uses major.minor as directory (e.g., 2.15.1 -> 2.15)
LIBXML2_MAJOR_MINOR="${LIBXML2_VERSION%.*}"
LIBXML2_URL="https://download.gnome.org/sources/libxml2/${LIBXML2_MAJOR_MINOR}/libxml2-${LIBXML2_VERSION}.tar.xz"
BZIP2_URL="https://www.sourceware.org/pub/bzip2/bzip2-${BZIP2_VERSION}.tar.gz"
ZLIB_URL="https://zlib.net/zlib-${ZLIB_VERSION}.tar.xz"
XZ_URL="https://github.com/tukaani-project/xz/releases/download/v${XZ_VERSION}/xz-${XZ_VERSION}.tar.xz"

# Common build settings
export PREFIX="${PREFIX:-$(pwd)/local}"

# Download cache directory (persistent across builds)
export DOWNLOAD_CACHE="${HOME}/downloads"

# Configure cache directory (persistent across builds, shared per host triplet)
export CONFIG_CACHE_DIR="${HOME}/config-cache"

# Get the config.cache file path for a given host triplet
# Usage: get_config_cache [host-triplet]
# If no host is specified, uses "native"
get_config_cache() {
    local host="${1:-native}"
    mkdir -p "$CONFIG_CACHE_DIR"
    echo "$CONFIG_CACHE_DIR/config.cache.${host}"
}

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
        # Retry up to 3 times with exponential backoff for transient network issues
        local max_retries=3
        local retry=0
        local downloaded=false

        while [ $retry -lt $max_retries ] && [ "$downloaded" = "false" ]; do
            if curl -fsSL "$url" -o "$cache_file"; then
                echo "Download successful"
                downloaded=true
            else
                retry=$((retry + 1))
                if [ $retry -lt $max_retries ]; then
                    echo "Download failed, retrying ($retry/$max_retries)..."
                    sleep $((retry * 2))
                fi
            fi
        done

        if [ "$downloaded" = "false" ]; then
            echo "ERROR: Failed to download ${name} after $max_retries attempts from primary source"
            echo "Please check network connectivity or try again later"
            echo "URL: $url"
            return 1
        fi
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

# Function to download toolchain to cache (does not unpack)
# Returns the cache file path for the build script to unpack
download_toolchain() {
    local url="$1"
    local name="$2"

    # Extract archive filename from URL
    local archive_name="${url##*/}"
    local cache_file="${DOWNLOAD_CACHE}/${archive_name}"

    # Create cache directory if it doesn't exist
    mkdir -p "$DOWNLOAD_CACHE"

    # Download to cache if not already present
    if [ ! -f "$cache_file" ]; then
        echo "Downloading ${name} toolchain to cache..." >&2
        local max_retries=3
        local retry=0
        local downloaded=false

        while [ $retry -lt $max_retries ] && [ "$downloaded" = "false" ]; do
            if curl -fsSL "$url" -o "$cache_file"; then
                echo "Toolchain download successful" >&2
                downloaded=true
            else
                retry=$((retry + 1))
                if [ $retry -lt $max_retries ]; then
                    echo "Toolchain download failed, retrying ($retry/$max_retries)..." >&2
                    sleep $((retry * 2))
                fi
            fi
        done

        if [ "$downloaded" = "false" ]; then
            echo "ERROR: Failed to download ${name} toolchain after $max_retries attempts" >&2
            echo "URL: $url" >&2
            return 1
        fi
    else
        echo "Using cached ${name} toolchain..." >&2
    fi

    # Return the cache file path (only thing written to stdout)
    echo "$cache_file"
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

    # Fix xz automake timestamp issue - touch generated files to prevent regeneration
    # xz 5.8+ requires automake 1.17 which may not be available on build systems
    if [ -d "xz-${XZ_VERSION}" ]; then
        echo "Fixing xz automake timestamps..."
        find "xz-${XZ_VERSION}" -name "configure" -exec touch {} \;
        find "xz-${XZ_VERSION}" -name "Makefile.in" -exec touch {} \;
        find "xz-${XZ_VERSION}" -name "aclocal.m4" -exec touch {} \;
        find "xz-${XZ_VERSION}" -name "config.h.in" -exec touch {} \;
    fi
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
