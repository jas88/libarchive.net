#!/bin/bash
# Shared build configuration for libarchive native library builds
# Source this file in platform-specific build scripts

# Library versions
LIBARCHIVE_VERSION="3.8.7"
LZ4_VERSION="1.10.0"
ZSTD_VERSION="1.5.7"
LZO_VERSION="2.10"
LIBXML2_VERSION="2.15.3"
ZLIB_VERSION="1.3.2"
XZ_VERSION="5.8.3"
BZIP2_VERSION="1.0.8"

# SHA256 checksums for download verification
LIBARCHIVE_SHA256="d3a8ba457ae25c27c84fd2830a2efdcc5b1d40bf585d4eb0d35f47e99e5d4774"
LZ4_SHA256="537512904744b35e232912055ccf8ec66d768639ff3abe5788d90d792ec5f48b"
ZSTD_SHA256="eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3"
LZO_SHA256="c0f892943208266f9b6543b3ae308fab6284c5c90e627931446fb49b4221a072"
LIBXML2_SHA256="78262a6e7ac170d6528ebfe2efccdf220191a5af6a6cd61ea4a9a9a5042c7a07"
ZLIB_SHA256="d7a0654783a4da529d1bb793b7ad9c3318020af77667bcae35f95d0e42a792f3"
XZ_SHA256="fff1ffcf2b0da84d308a14de513a1aa23d4e9aa3464d17e64b9714bfdd0bbfb6"
BZIP2_SHA256="ab5a03176ee106d3f0fa90e381da478ddae405918153cca248e682cd0c4a2269"

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

# Portable SHA256 helper (computes hash and compares directly;
# avoids --check flag which differs between GNU and BSD sha256sum)
sha256_compute() {
    local file="$1"
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$file" | cut -d' ' -f1
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | cut -d' ' -f1
    else
        echo "ERROR: No SHA256 tool found (need shasum or sha256sum)" >&2
        return 1
    fi
}

sha256_check() {
    local expected="$1"
    local file="$2"
    local actual
    actual=$(sha256_compute "$file") || return 1
    [ "$expected" = "$actual" ]
}

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
    local expected_sha256="${4:-}"

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

    # Verify SHA256 checksum if provided
    if [ -n "$expected_sha256" ]; then
        echo "Verifying ${name} checksum..."
        if ! sha256_check "$expected_sha256" "$cache_file"; then
            local actual
            actual=$(sha256_compute "$cache_file") || actual="(unable to compute)"
            echo "ERROR: SHA256 checksum mismatch for ${name}"
            echo "Expected: $expected_sha256"
            echo "Got:      $actual"
            rm -f "$cache_file"
            return 1
        fi
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
    download_library "$LIBARCHIVE_URL" "libarchive" "libarchive-${LIBARCHIVE_VERSION}" "$LIBARCHIVE_SHA256"
    download_library "$LZ4_URL" "lz4" "lz4-${LZ4_VERSION}" "$LZ4_SHA256"
    download_library "$ZSTD_URL" "zstd" "zstd-${ZSTD_VERSION}" "$ZSTD_SHA256"
    download_library "$LZO_URL" "lzo" "lzo-${LZO_VERSION}" "$LZO_SHA256"
    download_library "$LIBXML2_URL" "libxml2" "libxml2-${LIBXML2_VERSION}" "$LIBXML2_SHA256"
    download_library "$BZIP2_URL" "bzip2" "bzip2-${BZIP2_VERSION}" "$BZIP2_SHA256"
    download_library "$ZLIB_URL" "zlib" "zlib-${ZLIB_VERSION}" "$ZLIB_SHA256"
    download_library "$XZ_URL" "xz" "xz-${XZ_VERSION}" "$XZ_SHA256"

    # Fix xz automake timestamp issue - touch generated files to prevent regeneration
    # xz 5.8.2 was built with automake 1.18.1 which may not be available on build systems
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

# Static library verification output file
export STATIC_LIBS_FILE="${STATIC_LIBS_FILE:-static-libs.txt}"

# Function to verify a static library and capture symbol information
# Usage: verify_static_lib <library_path> [nm_command]
verify_static_lib() {
    local lib="$1"
    local nm_cmd="${2:-${NM:-nm}}"
    local lib_name
    lib_name="$(basename "$lib")"

    echo "=== Verifying $lib_name ===" >> "$STATIC_LIBS_FILE"

    if [ ! -f "$lib" ]; then
        echo "ERROR: Library not found: $lib" >> "$STATIC_LIBS_FILE"
        echo "" >> "$STATIC_LIBS_FILE"
        return 1
    fi

    # Count symbols
    local defined
    local undefined
    defined=$($nm_cmd "$lib" 2>/dev/null | grep -c " [TtDdBbRr] " || echo 0)
    undefined=$($nm_cmd -u "$lib" 2>/dev/null | wc -l | tr -d ' ')

    echo "Defined symbols: $defined" >> "$STATIC_LIBS_FILE"
    echo "Undefined symbols: $undefined" >> "$STATIC_LIBS_FILE"

    # List undefined symbols (these get resolved at link time)
    echo "Undefined symbol sample:" >> "$STATIC_LIBS_FILE"
    $nm_cmd -u "$lib" 2>/dev/null | head -30 >> "$STATIC_LIBS_FILE"
    echo "" >> "$STATIC_LIBS_FILE"
}
