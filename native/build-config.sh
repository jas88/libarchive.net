#!/bin/bash
# Shared build configuration for libarchive native library builds
# Source this file in platform-specific build scripts

# Library versions
LIBARCHIVE_VERSION="3.8.8"
LZ4_VERSION="1.10.0"
ZSTD_VERSION="1.5.7"
LZO_VERSION="2.10"
LIBXML2_VERSION="2.15.3"
ZLIB_VERSION="1.3.2"
XZ_VERSION="5.8.3"
BZIP2_VERSION="1.0.8"

# SHA256 checksums for download verification
LIBARCHIVE_SHA256="3873a88801da067d0528a989af06877710529d50ee8fe6f3970cbb4302efb918"
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

# Normalise autotools timestamps for a freshly unpacked release tarball.
#
# Release tarballs ship a complete, self-consistent set of generated files
# (aclocal.m4, configure, config.h.in, Makefile.in, build-aux/ltmain.sh). If their
# mtimes are out of dependency order, make fires the autotools rebuild rules and
# demands the exact tool versions the tarball was built with - e.g.
# "automake-1.17: command not found".
#
# Rewriting the files instead (aclocal && automake && autoconf) is worse: it
# regenerates aclocal.m4 from the *host* libtool.m4 while leaving the tarball's
# ltmain.sh in place, so a host libtool newer than the tarball's fails with
# "libtool: Version mismatch error ... definition of this LT_INIT comes from".
#
# Stamping the files in dependency order keeps the tarball's own generated files
# and stops any rebuild rule from firing, so neither the host automake version
# nor the host libtool version can affect the build.
# Timestamp for stamping tier N, as a touch -t argument. Tiers are one minute
# apart: make compares mtimes at second granularity, so a whole minute between
# tiers leaves no room for two tiers to be seen as equal. The day itself is
# arbitrary - only the relative order matters - and is overridable for testing.
autotools_stamp_tier() {
    printf '%s%02d%02d' "${AUTOTOOLS_STAMP_DAY:-20200101}" "$(($1 / 60))" "$(($1 % 60))"
}

normalize_autotools_timestamps() {
    local dir="$1"
    [ -d "$dir" ] || return 0
    [ -f "$dir/configure" ] || return 0

    echo "Normalising autotools timestamps in ${dir}..."

    # Applied oldest -> newest. The ascending tier numbers are the contract: they
    # mirror the autotools dependency graph, so a new file class must be inserted
    # at the tier its dependencies require, not appended.
    #
    #   0 every input       the whole tree, see below
    #   1 aclocal.m4        depends on tier 0
    #   2 configure         depends on tiers 0-1
    #   3 config.h.in       depends on tiers 0-1, and must not predate configure
    #   4 Makefile.in       depends on tiers 0-1
    #
    # Tier 0 deliberately backdates the entire tree rather than an enumerated list of
    # source patterns. automake makes Makefile.in depend on every fragment that
    # Makefile.am includes, and those are not named predictably - xz's
    # src/liblzma/Makefile.am pulls in seven */Makefile.inc files, none of which match
    # a configure.ac/Makefile.am/*.m4 pattern. Missing one silently lets the rebuild
    # rule fire and reintroduces the host-automake dependency. Backdating everything
    # first means no input can outrank a generated file, whatever the package includes.
    #
    # touch -h stamps a symlink itself rather than its target. find does not follow
    # symlinks while walking, but touch dereferences by default, so without -h a
    # symlink pointing out of the tree would silently backdate a file elsewhere on the
    # system. None of the tarballs currently ship symlinks; -h keeps that from becoming
    # a problem if one ever does.
    find "$dir" -exec touch -h -t "$(autotools_stamp_tier 0)" {} +
    find "$dir" -name 'aclocal.m4' -exec touch -h -t "$(autotools_stamp_tier 1)" {} +
    find "$dir" -name 'configure'  -exec touch -h -t "$(autotools_stamp_tier 2)" {} +
    find "$dir" \( -name 'config.h.in' -o -name 'config.hin' -o -name '*.h.in' \) \
        -exec touch -h -t "$(autotools_stamp_tier 3)" {} +
    find "$dir" -name 'Makefile.in' -exec touch -h -t "$(autotools_stamp_tier 4)" {} +
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

    # Stop autotools rebuild rules firing in any unpacked tarball. Without this the
    # build depends on the host's automake/libtool versions matching the ones each
    # tarball was released with, which breaks whenever a runner image updates them.
    # libxml2 is excluded: it is built via its own autogen.sh, which deliberately
    # runs a full, self-consistent autoreconf (libtoolize included).
    normalize_autotools_timestamps "libarchive-${LIBARCHIVE_VERSION}"
    normalize_autotools_timestamps "xz-${XZ_VERSION}"
    normalize_autotools_timestamps "lzo-${LZO_VERSION}"
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
